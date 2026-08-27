using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using McpVs2010.Bridge.Protocol;
using Microsoft.VisualStudio.Shell.Interop;

namespace McpVs2010.Bridge
{
    internal sealed class BridgeHost : IDisposable
    {
        private readonly DTE2 _dte;
        private readonly VisualStudioAutomation _automation;
        private readonly string _pipeName;
        private readonly string _startedAtUtc;
        private readonly string _discoveryDirectory;
        private readonly string _discoveryFile;
        private readonly SolutionEvents _solutionEvents;
        private System.Threading.Thread _listenerThread;
        private volatile bool _stopping;

        public BridgeHost(DTE2 dte, IVsSolution solutionService, IVsShell shellService)
        {
            _dte = dte;
            _automation = new VisualStudioAutomation(dte, solutionService, shellService);

            int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            _pipeName = "mcp-vs2010-" + processId + "-" + Guid.NewGuid().ToString("N");
            _startedAtUtc = DateTime.UtcNow.ToString("o");
            _discoveryDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "McpVs2010",
                "instances");
            _discoveryFile = Path.Combine(_discoveryDirectory, processId + ".json");

            _solutionEvents = _dte.Events.SolutionEvents;
            _solutionEvents.Opened += OnSolutionChanged;
            _solutionEvents.AfterClosing += OnSolutionChanged;
            _solutionEvents.Renamed += OnSolutionRenamed;
        }

        public void Start()
        {
            WriteDiscovery();
            _listenerThread = new System.Threading.Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "MCP VS2010 Bridge Listener"
            };
            _listenerThread.Start();
        }

        public void Dispose()
        {
            _stopping = true;
            try
            {
                _solutionEvents.Opened -= OnSolutionChanged;
                _solutionEvents.AfterClosing -= OnSolutionChanged;
                _solutionEvents.Renamed -= OnSolutionRenamed;
            }
            catch
            {
            }

            // WaitForConnection을 깨우기 위한 자기 연결이다.
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out))
                {
                    client.Connect(100);
                }
            }
            catch
            {
            }

            try
            {
                if (_listenerThread != null)
                {
                    _listenerThread.Join(500);
                }
            }
            catch
            {
            }

            try
            {
                File.Delete(_discoveryFile);
            }
            catch
            {
            }
        }

        private void ListenLoop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        4,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None);
                    pipe.WaitForConnection();

                    if (_stopping)
                    {
                        pipe.Dispose();
                        break;
                    }

                    ThreadPool.QueueUserWorkItem(HandleClient, pipe);
                    pipe = null;
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                    {
                        Trace.WriteLine("MCP VS2010 pipe listener error: " + ex);
                        System.Threading.Thread.Sleep(250);
                    }
                }
                finally
                {
                    if (pipe != null)
                    {
                        pipe.Dispose();
                    }
                }
            }
        }

        private void HandleClient(object state)
        {
            try
            {
                using (NamedPipeServerStream pipe = (NamedPipeServerStream)state)
                using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 4096))
                using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096))
                {
                    writer.AutoFlush = true;
                    BridgeRequest request = null;
                    BridgeResponse response;
                    try
                    {
                        string requestLine = reader.ReadLine();
                        if (string.IsNullOrEmpty(requestLine))
                        {
                            return;
                        }

                        request = JsonWire.Deserialize<BridgeRequest>(requestLine);
                        response = Dispatch(request);
                    }
                    catch (Exception ex)
                    {
                        response = BridgeResponse.FromError(
                            request == null ? string.Empty : request.Id,
                            ex.ToString());
                    }

                    writer.WriteLine(JsonWire.Serialize(response));
                }
            }
            catch (Exception ex)
            {
                // .NET Framework 4의 ThreadPool에서 처리되지 않은 예외는 devenv.exe를 종료시킬 수 있다.
                Trace.WriteLine("MCP VS2010 pipe client error: " + ex);
            }
        }

        private BridgeResponse Dispatch(BridgeRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.Id) || string.IsNullOrEmpty(request.Method))
            {
                throw new InvalidOperationException("브리지 요청의 id 또는 method가 비어 있습니다.");
            }

            switch (request.Method)
            {
                case "ping":
                    return BridgeResponse.FromResult(request.Id, new PingResult { ProtocolVersion = 3 });

                case "get_state":
                    VisualStudioState state = _automation.GetState();
                    WriteDiscovery(state.SolutionPath);
                    return BridgeResponse.FromResult(request.Id, state);

                case "open_solution":
                    return BridgeResponse.FromResult(request.Id, _automation.OpenSolution(request));

                case "build_solution":
                    request.Scope = "solution";
                    request.Operation = "build";
                    return BridgeResponse.FromResult(request.Id, _automation.RunBuildOperation(request));

                case "run_build_operation":
                    return BridgeResponse.FromResult(request.Id, _automation.RunBuildOperation(request));

                case "cancel_build":
                    return BridgeResponse.FromResult(request.Id, _automation.CancelBuild());

                default:
                    throw new InvalidOperationException("지원하지 않는 브리지 메서드입니다: " + request.Method);
            }
        }

        private void OnSolutionChanged()
        {
            WriteDiscovery();
        }

        private void OnSolutionRenamed(string oldName)
        {
            WriteDiscovery();
        }

        private void WriteDiscovery()
        {
            string solutionPath = null;
            try
            {
                if (_dte.Solution != null && _dte.Solution.IsOpen)
                {
                    solutionPath = _dte.Solution.FullName;
                }
            }
            catch
            {
            }

            WriteDiscovery(solutionPath);
        }

        private void WriteDiscovery(string solutionPath)
        {
            try
            {
                Directory.CreateDirectory(_discoveryDirectory);
                BridgeInstanceInfo info = new BridgeInstanceInfo
                {
                    ProtocolVersion = 3,
                    ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                    PipeName = _pipeName,
                    StartedAtUtc = _startedAtUtc,
                    SolutionPath = string.IsNullOrEmpty(solutionPath) ? null : solutionPath
                };

                string temporary = _discoveryFile + ".tmp";
                File.WriteAllText(temporary, JsonWire.Serialize(info), new UTF8Encoding(false));
                if (File.Exists(_discoveryFile))
                {
                    File.Delete(_discoveryFile);
                }

                File.Move(temporary, _discoveryFile);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("MCP VS2010 discovery write error: " + ex);
            }
        }
    }
}
