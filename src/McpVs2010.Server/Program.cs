using System.Diagnostics;
using Microsoft.Win32;
using ModelContextProtocol.Server;

const string RegistryPath = @"Software\McpVs2010";
const string RegistryPortValue = "HttpStreamPort";
const int DefaultHttpStreamPort = 3010;
const string McpEndpoint = "/stream";

int httpStreamPort = LoadHttpStreamPort(DefaultHttpStreamPort);
int? parentProcessId = LoadParentProcessId(args);
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

if (parentProcessId.HasValue)
{
    _ = MonitorParentProcessAsync(app.Lifetime, parentProcessId.Value);
}

app.UseHostFiltering();
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue("Origin", out var origins) &&
        (origins.Count != 1 || !IsLoopbackOrigin(origins[0])))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("허용되지 않은 Origin입니다.");
        return;
    }

    await next();
});

app.MapMcp(McpEndpoint);
await app.RunAsync();

static int LoadHttpStreamPort(int defaultPort)
{
    using var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32)
        .OpenSubKey(RegistryPath, false);
    object? value = key?.GetValue(RegistryPortValue);
    if (value == null) return defaultPort;
    if (value is int port && port is >= 1 and <= 65535) return port;
    throw new InvalidOperationException($"레지스트리 {RegistryPath}\\{RegistryPortValue} 값은 1~65535 정수여야 합니다.");
}

static int? LoadParentProcessId(string[] args)
{
    for (int index = 0; index < args.Length; index++)
    {
        if (!string.Equals(args[index], "--parent-process-id", StringComparison.OrdinalIgnoreCase))
            continue;

        if (index + 1 >= args.Length ||
            !int.TryParse(args[index + 1], out int parentProcessId) ||
            parentProcessId <= 0)
        {
            throw new InvalidOperationException("--parent-process-id 값이 올바른 프로세스 ID가 아닙니다.");
        }

        return parentProcessId;
    }

    return null;
}

static async Task MonitorParentProcessAsync(IHostApplicationLifetime lifetime, int parentProcessId)
{
    while (!lifetime.ApplicationStopping.IsCancellationRequested)
    {
        bool parentAlive;
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            parentAlive = !parent.HasExited &&
                string.Equals(parent.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            parentAlive = false;
        }
        catch (InvalidOperationException)
        {
            parentAlive = false;
        }

        if (!parentAlive)
        {
            lifetime.StopApplication();
            return;
        }

        try
        {
            await Task.Delay(1000, lifetime.ApplicationStopping);
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }
}

static bool IsLoopbackOrigin(string? origin)
{
    return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
           uri.IsLoopback;
}
