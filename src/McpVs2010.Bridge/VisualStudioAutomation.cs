using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using EnvDTE;
using EnvDTE80;
using McpVs2010.Bridge.Protocol;
using Microsoft.VisualStudio.VCProjectEngine;
using Microsoft.VisualStudio.Shell.Interop;




static class NativeDebug
{
  [DllImport("kernel32.dll",
      CharSet = CharSet.Unicode)]
  static extern void OutputDebugString(
      string lpOutputString);

  public static void WriteLine(string text)
  {
    OutputDebugString(text + "\r\n");
  }
}

namespace McpVs2010.Bridge
{

  internal sealed class VisualStudioAutomation
  {
    private const string BuildOutputPaneGuid = "{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}";
    private const string SolutionFolderProjectKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
    private const string VisualCppProjectKind = "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";
    private static readonly TimeSpan BuildCompletionTimeout = TimeSpan.FromHours(2);
    private readonly DTE2 _dte;
    private readonly IVsSolution _solutionService;
    private readonly IVsShell _shellService;
    private readonly Dispatcher _dispatcher;
    private readonly object _buildGate = new object();
    private readonly object _solutionGate = new object();

    public VisualStudioAutomation(DTE2 dte, IVsSolution solutionService, IVsShell shellService)
    {
      _dte = dte;
      _solutionService = solutionService;
      _shellService = shellService;
      _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public VisualStudioState GetState()
    {
      return OnUiThread(CaptureState);
    }

    public OpenSolutionResult OpenSolution(BridgeRequest request)
    {
      if (!Monitor.TryEnter(_solutionGate))
      {
        throw new InvalidOperationException("이 VS2010 인스턴스에서 이미 솔루션 열기 요청이 실행 중입니다.");
      }

      try
      {
        // 패키지는 VS 시작 중 일찍 로드될 수 있다. 셸이 idle 상태가 된 뒤
        // VS SDK의 IVsSolution 서비스를 호출해야 시작 단계의 재진입을 피할 수 있다.
        return OnUiThreadAtIdle(delegate
        {
          EnsureShellInitialized();

          string requestedPath = string.IsNullOrWhiteSpace(request.SolutionPath)
                      ? null
                      : request.SolutionPath.Trim();
          if (string.IsNullOrEmpty(requestedPath))
          {
            throw new InvalidOperationException("열 솔루션 경로가 비어 있습니다.");
          }

          string solutionPath;
          try
          {
            solutionPath = Path.GetFullPath(requestedPath);
          }
          catch (Exception ex)
          {
            throw new InvalidOperationException("솔루션 경로가 올바르지 않습니다: " + requestedPath, ex);
          }

          if (!string.Equals(Path.GetExtension(solutionPath), ".sln", StringComparison.OrdinalIgnoreCase))
          {
            throw new InvalidOperationException("VS2010 솔루션(.sln) 파일이 아닙니다: " + solutionPath);
          }
          if (!File.Exists(solutionPath))
          {
            throw new FileNotFoundException("열 솔루션 파일이 존재하지 않습니다.", solutionPath);
          }

          string currentPath = null;
          if (_dte.Solution != null && _dte.Solution.IsOpen)
          {
            currentPath = EmptyToNull(_dte.Solution.FullName);
            if (PathsEqual(currentPath, solutionPath))
            {
              return new OpenSolutionResult
              {
                Success = true,
                OpenedSolutionPath = currentPath,
                SolutionName = Path.GetFileName(currentPath),
                SavedCurrentSolution = false,
                WasAlreadyOpen = true
              };
            }

            if (_dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
            {
              throw new InvalidOperationException(
                          "VS2010에서 빌드가 진행 중이므로 현재 솔루션을 닫을 수 없습니다: " + currentPath);
            }
          }

          bool saveCurrentSolution = request.SaveCurrentSolution.GetValueOrDefault(true);
          if (!string.IsNullOrEmpty(currentPath))
          {
            uint closeOptions = (uint)(saveCurrentSolution
                        ? __VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty
                        : __VSSLNSAVEOPTIONS.SLNSAVEOPT_NoSave);
            ThrowOnFailure(_solutionService.CloseSolutionElement(closeOptions, null, 0));
            if (_dte.Solution.IsOpen)
            {
              throw new InvalidOperationException("현재 솔루션을 닫지 못했습니다: " + currentPath);
            }
          }

          ThrowOnFailure(_solutionService.OpenSolutionFile(0, solutionPath));
          if (!_dte.Solution.IsOpen || !PathsEqual(_dte.Solution.FullName, solutionPath))
          {
            throw new InvalidOperationException("요청한 솔루션을 열지 못했습니다: " + solutionPath);
          }

          return new OpenSolutionResult
          {
            Success = true,
            ClosedSolutionPath = currentPath,
            OpenedSolutionPath = _dte.Solution.FullName,
            SolutionName = Path.GetFileName(_dte.Solution.FullName),
            SavedCurrentSolution = !string.IsNullOrEmpty(currentPath) && saveCurrentSolution,
            WasAlreadyOpen = false
          };
        });
      }
      finally
      {
        Monitor.Exit(_solutionGate);
      }
    }

    public CloseSolutionResult SaveAndCloseSolution()
    {
      return OnUiThread(delegate
      {
        if (_dte.Solution == null || !_dte.Solution.IsOpen)
          return new CloseSolutionResult { ClosedSolutionPath = null, Saved = false };

        string currentPath = EmptyToNull(_dte.Solution.FullName);
        _dte.ExecuteCommand("File.SaveAll", string.Empty);
        ThrowOnFailure(_solutionService.CloseSolutionElement(
                  (uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0));
        if (_dte.Solution.IsOpen)
          throw new InvalidOperationException("The current solution could not be closed.");

        return new CloseSolutionResult { ClosedSolutionPath = currentPath, Saved = true };
      });
    }

    public CreateSolutionResult CreateEmptySolution(BridgeRequest request)
    {
      return OnUiThreadAtIdle(delegate
      {
        string name = string.IsNullOrWhiteSpace(request.SolutionName) ? null : request.SolutionName.Trim();
        if (string.IsNullOrEmpty(name))
          throw new InvalidOperationException("솔루션 이름이 필요합니다.");
        if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
          name = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
          throw new InvalidOperationException("솔루션 이름이 올바르지 않습니다.");

        string directory = string.IsNullOrWhiteSpace(request.SolutionDirectory)
                  ? Environment.CurrentDirectory
                  : request.SolutionDirectory.Trim();
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory))
          Directory.CreateDirectory(directory);

        string currentPath = null;
        if (_dte.Solution != null && _dte.Solution.IsOpen)
        {
          currentPath = EmptyToNull(_dte.Solution.FullName);
          if (_dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
            throw new InvalidOperationException("빌드가 진행 중이므로 현재 솔루션을 닫을 수 없습니다.");
          ThrowOnFailure(_solutionService.CloseSolutionElement(
                    (uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0));
          if (_dte.Solution.IsOpen)
            throw new InvalidOperationException("현재 솔루션을 닫지 못했습니다.");
        }

        _dte.Solution.Create(directory, name);
        if (!_dte.Solution.IsOpen)
          throw new InvalidOperationException("빈 솔루션을 생성하지 못했습니다.");
        AddX64SolutionConfigurations();
        string solutionPath = EmptyToNull(_dte.Solution.FullName);
        if (!string.IsNullOrWhiteSpace(solutionPath))
          _dte.Solution.SaveAs(solutionPath);
        return new CreateSolutionResult
        {
          SolutionName = name,
          SolutionPath = solutionPath,
          SolutionDirectory = directory,
          ClosedSolutionPath = currentPath
        };
      });
    }

    private void AddX64SolutionConfigurations()
    {
      SolutionConfigurations configurations = _dte.Solution.SolutionBuild.SolutionConfigurations;
      var names = new List<string>();
      for (int index = 1; index <= configurations.Count; index++)
        names.Add(configurations.Item(index).Name);

      foreach (string name in names)
      {
        bool exists = false;
        for (int index = 1; index <= configurations.Count; index++)
        {
          SolutionConfiguration2 item = configurations.Item(index) as SolutionConfiguration2;
          if (item != null && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(item.PlatformName, "x64", StringComparison.OrdinalIgnoreCase))
          {
            exists = true;
            break;
          }
        }
        if (!exists)
        {
          try
          {
            configurations.Add(name, "x64", true);
          }
          catch (Exception ex)
          {
            Debug.WriteLine("솔루션 x64 구성 추가를 건너뜁니다: " + ex.Message);
          }
        }
      }
    }

    public RemoveProjectResult RemoveProject(BridgeRequest request)
    {
      return OnUiThreadAtIdle(delegate
      {
        EnsureSolutionOpen();
        if (string.IsNullOrWhiteSpace(request.Project))
          throw new InvalidOperationException("제거할 프로젝트 이름 또는 경로가 필요합니다.");
        Project project = ResolveProject(request.Project.Trim());
        string name = project.Name;
        string path = EmptyToNull(project.FullName);
        _dte.Solution.Remove(project);
        return new RemoveProjectResult
        {
          ProjectName = name,
          ProjectPath = path,
          SolutionPath = EmptyToNull(_dte.Solution.FullName),
          FilesDeleted = false
        };
      });
    }

    private static bool HasPlatform(ConfigurationManager cm, string platformName)
    {
      Array platforms = (Array)cm.PlatformNames;
      foreach (object o in platforms)
      {
        string name = o as string;
        if (string.Equals(name, platformName, StringComparison.OrdinalIgnoreCase))
        {
          return true;
        }
      }
      return false;
    }
    private static bool IsSupportedPlatform(ConfigurationManager cm, string platformName)
    {
      Array platforms = (Array)cm.SupportedPlatforms;
      foreach (object o in platforms)
      {
        string name = o as string;
        if (string.Equals(name, platformName, StringComparison.OrdinalIgnoreCase))
        {
          return true;
        }
      }
      return false;
    }

    static readonly HashSet<string> DynamicTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
        "dll",
        "dynamic library",
        "dynamiclibrary",
        "dynamic"
        };
    static readonly HashSet<string> StaticTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
        "lib",
        "static library",
        "staticlibrary",
        "static"
        };
    static readonly HashSet<string> ConsoleTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
        "console",
        "console application",
        "consoleapplication"
        };
    static bool IsDynamicLibrary(string value)
    {
      return DynamicTypes.Contains(value);
    }
    static bool IsStaticLibrary(string value)
    {
      return StaticTypes.Contains(value);
    }
    static bool IsConsoleApplication(string value)
    {
      return ConsoleTypes.Contains(value);
    }

    static string ToSafeWizardIdentifier(string value)
    {
      string source = string.IsNullOrWhiteSpace(value) ? "PROJECT" : value.Trim();
      char[] chars = source.ToUpperInvariant().ToCharArray();
      for (int i = 0; i < chars.Length; i++)
      {
        char c = chars[i];
        if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
          chars[i] = '_';
      }
      if (chars.Length == 0)
        return "PROJECT";
      if (chars[0] >= '0' && chars[0] <= '9')
        return "_" + new string(chars);
      return new string(chars);
    }


    public CreateProjectResult CreateNewProject(BridgeRequest request)
    {
      return OnUiThreadAtIdle(delegate
      {
        if (string.IsNullOrWhiteSpace(request.ProjectName))
        {
          throw new InvalidOperationException("프로젝트 이름이 필요합니다.");
        }
        // 기본 위치는 현재 솔루션 폴더(솔루션이 없으면 VS 프로세스의 현재 폴더) 아래의
        // 프로젝트 이름 폴더입니다. 명시적인 Location은 그대로 사용합니다.
        bool hasOpenSolution = _dte.Solution != null && _dte.Solution.IsOpen;
        string baseLocation;
        if (hasOpenSolution && !string.IsNullOrWhiteSpace(_dte.Solution.FullName))
        {
          baseLocation = Path.GetDirectoryName(Path.GetFullPath(_dte.Solution.FullName));
        }
        else
        {
          baseLocation = Environment.CurrentDirectory;
        }
        string location = string.IsNullOrWhiteSpace(request.Location)
                  ? Path.Combine(baseLocation, request.ProjectName.Trim())
                  : Path.GetFullPath(request.Location);
        if (!Directory.Exists(location))
        {
          Directory.CreateDirectory(location);
        }
        if (!hasOpenSolution)
        {
          _dte.Solution.Create(location, request.ProjectName);
        }


        string applicationType = string.IsNullOrWhiteSpace(request.ApplicationType)
            ? "Windows"
            : request.ApplicationType.Trim();
        bool isDll = IsDynamicLibrary(applicationType);
        bool isLib = IsStaticLibrary(applicationType);
        bool isConsole = IsConsoleApplication(applicationType);
        bool isWindows = !isDll && !isLib && !isConsole;
        bool emptyProject = request.EmptyProject == true;
        bool exportSymbols = !emptyProject && request.ExportSymbols == true;
        bool precompiledHeader = !emptyProject && request.PrecompiledHeader != false;
        bool supportAtl = !emptyProject && request.Atl == true;
        bool supportMfc = !emptyProject && request.Mfc == true;
        string emptyTemplatePath = FindVs2010EmptyProjectTemplate();

        Project project;

        string expectedProjectPath = Path.Combine(location, request.ProjectName + ".vcxproj");
        try
        {
          project = _dte.Solution.AddFromTemplate(emptyTemplatePath, location, request.ProjectName, false);
        }
        catch (Exception ex)
        {
          Debug.WriteLine("프로젝트 생성 실패: " + ex.ToString());
          throw new InvalidOperationException("프로젝트를 생성하지 못했습니다.", ex);
        }
        finally
        {
          try
          {
            NativeDebug.WriteLine("location: " + location + "\r\n");
            NativeDebug.WriteLine("ProjectName: " + request.ProjectName + "\r\n");
            NativeDebug.WriteLine("emptyTemplatePath: " + emptyTemplatePath + "\r\n");
          }
          catch
          {
          }
        }
        // VS2010의 C++ AddFromTemplate은 프로젝트를 솔루션에 추가한 뒤에도
        // 반환값을 null로 돌려주는 경우가 있습니다. 반환값만 신뢰하지 말고
        // 실제 솔루션 프로젝트 목록에서 생성된 vcxproj를 다시 찾습니다.
        if (project == null)
        {
          project = FindSolutionProject(expectedProjectPath, request.ProjectName);
        }
        if (project == null)
        {
          throw new InvalidOperationException(
              "프로젝트가 솔루션에 추가되지 않았습니다: " + expectedProjectPath);
        }

        // x64 플랫폼이 없는 경우 추가합니다. 
        ConfigurationManager cm = project.ConfigurationManager;
        if (!HasPlatform(cm, "x64"))
        {
          if (!IsSupportedPlatform(cm, "x64"))
          {
            throw new InvalidOperationException("Visual Studio가 x64 플랫폼을 지원하지 않습니다.");
          }
          cm.AddPlatform("x64", "Win32", false);
        }

        // emptyproj.vsz로 프로젝트 컨테이너를 만든 뒤, VS2010 Generic
        // Application 템플릿의 Templates.inf를 직접 해석하여 소스 파일을
        // 복사합니다. VS Wizard 반환값/내부 대화상자에 의존하지 않습니다.
        if (!emptyProject)
        {
          PopulateTemplateFiles(project, location, request.ProjectName,
              applicationType, precompiledHeader, exportSymbols, supportAtl, supportMfc);
        }

        ApplyConfigurationType(project, applicationType, supportAtl, supportMfc, emptyProject, precompiledHeader, exportSymbols, isConsole, isDll, isLib);
        string generatedPath = project == null ? Path.Combine(location, request.ProjectName + ".vcxproj") : project.FullName;

        string solutionPathToSave = EmptyToNull(_dte.Solution.FullName);
        if (string.IsNullOrWhiteSpace(solutionPathToSave))
          throw new InvalidOperationException("생성된 솔루션의 저장 경로를 확인할 수 없습니다.");
        _dte.Solution.SaveAs(solutionPathToSave);

        return new CreateProjectResult
        {
          ProjectName = request.ProjectName,
          ProjectPath = generatedPath,
          SolutionPath = EmptyToNull(_dte.Solution.FullName)
        };
      });
    }



    private Project FindSolutionProject(string expectedProjectPath, string projectName)
    {
      string fullExpectedPath = Path.GetFullPath(expectedProjectPath);
      foreach (Project candidate in _dte.Solution.Projects)
      {
        try
        {
          if (!string.IsNullOrWhiteSpace(candidate.FullName) &&
              string.Equals(Path.GetFullPath(candidate.FullName), fullExpectedPath,
                  StringComparison.OrdinalIgnoreCase))
            return candidate;
          if (string.Equals(candidate.Name, projectName, StringComparison.OrdinalIgnoreCase))
            return candidate;
        }
        catch (Exception ex)
        {
          Debug.WriteLine("솔루션 프로젝트 확인 실패: " + ex.Message);
        }
      }
      return null;
    }

    private void PopulateTemplateFiles(Project project, string location, string projectName,
        string applicationType, bool precompiledHeader, bool exportSymbols,
        bool supportAtl, bool supportMfc)
    {
      string templatesDirectory = FindVs2010ApplicationTemplatesDirectory();
      string infPath = Path.Combine(templatesDirectory, "Templates.inf");
      if (!File.Exists(infPath))
        throw new FileNotFoundException("VS2010 Application Templates.inf를 찾을 수 없습니다.", infPath);

      bool isDll = IsDynamicLibrary(applicationType);
      bool isLib = IsStaticLibrary(applicationType);
      bool isConsole = IsConsoleApplication(applicationType);
      bool isWin = !isDll && !isLib && !isConsole;
      var symbols = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
      {
        { "DLL_APP", isDll }, { "LIB_APP", isLib }, { "CONSOLE_APP", isConsole },
        { "WIN_APP", isWin }, { "EMPTY_PROJECT", false },
        { "PRE_COMPILED_HEADER", precompiledHeader }, { "EXPORT_SYMBOLS", exportSymbols },
        { "SUPPORT_ATL", supportAtl }, { "SUPPORT_MFC", supportMfc }
      };
      var active = new Stack<bool>();
      bool include = true;
      foreach (string rawLine in File.ReadAllLines(infPath, Encoding.Default))
      {
        string line = rawLine.Trim();
        if (line.Length == 0) continue;
        if (line.StartsWith("[!if ", StringComparison.OrdinalIgnoreCase) && line.EndsWith("]"))
        {
          string expression = line.Substring(5, line.Length - 6).Trim();
          bool value = EvaluateTemplateExpression(expression, symbols);
          active.Push(include);
          include = include && value;
          continue;
        }
        if (line.Equals("[!else]", StringComparison.OrdinalIgnoreCase))
        {
          if (active.Count == 0) throw new InvalidOperationException("Templates.inf의 !else 위치가 잘못되었습니다.");
          bool parent = active.Peek();
          include = parent && !include;
          continue;
        }
        if (line.Equals("[!endif]", StringComparison.OrdinalIgnoreCase))
        {
          if (active.Count == 0) throw new InvalidOperationException("Templates.inf의 !endif 위치가 잘못되었습니다.");
          include = active.Pop();
          continue;
        }
        if (!include) continue;

        string sourceName = line;
        bool copyOnly = false;
        int separator = line.IndexOf('|');
        if (separator >= 0)
        {
          copyOnly = line.Substring(0, separator).Trim().Equals("CopyOnly", StringComparison.OrdinalIgnoreCase);
          sourceName = line.Substring(separator + 1).Trim();
        }
        string outputName = ReplaceTemplateName(sourceName, projectName);
        string sourcePath = Path.Combine(templatesDirectory, sourceName);
        string outputPath = Path.Combine(location, outputName);
        if (!File.Exists(sourcePath))
          throw new FileNotFoundException("템플릿 파일을 찾을 수 없습니다.", sourcePath);
        Directory.CreateDirectory(location);
        if (copyOnly)
          File.Copy(sourcePath, outputPath, true);
        else
        {
          string content = File.ReadAllText(sourcePath, Encoding.Default);
          content = RenderTemplateContent(content, projectName, symbols);
          if (content.IndexOf("[!", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("템플릿 지시문을 모두 해석하지 못했습니다: " + sourceName);
          File.WriteAllText(outputPath, content, Encoding.Default);
          project.ProjectItems.AddFromFile(outputPath);
        }
      }
    }

    private static string ReplaceTemplateName(string sourceName, string projectName)
    {
      string name = sourceName.Replace("root", projectName);
      return name.Equals("readme.txt", StringComparison.OrdinalIgnoreCase) ? "ReadMe.txt" : name;
    }

    private static string RenderTemplateContent(string content, string projectName, Dictionary<string, bool> symbols)
    {
      string safe = ToSafeWizardIdentifier(projectName);
      string upper = safe.ToUpperInvariant();
      var result = new StringBuilder();
      var active = new Stack<bool>();
      bool include = true;
      foreach (string rawLine in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
      {
        string line = rawLine.Trim();
        if (line.StartsWith("[!if ", StringComparison.OrdinalIgnoreCase) && line.EndsWith("]"))
        {
          string expression = line.Substring(5, line.Length - 6).Trim();
          bool value = EvaluateTemplateExpression(expression, symbols);
          active.Push(include);
          include = include && value;
          continue;
        }
        if (line.Equals("[!else]", StringComparison.OrdinalIgnoreCase))
        {
          if (active.Count == 0) throw new InvalidOperationException("템플릿의 !else 위치가 잘못되었습니다.");
          include = active.Peek() && !include;
          continue;
        }
        if (line.Equals("[!endif]", StringComparison.OrdinalIgnoreCase))
        {
          if (active.Count == 0) throw new InvalidOperationException("템플릿의 !endif 위치가 잘못되었습니다.");
          include = active.Pop();
          continue;
        }
        if (include)
        {
          result.Append(rawLine.Replace("[!output PROJECT_NAME]", projectName)
              .Replace("[!output SAFE_PROJECT_IDENTIFIER_NAME]", safe)
              .Replace("[!output UPPER_CASE_SAFE_PROJECT_IDENTIFIER_NAME]", upper)
              .Replace("[!output RC_FILE_NAME]", projectName + ".rc")
              .Replace("[!output LANG_SUFFIX]", "0409")
              .Replace("[!output PRIMARY_LANG_ID]", "9")
              .Replace("[!output SUB_LANG_ID]", "1")
              .Replace("[!output MFC_RC_INCLUDE_PREFIX]", "")
              .Replace("[!output DLG_FONT_NAME]", "MS Shell Dlg")
              .Replace("[!output DLG_FONT_SIZE]", "8")
              .Replace("[!output YEAR]", DateTime.Now.Year.ToString()));
          result.AppendLine();
        }
      }
      return result.ToString();
    }

    private static bool EvaluateTemplateExpression(string expression, Dictionary<string, bool> symbols)
    {
      foreach (string orPart in expression.Split(new[] { "||" }, StringSplitOptions.None))
      {
        bool andValue = true;
        foreach (string rawTerm in orPart.Split(new[] { "&&" }, StringSplitOptions.None))
        {
          string term = rawTerm.Trim();
          bool negate = term.StartsWith("!");
          if (negate) term = term.Substring(1).Trim();
          bool value = symbols.ContainsKey(term) && symbols[term];
          andValue = andValue && (negate ? !value : value);
        }
        if (andValue) return true;
      }
      return false;
    }

    private string FindVs2010ApplicationTemplatesDirectory()
    {
      string devenvPath = _dte.FullName;
      string ideDirectory = Path.GetDirectoryName(devenvPath);
      string common7Directory = Directory.GetParent(ideDirectory).FullName;
      string vsRoot = Directory.GetParent(common7Directory).FullName;
      string path = Path.Combine(vsRoot, "VC", "VCWizards", "AppWiz", "Generic", "Application", "templates", "1033");
      if (!Directory.Exists(path))
        throw new DirectoryNotFoundException("VS2010 Application 템플릿 폴더를 찾을 수 없습니다: " + path);
      return path;
    }

    private string FindVs2010EmptyProjectTemplate()
    {
      string devenvPath = _dte.FullName;
      string ideDirectory = Path.GetDirectoryName(devenvPath);
      string common7Directory = Directory.GetParent(ideDirectory).FullName;
      string vsRoot = Directory.GetParent(common7Directory).FullName;
      string templatePath = Path.Combine(vsRoot, "VC", "vcprojects", "emptyproj.vsz");
      if (!File.Exists(templatePath))
      {
        throw new FileNotFoundException(
            "Visual Studio 2010 설치 폴더에서 emptyproj.vsz를 찾을 수 없습니다.",
            templatePath);
      }
      return templatePath;
    }

    private static void ApplyConfigurationType(
        Project project,
        string applicationType,
        bool supportAtl,
        bool supportMfc,
        bool emptyProject,
        bool precompiledHeader,
        bool exportSymbols,
        bool isConsole,
        bool isDll,
        bool isLib)
    {
      ConfigurationTypes configurationType = isDll ? ConfigurationTypes.typeDynamicLibrary :
          isLib ? ConfigurationTypes.typeStaticLibrary : ConfigurationTypes.typeApplication;
      string projectIdentifier = ToSafeWizardIdentifier(project.Name);
      VCProject vcProject = project.Object as VCProject;
      if (vcProject == null)
        throw new InvalidOperationException("생성된 프로젝트가 Visual C++ VCProject로 확인되지 않습니다.");

      IVCCollection configurations = (IVCCollection)vcProject.Configurations;
      for (int index = 1; index <= configurations.Count; index++)
      {
        VCConfiguration config = (VCConfiguration)configurations.Item(index);
        string configurationName = "configuration #" + index;
        try
        {
          config.ConfigurationType = configurationType;
          if (supportMfc)
          {
            config.useOfMfc = supportMfc
                ? (isLib ? useOfMfc.useMfcStatic : isDll ? useOfMfc.useMfcDynamic : useOfMfc.useMfcStdWin)
                : useOfMfc.useMfcStdWin;
          }

          if (supportAtl)
          {
            config.useOfATL = supportAtl
                ? (isLib ? useOfATL.useATLStatic : isDll ? useOfATL.useATLDynamic : useOfATL.useATLNotSet)
                : useOfATL.useATLNotSet;
          }

          IVCCollection tools = (IVCCollection)config.Tools;
          VCCLCompilerTool compilerTool = (VCCLCompilerTool)tools.Item("VCCLCompilerTool");
          VCLinkerTool linker = (VCLinkerTool)tools.Item("VCLinkerTool");
          compilerTool.UsePrecompiledHeader = emptyProject || !precompiledHeader
              ? pchOption.pchNone
              : pchOption.pchUseUsingSpecific;
          if (!emptyProject && precompiledHeader)
            compilerTool.PrecompiledHeaderThrough = "stdafx.h";

          string definitions = compilerTool.PreprocessorDefinitions ?? string.Empty;
          List<string> filtered = new List<string>();
          foreach (string item in definitions.Split(';'))
          {
            string trimmed = item.Trim();
            if (trimmed.Length == 0 ||
                string.Equals(trimmed, "_WINDOWS", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "_CONSOLE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "_USRDLL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "_LIB", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith("_EXPORTS", StringComparison.OrdinalIgnoreCase))
              continue;
            filtered.Add(trimmed);
          }
          if (isLib) filtered.Insert(0, "_LIB");
          else if (isDll)
          {
            filtered.Insert(0, "_USRDLL");
            filtered.Insert(0, "_WINDOWS");
          }
          else if (isConsole) filtered.Insert(0, "_CONSOLE");
          else filtered.Insert(0, "_WINDOWS");
          if (isDll && exportSymbols)
            filtered.Insert(0, projectIdentifier + "_EXPORTS");
          compilerTool.PreprocessorDefinitions = string.Join(";", filtered.ToArray());

          if (!isLib)
            linker.SubSystem = isConsole ? subSystemOption.subSystemConsole : subSystemOption.subSystemWindows;
        }
        catch (Exception ex)
        {
          throw new InvalidOperationException(
              "프로젝트 구성 설정에 실패했습니다: " + configurationName, ex);
        }
      }
      // VCProjectEngine 설정을 DTE 구성에도 반영해 VS2010의 프로젝트
      // 저장 시 기본 Application 값으로 되돌아가지 않도록 합니다.
      foreach (Configuration configuration in project.ConfigurationManager)
      {
        Property typeProperty = configuration.Properties.Item("ConfigurationType");
        if (typeProperty != null)
          typeProperty.Value = (int)configurationType;
        Property characterSet = configuration.Properties.Item("CharacterSet");
        if (characterSet != null)
          characterSet.Value = 1; // Unicode
      }
      foreach (Configuration configuration in project.ConfigurationManager)
      {
        Property typeProperty = configuration.Properties.Item("ConfigurationType");
        if (typeProperty != null && Convert.ToInt32(typeProperty.Value) != (int)configurationType)
          throw new InvalidOperationException("DTE 구성에 ConfigurationType이 저장되지 않았습니다.");
      }
      ApplyPrecompiledHeaderFileSettings(vcProject, precompiledHeader && !emptyProject);
      // DTE Save()가 VCProjectEngine에서 방금 설정한 값을 다시 기본값으로
      // 덮어쓸 수 있으므로, DTE 설정을 먼저 저장한 다음 VCProject를 마지막에
      // 저장합니다. VCProject.Save()가 최종 프로젝트 파일을 기록합니다.
      project.Save();
      vcProject.Save();
    }

    private static void ApplyPrecompiledHeaderFileSettings(VCProject vcProject, bool enabled)
    {
      IVCCollection files = (IVCCollection)vcProject.Files;
      for (int fileIndex = 1; fileIndex <= files.Count; fileIndex++)
      {
        VCFile file = (VCFile)files.Item(fileIndex);
        string fileName = Path.GetFileName(file.Name);
        bool isCpp = fileName.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".cxx", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".c", StringComparison.OrdinalIgnoreCase);
        if (!isCpp) continue;
        bool createsHeader = enabled && fileName.Equals("stdafx.cpp", StringComparison.OrdinalIgnoreCase);
        IVCCollection fileConfigurations = (IVCCollection)file.FileConfigurations;
        for (int configIndex = 1; configIndex <= fileConfigurations.Count; configIndex++)
        {
          VCFileConfiguration fileConfiguration = (VCFileConfiguration)fileConfigurations.Item(configIndex);
          VCCLCompilerTool compilerTool = fileConfiguration.Tool as VCCLCompilerTool;
          if (compilerTool == null) continue;
          compilerTool.UsePrecompiledHeader = enabled
              ? (createsHeader ? pchOption.pchCreateUsingSpecific : pchOption.pchUseUsingSpecific)
              : pchOption.pchNone;
          compilerTool.PrecompiledHeaderThrough = enabled ? "stdafx.h" : string.Empty;
        }
      }
    }

    public BuildResult RunBuildOperation(BridgeRequest request)
    {
      string scope = NormalizeScope(request.Scope);
      string operation = NormalizeOperation(request.Operation);
      if (scope == "project" && string.IsNullOrWhiteSpace(request.Project))
      {
        throw new InvalidOperationException("Project Only 작업에는 project 인자가 필요합니다.");
      }

      if (!Monitor.TryEnter(_buildGate))
      {
        throw new InvalidOperationException("이 VS2010 인스턴스에서 이미 MCP 빌드 요청이 실행 중입니다.");
      }

      try
      {
        DateTime startedAt = DateTime.UtcNow;
        OutputSnapshot before = OnUiThread(CaptureOutputSnapshot);
        ManualResetEvent buildCompleted = new ManualResetEvent(false);
        BuildEvents buildEvents = null;
        ProjectInfo selectedProject = null;
        string executedCommand = null;
        _dispBuildEvents_OnBuildDoneEventHandler onBuildDone = delegate
        {
          buildCompleted.Set();
        };

        try
        {
          SolutionConfigurationInfo selected = OnUiThread(delegate
          {
            EnsureSolutionOpen();
            SolutionConfigurationInfo configuration = ActivateConfiguration(request.Configuration, request.Platform);
            if (_dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
            {
              throw new InvalidOperationException("Visual Studio 2010에서 이미 빌드가 진행 중입니다.");
            }

            Project project = null;
            if (scope == "project")
            {
              project = ResolveProject(request.Project);
              selectedProject = CreateProjectInfo(project);
            }

            buildEvents = _dte.Events.BuildEvents;
            buildEvents.OnBuildDone += onBuildDone;

            // UI 스레드를 막지 않으면서 OnBuildDone 이벤트로 실제 완료를 확인한다.
            executedCommand = StartBuildOperation(scope, operation, project);
            return configuration;
          });

          if (!buildCompleted.WaitOne(BuildCompletionTimeout))
          {
            OnUiThread(delegate
            {
              if (_dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
              {
                _dte.ExecuteCommand("Build.Cancel", string.Empty);
              }

              return true;
            });
            throw new TimeoutException("VS2010 빌드가 2시간 안에 완료되지 않아 취소를 요청했습니다.");
          }

          return OnUiThread(delegate
          {
            OutputSnapshot after = CaptureOutputSnapshot();
            BuildResult result = new BuildResult
            {
              FailedProjects = _dte.Solution.SolutionBuild.LastBuildInfo,
              Configuration = selected.Name,
              Platform = selected.Platform,
              Scope = scope,
              Operation = operation,
              Command = executedCommand,
              StartedAtUtc = startedAt.ToString("o"),
              FinishedAtUtc = DateTime.UtcNow.ToString("o")
            };

            if (selectedProject != null)
            {
              result.ProjectName = selectedProject.Name;
              result.ProjectUniqueName = selectedProject.UniqueName;
              result.ProjectFullName = selectedProject.FullName;
            }

            result.Success = result.FailedProjects == 0;
            result.Errors.AddRange(CaptureErrorItems(result.CaptureErrors));
            result.OutputPanes.AddRange(FindOutputChanges(before, after));
            result.CaptureErrors.AddRange(before.Errors);
            result.CaptureErrors.AddRange(after.Errors);
            return result;
          });
        }
        finally
        {
          if (buildEvents != null)
          {
            try
            {
              OnUiThread(delegate
              {
                buildEvents.OnBuildDone -= onBuildDone;
                return true;
              });
            }
            catch
            {
            }
          }

          buildCompleted.Close();
        }
      }
      finally
      {
        Monitor.Exit(_buildGate);
      }
    }

    private string StartBuildOperation(string scope, string operation, Project project)
    {
      if (scope == "solution")
      {
        switch (operation)
        {
          case "clean":
            _dte.Solution.SolutionBuild.Clean(false);
            return "Build.CleanSolution";

          case "build":
            _dte.Solution.SolutionBuild.Build(false);
            return "Build.BuildSolution";

          case "rebuild":
            _dte.ExecuteCommand("Build.RebuildSolution", string.Empty);
            return "Build.RebuildSolution";
        }
      }

      if (project == null)
      {
        throw new InvalidOperationException("Project Only 대상 프로젝트를 확인할 수 없습니다.");
      }
      if (!string.Equals(project.Kind, VisualCppProjectKind, StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException(
            "Project Only는 Visual C++ 프로젝트에만 지원됩니다: " + project.Name);
      }

      SelectProjectInSolutionExplorer(project);
      string commandName;
      switch (operation)
      {
        case "clean":
          commandName = "Build.CleanOnlyProject";
          break;

        case "build":
          commandName = "Build.BuildOnlyProject";
          break;

        case "rebuild":
          commandName = "Build.RebuildOnlyProject";
          break;

        default:
          throw new InvalidOperationException("지원하지 않는 Project Only 작업입니다: " + operation);
      }

      Command command;
      try
      {
        command = _dte.Commands.Item(commandName, 0);
      }
      catch (Exception ex)
      {
        throw new InvalidOperationException(
            "VS2010에서 Project Only 명령을 찾을 수 없습니다: " + commandName,
            ex);
      }

      if (!command.IsAvailable)
      {
        throw new InvalidOperationException(
            "선택한 프로젝트에서 Project Only 명령을 사용할 수 없습니다: " + commandName);
      }

      _dte.ExecuteCommand(commandName, string.Empty);
      return commandName;
    }

    public CancelResult CancelBuild()
    {
      return OnUiThread(delegate
      {
        bool inProgress = _dte.Solution != null &&
                                _dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress;
        if (inProgress)
        {
          _dte.ExecuteCommand("Build.Cancel", string.Empty);
        }

        return new CancelResult { Requested = inProgress };
      });
    }

    private VisualStudioState CaptureState()
    {
      VisualStudioState state = new VisualStudioState
      {
        ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
        VisualStudioVersion = _dte.Version,
        BuildState = _dte.Solution.SolutionBuild.BuildState.ToString()
      };

      if (_dte.Solution != null && _dte.Solution.IsOpen)
      {
        state.SolutionPath = EmptyToNull(_dte.Solution.FullName);
        state.SolutionName = EmptyToNull(Path.GetFileName(_dte.Solution.FullName));

        SolutionConfiguration2 active = _dte.Solution.SolutionBuild.ActiveConfiguration as SolutionConfiguration2;
        if (active != null)
        {
          state.ActiveConfiguration = active.Name;
          state.ActivePlatform = active.PlatformName;
        }

        CaptureProjects(state.Projects);
        CaptureConfigurations(state.Configurations);
      }

      return state;
    }

    private void CaptureProjects(List<ProjectInfo> projects)
    {
      Projects dteProjects = _dte.Solution.Projects;
      for (int index = 1; index <= dteProjects.Count; index++)
      {
        try
        {
          CaptureProject(dteProjects.Item(index), projects);
        }
        catch (Exception ex)
        {
          projects.Add(new ProjectInfo
          {
            Name = "<프로젝트 정보 읽기 실패>",
            FullName = ex.Message
          });
        }
      }
    }

    private void CaptureProject(Project project, List<ProjectInfo> projects)
    {
      projects.Add(CreateProjectInfo(project));
      if (!IsSolutionFolder(project))
      {
        return;
      }

      ProjectItems items = project.ProjectItems;
      if (items == null)
      {
        return;
      }

      for (int index = 1; index <= items.Count; index++)
      {
        try
        {
          Project subProject = items.Item(index).SubProject;
          if (subProject != null)
          {
            CaptureProject(subProject, projects);
          }
        }
        catch
        {
          // 로드되지 않았거나 자동화 모델을 제공하지 않는 항목은 건너뛴다.
        }
      }
    }

    private Project ResolveProject(string identifier)
    {
      string requested = identifier == null ? string.Empty : identifier.Trim();
      if (requested.Length == 0)
      {
        throw new InvalidOperationException("Project Only 대상 프로젝트가 비어 있습니다.");
      }

      List<Project> projects = GetBuildableProjects();
      if (Path.IsPathRooted(requested))
      {
        List<Project> pathMatches = projects.FindAll(delegate (Project project)
        {
          return PathsEqual(SafeProjectFullName(project), requested);
        });
        if (pathMatches.Count == 1)
        {
          return pathMatches[0];
        }
      }

      string normalizedRequested = NormalizeProjectIdentifier(requested);
      List<Project> uniqueNameMatches = projects.FindAll(delegate (Project project)
      {
        return string.Equals(
                  NormalizeProjectIdentifier(SafeProjectUniqueName(project)),
                  normalizedRequested,
                  StringComparison.OrdinalIgnoreCase);
      });
      if (uniqueNameMatches.Count == 1)
      {
        return uniqueNameMatches[0];
      }

      List<Project> nameMatches = projects.FindAll(delegate (Project project)
      {
        return string.Equals(project.Name, requested, StringComparison.OrdinalIgnoreCase);
      });
      if (nameMatches.Count == 1)
      {
        return nameMatches[0];
      }
      if (nameMatches.Count > 1 || uniqueNameMatches.Count > 1)
      {
        List<Project> ambiguous = uniqueNameMatches.Count > 1 ? uniqueNameMatches : nameMatches;
        List<string> choices = ambiguous.ConvertAll(delegate (Project project)
        {
          return project.Name + " [" + (SafeProjectUniqueName(project) ?? SafeProjectFullName(project)) + "]";
        });
        throw new InvalidOperationException(
            "프로젝트 식별자가 둘 이상과 일치합니다. UniqueName 또는 전체 경로를 사용하십시오: " +
            string.Join(", ", choices.ToArray()));
      }

      List<string> available = projects.ConvertAll(delegate (Project project)
      {
        return project.Name + " [" + (SafeProjectUniqueName(project) ?? SafeProjectFullName(project)) + "]";
      });
      throw new InvalidOperationException(
          "요청한 프로젝트를 찾을 수 없습니다: " + requested +
          ". 사용 가능한 프로젝트: " + string.Join(", ", available.ToArray()));
    }

    private List<Project> GetBuildableProjects()
    {
      List<Project> projects = new List<Project>();
      Projects roots = _dte.Solution.Projects;
      for (int index = 1; index <= roots.Count; index++)
      {
        try
        {
          CollectBuildableProjects(roots.Item(index), projects);
        }
        catch
        {
          // 다른 프로젝트 검색은 계속한다.
        }
      }

      return projects;
    }

    private void CollectBuildableProjects(Project project, List<Project> projects)
    {
      if (!IsSolutionFolder(project))
      {
        projects.Add(project);
        return;
      }

      ProjectItems items = project.ProjectItems;
      if (items == null)
      {
        return;
      }

      for (int index = 1; index <= items.Count; index++)
      {
        try
        {
          Project subProject = items.Item(index).SubProject;
          if (subProject != null)
          {
            CollectBuildableProjects(subProject, projects);
          }
        }
        catch
        {
          // 로드되지 않았거나 자동화 모델을 제공하지 않는 항목은 건너뛴다.
        }
      }
    }

    private void SelectProjectInSolutionExplorer(Project project)
    {
      UIHierarchy solutionExplorer = _dte.ToolWindows.SolutionExplorer;
      UIHierarchyItem item = FindProjectHierarchyItem(solutionExplorer.UIHierarchyItems, project);
      if (item == null)
      {
        throw new InvalidOperationException(
            "솔루션 탐색기에서 Project Only 대상 프로젝트를 찾을 수 없습니다: " + project.Name);
      }

      solutionExplorer.Parent.Activate();
      item.Select(vsUISelectionType.vsUISelectionTypeSelect);
    }

    private UIHierarchyItem FindProjectHierarchyItem(UIHierarchyItems items, Project project)
    {
      if (items == null)
      {
        return null;
      }

      for (int index = 1; index <= items.Count; index++)
      {
        UIHierarchyItem item = null;
        try
        {
          item = items.Item(index);
          Project candidate = item.Object as Project;
          if (candidate != null && SameProject(candidate, project))
          {
            return item;
          }

          UIHierarchyItem child = FindProjectHierarchyItem(item.UIHierarchyItems, project);
          if (child != null)
          {
            return child;
          }
        }
        catch
        {
          // 자동화 모델을 제공하지 않는 노드는 건너뛴다.
        }
      }

      return null;
    }

    private static bool SameProject(Project left, Project right)
    {
      string leftUniqueName = SafeProjectUniqueName(left);
      string rightUniqueName = SafeProjectUniqueName(right);
      if (!string.IsNullOrEmpty(leftUniqueName) && !string.IsNullOrEmpty(rightUniqueName))
      {
        return string.Equals(
            NormalizeProjectIdentifier(leftUniqueName),
            NormalizeProjectIdentifier(rightUniqueName),
            StringComparison.OrdinalIgnoreCase);
      }

      return PathsEqual(SafeProjectFullName(left), SafeProjectFullName(right));
    }

    private static ProjectInfo CreateProjectInfo(Project project)
    {
      return new ProjectInfo
      {
        Name = project.Name,
        UniqueName = SafeProjectUniqueName(project),
        FullName = SafeProjectFullName(project),
        Kind = project.Kind,
        IsSolutionFolder = IsSolutionFolder(project)
      };
    }

    private static bool IsSolutionFolder(Project project)
    {
      return string.Equals(project.Kind, SolutionFolderProjectKind, StringComparison.OrdinalIgnoreCase);
    }

    private void CaptureConfigurations(List<SolutionConfigurationInfo> configurations)
    {
      SolutionConfigurations available = _dte.Solution.SolutionBuild.SolutionConfigurations;
      for (int index = 1; index <= available.Count; index++)
      {
        SolutionConfiguration configuration = available.Item(index);
        SolutionConfiguration2 configuration2 = configuration as SolutionConfiguration2;
        configurations.Add(new SolutionConfigurationInfo
        {
          Name = configuration.Name,
          Platform = configuration2 == null ? null : configuration2.PlatformName
        });
      }
    }

    private SolutionConfigurationInfo ActivateConfiguration(string requestedName, string requestedPlatform)
    {
      SolutionBuild build = _dte.Solution.SolutionBuild;
      SolutionConfiguration2 active = build.ActiveConfiguration as SolutionConfiguration2;
      string name = string.IsNullOrWhiteSpace(requestedName)
          ? (active == null ? null : active.Name)
          : requestedName;
      string platform = string.IsNullOrWhiteSpace(requestedPlatform)
          ? (active == null ? null : active.PlatformName)
          : requestedPlatform;

      SolutionConfigurations available = build.SolutionConfigurations;
      List<string> choices = new List<string>();
      for (int index = 1; index <= available.Count; index++)
      {
        SolutionConfiguration configuration = available.Item(index);
        SolutionConfiguration2 configuration2 = configuration as SolutionConfiguration2;
        string candidatePlatform = configuration2 == null ? null : configuration2.PlatformName;
        choices.Add(configuration.Name + "|" + (candidatePlatform ?? string.Empty));

        if (string.Equals(configuration.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(platform) ||
             string.Equals(candidatePlatform, platform, StringComparison.OrdinalIgnoreCase)))
        {
          configuration.Activate();
          return new SolutionConfigurationInfo
          {
            Name = configuration.Name,
            Platform = candidatePlatform
          };
        }
      }

      throw new InvalidOperationException(
          "요청한 솔루션 구성을 찾을 수 없습니다. 사용 가능한 구성: " + string.Join(", ", choices.ToArray()));
    }

    private OutputSnapshot CaptureOutputSnapshot()
    {
      OutputSnapshot snapshot = new OutputSnapshot();
      try
      {
        OutputWindow outputWindow = _dte.ToolWindows.OutputWindow;
        OutputWindowPanes panes = outputWindow.OutputWindowPanes;
        for (int index = 1; index <= panes.Count; index++)
        {
          OutputWindowPane pane = null;
          try
          {
            pane = panes.Item(index);
            if (!IsBuildOutputPane(pane))
            {
              continue;
            }

            pane.Activate();
            TextDocument document = pane.TextDocument;
            EditPoint start = document.StartPoint.CreateEditPoint();
            snapshot.TextByPane[pane.Name] = start.GetText(document.EndPoint);
          }
          catch (Exception ex)
          {
            snapshot.Errors.Add("Output pane '" + (pane == null ? index.ToString() : pane.Name) +
                                "' 읽기 실패: " + ex.Message);
          }
        }
      }
      catch (Exception ex)
      {
        snapshot.Errors.Add("Output 창 읽기 실패: " + ex.Message);
      }

      return snapshot;
    }

    private static bool IsBuildOutputPane(OutputWindowPane pane)
    {
      if (string.Equals(pane.Name, "Build", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(pane.Name, "빌드", StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }

      try
      {
        return string.Equals(pane.Guid, BuildOutputPaneGuid, StringComparison.OrdinalIgnoreCase);
      }
      catch
      {
        return false;
      }
    }

    private static IEnumerable<OutputPaneResult> FindOutputChanges(OutputSnapshot before, OutputSnapshot after)
    {
      List<OutputPaneResult> results = new List<OutputPaneResult>();
      foreach (KeyValuePair<string, string> pair in after.TextByPane)
      {
        string oldText;
        before.TextByPane.TryGetValue(pair.Key, out oldText);
        oldText = oldText ?? string.Empty;
        string newText = pair.Value ?? string.Empty;
        string delta = newText.StartsWith(oldText, StringComparison.Ordinal)
            ? newText.Substring(oldText.Length)
            : newText;

        if (!string.IsNullOrEmpty(delta))
        {
          results.Add(new OutputPaneResult { Name = pair.Key, Text = delta });
        }
      }

      return results;
    }

    private List<BuildErrorInfo> CaptureErrorItems(List<string> captureErrors)
    {
      List<BuildErrorInfo> errors = new List<BuildErrorInfo>();
      try
      {
        ErrorItems items = _dte.ToolWindows.ErrorList.ErrorItems;
        for (int index = 1; index <= items.Count; index++)
        {
          try
          {
            ErrorItem item = items.Item(index);
            errors.Add(new BuildErrorInfo
            {
              Level = item.ErrorLevel.ToString(),
              Description = item.Description,
              Project = EmptyToNull(item.Project),
              File = EmptyToNull(item.FileName),
              Line = item.Line,
              Column = item.Column
            });
          }
          catch (Exception ex)
          {
            captureErrors.Add("Error List 항목 " + index + " 읽기 실패: " + ex.Message);
          }
        }
      }
      catch (Exception ex)
      {
        captureErrors.Add("Error List 읽기 실패: " + ex.Message);
      }

      return errors;
    }

    private void EnsureSolutionOpen()
    {
      if (_dte.Solution == null || !_dte.Solution.IsOpen)
      {
        throw new InvalidOperationException("이 VS2010 인스턴스에 열린 솔루션이 없습니다.");
      }
    }

    private void EnsureShellInitialized()
    {
      object initialized;
      ThrowOnFailure(_shellService.GetProperty(
          (int)__VSSPROPID4.VSSPROPID_ShellInitialized,
          out initialized));
      if (initialized == null || !Convert.ToBoolean(initialized))
      {
        throw new InvalidOperationException(
            "VS2010 셸 초기화가 아직 끝나지 않았습니다. 시작 화면 로딩이 끝난 뒤 다시 시도하십시오.");
      }
    }

    private T OnUiThread<T>(Func<T> action)
    {
      return InvokeOnUiThread(action, DispatcherPriority.Send);
    }

    private T OnUiThreadAtIdle<T>(Func<T> action)
    {
      return InvokeOnUiThread(action, DispatcherPriority.ApplicationIdle);
    }

    private T InvokeOnUiThread<T>(Func<T> action, DispatcherPriority priority)
    {
      if (_dispatcher.CheckAccess())
      {
        return action();
      }

      T result = default(T);
      Exception actionError = null;
      _dispatcher.Invoke(priority, (Action)delegate
      {
        try
        {
          result = action();
        }
        catch (Exception ex)
        {
          // VS2010 UI Dispatcher 밖으로 예외가 탈출하면 devenv.exe가 종료될 수 있다.
          // UI 스레드에서는 예외를 캡처하고 호출한 브리지 작업 스레드에서 다시 발생시킨다.
          actionError = ex;
        }
      });

      if (actionError != null)
      {
        throw new InvalidOperationException(actionError.Message, actionError);
      }

      return result;
    }

    private static string NormalizeScope(string scope)
    {
      string normalized = string.IsNullOrWhiteSpace(scope)
          ? "solution"
          : scope.Trim().ToLowerInvariant();
      if (normalized != "solution" && normalized != "project")
      {
        throw new InvalidOperationException(
            "지원하지 않는 빌드 범위입니다: " + scope + ". solution 또는 project를 사용하십시오.");
      }

      return normalized;
    }

    private static void ThrowOnFailure(int hresult)
    {
      if (hresult < 0)
      {
        Marshal.ThrowExceptionForHR(hresult);
      }
    }

    private static string NormalizeOperation(string operation)
    {
      string normalized = string.IsNullOrWhiteSpace(operation)
          ? "build"
          : operation.Trim().ToLowerInvariant();
      if (normalized != "clean" && normalized != "build" && normalized != "rebuild")
      {
        throw new InvalidOperationException(
            "지원하지 않는 빌드 작업입니다: " + operation + ". clean, build, rebuild 중 하나를 사용하십시오.");
      }

      return normalized;
    }

    private static string SafeProjectUniqueName(Project project)
    {
      try
      {
        return EmptyToNull(project.UniqueName);
      }
      catch
      {
        return null;
      }
    }

    private static string NormalizeProjectIdentifier(string value)
    {
      return string.IsNullOrWhiteSpace(value)
          ? string.Empty
          : value.Trim().Replace('/', '\\');
    }

    private static bool PathsEqual(string left, string right)
    {
      if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
      {
        return false;
      }

      try
      {
        string normalizedLeft = Path.GetFullPath(left).TrimEnd('\\', '/');
        string normalizedRight = Path.GetFullPath(right).TrimEnd('\\', '/');
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
      }
      catch
      {
        return false;
      }
    }

    private static string SafeProjectFullName(Project project)
    {
      try
      {
        return EmptyToNull(project.FullName);
      }
      catch
      {
        return null;
      }
    }

    private static string EmptyToNull(string value)
    {
      return string.IsNullOrEmpty(value) ? null : value;
    }

    private sealed class OutputSnapshot
    {
      public OutputSnapshot()
      {
        TextByPane = new Dictionary<string, string>(StringComparer.Ordinal);
        Errors = new List<string>();
      }

      public Dictionary<string, string> TextByPane { get; private set; }

      public List<string> Errors { get; private set; }
    }
  }
}
