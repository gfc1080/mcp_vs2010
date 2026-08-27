using System.Diagnostics;
using System.Text.Json;

namespace McpVs2010.Server.Bridge;

internal sealed class InstanceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;

    public InstanceRegistry(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "McpVs2010",
            "instances");
    }

    public IReadOnlyList<BridgeInstance> List()
    {
        if (!Directory.Exists(_directory))
        {
            return Array.Empty<BridgeInstance>();
        }

        var instances = new List<BridgeInstance>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var instance = JsonSerializer.Deserialize<BridgeInstance>(File.ReadAllText(file), JsonOptions);
                if (instance is null || instance.ProtocolVersion != 3 || instance.ProcessId <= 0 ||
                    string.IsNullOrWhiteSpace(instance.PipeName))
                {
                    continue;
                }

                if (!IsLiveVisualStudioProcess(instance.ProcessId))
                {
                    TryDelete(file);
                    continue;
                }

                instance.DiscoveryFilePath = file;
                instances.Add(instance);
            }
            catch (JsonException)
            {
                // 부분 기록 또는 다른 버전의 파일은 무시한다.
            }
            catch (IOException)
            {
                // VSIX가 파일을 갱신하는 순간일 수 있으므로 다음 호출에서 재시도한다.
            }
            catch (UnauthorizedAccessException)
            {
                // 접근할 수 없는 레코드는 노출하지 않는다.
            }
        }

        return instances.OrderBy(instance => instance.ProcessId).ToArray();
    }

    public BridgeInstance Resolve(int? processId)
    {
        var instances = List();
        if (processId.HasValue)
        {
            var selected = instances.FirstOrDefault(instance => instance.ProcessId == processId.Value);
            return selected ?? throw new InvalidOperationException(
                $"PID {processId.Value}에 연결된 VS2010 MCP 브리지를 찾을 수 없습니다.");
        }

        return instances.Count switch
        {
            0 => throw new InvalidOperationException(
                "실행 중인 VS2010 MCP 브리지를 찾을 수 없습니다. VSIX 설치 후 Visual Studio 2010을 실행하십시오."),
            1 => instances[0],
            _ => throw new InvalidOperationException(
                "여러 VS2010 인스턴스가 실행 중입니다. list_vs2010_instances 결과의 processId를 지정하십시오.")
        };
    }

    private static bool IsLiveVisualStudioProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && string.Equals(process.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
