using System.ComponentModel;
using System.Runtime.Versioning;
using McpVs2010.Server.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpVs2010.Server.Tools;

[McpServerToolType]
public static class Vs2010Tools
{
    private static readonly Vs2010BridgeClient Client = new();

    [McpServerTool, Description("실행 중이며 MCP VSIX 브리지가 로드된 Visual Studio 2010 인스턴스를 나열합니다.")]
    public static CallToolResult list_vs2010_instances()
    {
        try
        {
            return Success(Client.ListInstances());
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool, Description("VS2010의 최근 프로젝트 및 솔루션 목록을 최근 사용 순서로 조회합니다.")]
    [SupportedOSPlatform("windows")]
    public static CallToolResult list_vs2010_recent_projects()
    {
        try
        {
            return Success(Vs2010RecentProjectsReader.ReadAsJson());
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool, Description("VS2010의 최근 목록에서 지정한 순번의 솔루션을 엽니다. 현재 솔루션이 다르면 저장 후 닫습니다.")]
    [SupportedOSPlatform("windows")]
    public static Task<CallToolResult> open_vs2010_recent_solution(
        [Description("VS2010 최근 프로젝트/솔루션 목록의 순번. 기본값은 가장 최근인 1입니다.")] int position = 1,
        [Description("대상 devenv.exe 프로세스 ID. 인스턴스가 하나면 생략할 수 있습니다.")] int? processId = null,
        [Description("현재 열린 솔루션을 닫기 전에 저장할지 여부. 기본값은 true입니다.")] bool saveCurrentSolution = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solutionPath = Vs2010RecentProjectsReader.GetSolutionPath(position);
            return ExecuteAsync(() => Client.OpenSolutionAsync(
                processId,
                solutionPath,
                saveCurrentSolution,
                cancellationToken));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(ex));
        }
    }

    [McpServerTool, Description("VS2010 인스턴스의 열린 솔루션, 활성 구성, 프로젝트와 빌드 상태를 조회합니다.")]
    public static Task<CallToolResult> get_vs2010_state(
        [Description("대상 devenv.exe 프로세스 ID. 인스턴스가 하나면 생략할 수 있습니다.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => Client.GetStateAsync(processId, cancellationToken));
    }

    [McpServerTool, Description("VS2010 IDE에서 솔루션 전체 Clean, Build 또는 Rebuild를 실행합니다. 설치된 외부 플러그인은 VS2010이 평소와 동일하게 처리합니다.")]
    public static Task<CallToolResult> build_vs2010_solution(
        [Description("대상 devenv.exe 프로세스 ID. 인스턴스가 하나면 생략할 수 있습니다.")] int? processId = null,
        [Description("선택할 솔루션 구성 이름. 생략하면 현재 활성 구성을 사용합니다.")] string? configuration = null,
        [Description("선택할 플랫폼 이름. 생략하면 현재 활성 플랫폼을 사용합니다.")] string? platform = null,
        [Description("솔루션 작업. clean, build, rebuild 중 하나이며 기본값은 build입니다.")] string operation = "build",
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => Client.RunSolutionOperationAsync(
            processId,
            operation,
            configuration,
            platform,
            cancellationToken));
    }

    [McpServerTool, Description("VS2010 C++ Project Only의 Clean Only, Build Only 또는 Rebuild Only 명령을 실행합니다. 프로젝트 의존성은 함께 작업하지 않습니다.")]
    public static Task<CallToolResult> build_vs2010_project(
        [Description("대상 프로젝트의 이름, UniqueName 또는 프로젝트 파일 전체 경로.")] string project,
        [Description("Project Only 작업. clean, build, rebuild 중 하나이며 기본값은 build입니다.")] string operation = "build",
        [Description("대상 devenv.exe 프로세스 ID. 인스턴스가 하나면 생략할 수 있습니다.")] int? processId = null,
        [Description("선택할 솔루션 구성 이름. 생략하면 현재 활성 구성을 사용합니다.")] string? configuration = null,
        [Description("선택할 플랫폼 이름. 생략하면 현재 활성 플랫폼을 사용합니다.")] string? platform = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => Client.RunProjectOperationAsync(
            processId,
            project,
            operation,
            configuration,
            platform,
            cancellationToken));
    }

    [McpServerTool, Description("진행 중인 VS2010 빌드에 Build.Cancel 명령을 보냅니다.")]
    public static Task<CallToolResult> cancel_vs2010_build(
        [Description("대상 devenv.exe 프로세스 ID. 인스턴스가 하나면 생략할 수 있습니다.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => Client.CancelBuildAsync(processId, cancellationToken));
    }

    private static async Task<CallToolResult> ExecuteAsync(Func<Task<string>> action)
    {
        try
        {
            return Success(await action().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    private static CallToolResult Success(string text)
    {
        return Result(text, false);
    }

    private static CallToolResult Error(Exception exception)
    {
        return Result(exception.Message, true);
    }

    private static CallToolResult Result(string text, bool isError)
    {
        return new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = text }
            },
            IsError = isError
        };
    }
}
