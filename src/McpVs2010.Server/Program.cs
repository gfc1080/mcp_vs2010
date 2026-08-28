using Microsoft.Win32;
using ModelContextProtocol.Server;
using McpVs2010.Server;

const string RegistryPath = @"Software\McpVs2010";
const string RegistryPortValue = "HttpStreamPort";
const int DefaultHttpStreamPort = 3010;
const string McpEndpoint = "/stream";
const string HealthEndpoint = "/health";

using var serverMutex = new Mutex(true, "Global\\McpVs2010.Server", out bool isPrimaryServer);
if (!isPrimaryServer)
{
    return;
}

int httpStreamPort = LoadHttpStreamPort(DefaultHttpStreamPort);
var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls($"http://127.0.0.1:{httpStreamPort}");
}

builder.Services.AddHostFiltering(options =>
{
    options.AllowedHosts = ["127.0.0.1", "localhost", "[::1]"];
});

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        // 현재 도구는 요청 사이의 MCP 세션 상태나 서버 발신 요청을 사용하지 않는다.
        options.Stateless = true;
    })
    .WithToolsFromAssembly();

var app = builder.Build();
ServerTray.Start(app.Lifetime);

app.UseHostFiltering();
app.Use(async (context, next) =>
{
    if (!ServerRuntimeState.HttpEnabled)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsync("MCP HTTP server is stopped.");
        return;
    }

    if (context.Request.Headers.TryGetValue("Origin", out var origins) &&
        (origins.Count != 1 || !IsLoopbackOrigin(origins[0])))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Origin is not allowed.");
        return;
    }

    await next();
});

app.MapMcp(McpEndpoint);
app.MapGet(HealthEndpoint, () => Results.Ok("MCP VS2010 is running."));
await app.RunAsync();

static int LoadHttpStreamPort(int defaultPort)
{
    using var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32)
        .OpenSubKey(RegistryPath, false);
    object? value = key?.GetValue(RegistryPortValue);
    if (value == null) return defaultPort;
    if (value is int port && port is >= 1 and <= 65535) return port;
    throw new InvalidOperationException($"Registry value {RegistryPath}\\{RegistryPortValue} must be an integer from 1 to 65535.");
}

static bool IsLoopbackOrigin(string? origin)
{
    return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
           uri.IsLoopback;
}

internal static class ServerRuntimeState
{
    private static int _httpEnabled = 1;
    public static bool HttpEnabled => Volatile.Read(ref _httpEnabled) != 0;
    public static void SetHttpEnabled(bool enabled) => Volatile.Write(ref _httpEnabled, enabled ? 1 : 0);
}
