using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using EnvDTE;
using EnvDTE80;
using McpVs2010.Bridge.Protocol;
using Microsoft.VisualStudio.Shell.Interop;

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
                List<Project> pathMatches = projects.FindAll(delegate(Project project)
                {
                    return PathsEqual(SafeProjectFullName(project), requested);
                });
                if (pathMatches.Count == 1)
                {
                    return pathMatches[0];
                }
            }

            string normalizedRequested = NormalizeProjectIdentifier(requested);
            List<Project> uniqueNameMatches = projects.FindAll(delegate(Project project)
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

            List<Project> nameMatches = projects.FindAll(delegate(Project project)
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
                List<string> choices = ambiguous.ConvertAll(delegate(Project project)
                {
                    return project.Name + " [" + (SafeProjectUniqueName(project) ?? SafeProjectFullName(project)) + "]";
                });
                throw new InvalidOperationException(
                    "프로젝트 식별자가 둘 이상과 일치합니다. UniqueName 또는 전체 경로를 사용하십시오: " +
                    string.Join(", ", choices.ToArray()));
            }

            List<string> available = projects.ConvertAll(delegate(Project project)
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
