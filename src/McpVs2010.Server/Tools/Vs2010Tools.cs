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

    [McpServerTool, Description("지정한 전체 경로의 솔루션 파일을 VS2010에서 엽니다. VS2010 Recent 목록은 사용하지 않습니다.")]
    [SupportedOSPlatform("windows")]
    public static Task<CallToolResult> open_vs2010_solution(
        [Description("열 솔루션 파일(.sln)의 전체 경로입니다.")] string solution_path,
        [Description("대상 devenv.exe 프로세스 ID. 인스턴스가 하나면 생략할 수 있습니다.")] int? processId = null,
        [Description("현재 열린 솔루션을 닫기 전에 저장할지 여부입니다. 기본값은 true입니다.")] bool saveCurrentSolution = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(solution_path))
                throw new ArgumentException("solution_path가 필요합니다.", nameof(solution_path));
            string fullPath = Path.GetFullPath(solution_path.Trim());
            if (!fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(".sln 솔루션 파일만 열 수 있습니다.", nameof(solution_path));
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("솔루션 파일을 찾을 수 없습니다.", fullPath);
            return ExecuteAsync(() => Client.OpenSolutionAsync(
                processId, fullPath, saveCurrentSolution, cancellationToken));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(ex));
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

    [McpServerTool, Description("현재 열려 있는 VS2010 솔루션의 모든 변경 내용을 저장한 후 솔루션을 닫습니다." )]
    public static Task<CallToolResult> close_vs2010_solution(
        [Description("대상 devenv.exe 프로세스 ID. 인스턴스가 하나면 생략할 수 있습니다.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => Client.CloseSolutionAsync(processId, cancellationToken));
    }

    [McpServerTool, Description("새 Visual Studio 2010 빈 솔루션을 생성합니다. 기존 솔루션은 저장 후 닫습니다.")]
    public static Task<CallToolResult> create_new_solution(
        [Description("새 솔루션 이름입니다. .sln 확장자는 자동으로 처리됩니다.")] string solutionName,
        [Description("솔루션 디렉터리 전체 경로입니다. 생략하면 MCP 서버의 현재 디렉터리를 사용합니다.")] string? solutionDirectory = null,
        [Description("대상 devenv.exe 프로세스 ID. 인스턴스가 하나면 생략할 수 있습니다.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => Client.CreateEmptySolutionAsync(processId, solutionName, solutionDirectory, cancellationToken));
    }

    [McpServerTool, Description("현재 VS2010 솔루션에서 프로젝트를 제거합니다. 프로젝트 파일은 삭제하지 않습니다.")]
    public static Task<CallToolResult> remove_project(
        [Description("제거할 프로젝트 이름, uniqueName 또는 프로젝트 파일 전체 경로입니다.")] string project,
        [Description("대상 devenv.exe 프로세스 ID. 인스턴스가 하나면 생략할 수 있습니다.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => Client.RemoveProjectAsync(processId, project, cancellationToken));
    }

    [McpServerTool, Description("Visual C++ 템플릿으로 새 프로젝트를 생성합니다.")]
    public static Task<CallToolResult> create_new_project(
        [Description("Visual C++ 템플릿(.vsz) 전체 경로입니다.")] string template,
        [Description("프로젝트 이름입니다.")] string projectName,
        [Description("프로젝트 생성 폴더 전체 경로입니다. 생략하면 현재 위치 아래에 프로젝트 이름 폴더를 자동으로 만듭니다.")] string? location = null,
        [Description("대상 devenv.exe 프로세스 ID입니다.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => Client.CreateNewProjectAsync(processId, template, projectName, location ?? string.Empty, null, false, false, false, false, false, cancellationToken));
    }

    [McpServerTool, Description("VS2010 프로젝트 생성 표준 API입니다. 현재 c/c++ 유형을 지원합니다.")]
    public static Task<CallToolResult> vs2010_create_project(
        [Description("프로젝트 이름입니다.")] string project_name,
        [Description("프로젝트 폴더 전체 경로입니다. 지정하면 해당 위치를 사용합니다. 생략하면 현재 위치 아래에 프로젝트 이름 폴더를 자동으로 만듭니다.")] string? project_folder_path = null,
        [Description("프로젝트 유형입니다. 현재 c/c++를 지원하며 향후 c# 등을 추가할 수 있습니다.")] string project_type = "c/c++",
        [Description("c/c++ option_1: Window Application, Console Application, Dynamic Library, Static Library 중 하나입니다. 생략하고 빈 프로젝트를 선택하면 Window Application Empty Project로 생성됩니다.")] string option_1 = "Window Application",
        [Description("option_2 Empty project 여부입니다. true이면 다른 option_2/3은 비활성화됩니다.")] bool empty_project = false,
        [Description("option_2 Export symbols 여부입니다.")] bool export_symbols = false,
        [Description("option_2 Precompiled Header 여부입니다.")] bool precompiled_header = false,
        [Description("option_3 ATL header 여부입니다.")] bool atl = false,
        [Description("option_3 MFC header 여부입니다.")] bool mfc = false,
        [Description("대상 devenv.exe 프로세스 ID입니다.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(project_type, "c/c++", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(project_type, "c++", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(project_type, "cpp", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Result("ERROR\r\n지원하지 않는 project_type입니다: " + project_type, true));
        string normalizedOption = RemoveWhitespace(option_1);
        // "Console Application"에는 application이라는 단어도 포함되므로
        // Windows/Application 판정보다 Console 판정을 먼저 해야 합니다.
        string applicationType;
        if (normalizedOption.IndexOf("console", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalizedOption.IndexOf("콘솔", StringComparison.OrdinalIgnoreCase) >= 0)
            applicationType = "Console";
        else if (normalizedOption == "dynamiclibrary" || normalizedOption == "dll" || normalizedOption == "dynamic")
            applicationType = "DynamicLibrary";
        else if (normalizedOption == "staticlibrary" || normalizedOption == "lib" || normalizedOption == "static")
            applicationType = "StaticLibrary";
        else if (normalizedOption == "window" || normalizedOption == "windows" ||
                 normalizedOption == "windowapplication" || normalizedOption == "windowsapplication" ||
                 normalizedOption == "application" || normalizedOption == "윈도우어플리케이션" ||
                 normalizedOption == "어플리케이션")
            applicationType = "Windows";
        else
            applicationType = "Windows";
        if (empty_project) { export_symbols = false; precompiled_header = false; atl = false; mfc = false; }
        else if (!precompiled_header) precompiled_header = true;
        return ExecuteAsync(() => Client.CreateNewProjectAsync(
            processId,
            "C:\\Program Files (x86)\\Microsoft Visual Studio 10.0\\VC\\vcprojects\\Win32Wiz.vsz",
            project_name,
            project_folder_path ?? string.Empty,
            applicationType,
            empty_project,
            export_symbols,
            precompiled_header,
            atl,
            mfc,
            cancellationToken));
    }

    private static string RemoveWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var buffer = new char[value.Length];
        int count = 0;
        for (int index = 0; index < value.Length; index++)
            if (!char.IsWhiteSpace(value[index])) buffer[count++] = value[index];
        return new string(buffer, 0, count);
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
