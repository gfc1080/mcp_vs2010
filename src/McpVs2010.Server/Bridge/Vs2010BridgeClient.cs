using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace McpVs2010.Server.Bridge;

internal sealed class Vs2010BridgeClient
{
    private static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly InstanceRegistry _registry = new();

    public string ListInstances()
    {
        return JsonSerializer.Serialize(_registry.List(), DisplayJsonOptions);
    }

    public Task<string> GetStateAsync(int? processId, CancellationToken cancellationToken)
    {
        return CallAsync(processId, new BridgeRequest { Method = "get_state" }, cancellationToken);
    }

    public Task<string> OpenSolutionAsync(
        int? processId,
        string solutionPath,
        bool saveCurrentSolution,
        CancellationToken cancellationToken)
    {
        return CallAsync(processId, new BridgeRequest
        {
            Method = "open_solution",
            SolutionPath = solutionPath,
            SaveCurrentSolution = saveCurrentSolution
        }, cancellationToken);
    }

    public Task<string> RunSolutionOperationAsync(
        int? processId,
        string? operation,
        string? configuration,
        string? platform,
        CancellationToken cancellationToken)
    {
        return RunBuildOperationAsync(
            processId,
            "solution",
            operation,
            null,
            configuration,
            platform,
            cancellationToken);
    }

    public Task<string> RunProjectOperationAsync(
        int? processId,
        string project,
        string? operation,
        string? configuration,
        string? platform,
        CancellationToken cancellationToken)
    {
        return RunBuildOperationAsync(
            processId,
            "project",
            operation,
            project,
            configuration,
            platform,
            cancellationToken);
    }

    private async Task<string> RunBuildOperationAsync(
        int? processId,
        string scope,
        string? operation,
        string? project,
        string? configuration,
        string? platform,
        CancellationToken cancellationToken)
    {
        var instance = _registry.Resolve(processId);
        try
        {
            return await SendAsync(instance, new BridgeRequest
            {
                Method = "run_build_operation",
                Scope = scope,
                Operation = NullIfWhiteSpace(operation) ?? "build",
                Project = NullIfWhiteSpace(project),
                Configuration = NullIfWhiteSpace(configuration),
                Platform = NullIfWhiteSpace(platform)
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // MCP 호출이 취소되면 가능한 경우 VS 빌드 취소도 요청한다.
            using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await SendAsync(instance, new BridgeRequest { Method = "cancel_build" }, cancelTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 원래의 취소 예외를 보존한다.
            }

            throw;
        }
    }

    public Task<string> CancelBuildAsync(int? processId, CancellationToken cancellationToken)
    {
        return CallAsync(processId, new BridgeRequest { Method = "cancel_build" }, cancellationToken);
    }

    private Task<string> CallAsync(int? processId, BridgeRequest request, CancellationToken cancellationToken)
    {
        return SendAsync(_registry.Resolve(processId), request, cancellationToken);
    }

    private static async Task<string> SendAsync(
        BridgeInstance instance,
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            instance.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(5_000, cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };

        var requestJson = JsonSerializer.Serialize(request, WireJsonOptions);
        await writer.WriteLineAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);

        var responseLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new IOException($"VS2010 브리지(PID {instance.ProcessId})가 응답 없이 연결을 종료했습니다.");
        }

        var response = JsonSerializer.Deserialize<BridgeResponse>(responseLine, WireJsonOptions)
                       ?? throw new IOException("VS2010 브리지 응답을 JSON으로 해석할 수 없습니다.");

        if (!string.Equals(request.Id, response.Id, StringComparison.Ordinal))
        {
            throw new IOException("VS2010 브리지 응답 ID가 요청 ID와 일치하지 않습니다.");
        }

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error ?? "VS2010 브리지가 원인을 제공하지 않고 실패했습니다.");
        }

        return FormatResultJson(response.ResultJson);
    }

    private static string FormatResultJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "null";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, DisplayJsonOptions);
        }
        catch (JsonException)
        {
            // 브리지가 보낸 원문을 손실하거나 바꾸지 않는다.
            return json;
        }
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
