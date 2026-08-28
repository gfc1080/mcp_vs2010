using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading;

namespace McpVs2010.Bridge
{
    internal sealed class McpServerProcess : IDisposable
    {
        private readonly Process _process;
        private readonly bool _ownsProcess;
        private static string _lastServerDirectory;
        private bool _disposed;

        private McpServerProcess(Process process, bool ownsProcess)
        {
            _process = process;
            _ownsProcess = ownsProcess;
        }

        public bool IsRunning
        {
            get
            {
                if (_process == null) return true;
                try { return !_process.HasExited; }
                catch { return false; }
            }
        }

        public static McpServerProcess Start()
        {
            try
            {
                using (Mutex.OpenExisting("Global\\McpVs2010.Server"))
                    return new McpServerProcess(null, false);
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }

            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(assemblyDirectory))
                throw new InvalidOperationException("MCP VS2010 Bridge 설치 폴더를 확인할 수 없습니다.");

            string installedServerDirectory = Path.Combine(assemblyDirectory, "server");
            string serverDirectory = MaterializeServerDirectory(installedServerDirectory);
            string localServerDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "McpVs2010");
            if (File.Exists(Path.Combine(localServerDirectory, "McpVs2010.Server.exe")) ||
                File.Exists(Path.Combine(localServerDirectory, "McpVs2010.Server.dll")))
            {
                serverDirectory = MaterializeServerDirectory(localServerDirectory);
            }
            string serverPath = Path.Combine(serverDirectory, "McpVs2010.Server.exe");
            string serverDllPath = Path.Combine(serverDirectory, "McpVs2010.Server.dll");
            if (!File.Exists(serverPath) && !File.Exists(serverDllPath))
            {
                string discoveredDirectory = FindInstalledServerDirectory(assemblyDirectory);
                if (!string.IsNullOrEmpty(discoveredDirectory))
                {
                    serverDirectory = MaterializeServerDirectory(discoveredDirectory);
                    serverPath = Path.Combine(serverDirectory, "McpVs2010.Server.exe");
                    serverDllPath = Path.Combine(serverDirectory, "McpVs2010.Server.dll");
                }
            }
            if (!File.Exists(serverPath))
            {
                if (!string.IsNullOrEmpty(_lastServerDirectory) && File.Exists(Path.Combine(_lastServerDirectory, "McpVs2010.Server.exe")))
                    serverDirectory = _lastServerDirectory;
                else
                {
                    string cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "McpVs2010", "server-cache");
                    string versionPrefix = Assembly.GetExecutingAssembly().GetName().Version + "-";
                    string cached = Directory.Exists(cacheRoot) ? Directory.GetDirectories(cacheRoot, versionPrefix + "*", SearchOption.TopDirectoryOnly)
                        .Where(path => File.Exists(Path.Combine(path, "McpVs2010.Server.exe")))
                        .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path)).FirstOrDefault() : null;
                    if (!string.IsNullOrEmpty(cached)) serverDirectory = cached;
                }
                serverPath = Path.Combine(serverDirectory, "McpVs2010.Server.exe");
                serverDllPath = Path.Combine(serverDirectory, "McpVs2010.Server.dll");
                if (!File.Exists(serverPath) && !File.Exists(serverDllPath)) throw new FileNotFoundException("VSIX에 포함된 MCP VS2010 서버 파일을 찾을 수 없습니다.", serverPath);
            }
            _lastServerDirectory = serverDirectory;

            bool useDotnetHost = !File.Exists(serverPath);
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = useDotnetHost ? FindDotnetHost() : serverPath,
                WorkingDirectory = serverDirectory,
                Arguments = useDotnetHost ? "\"" + serverDllPath + "\"" : string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("MCP VS2010 서버 프로세스를 시작하지 못했습니다.");

            return new McpServerProcess(process, true);
        }

        private static string FindInstalledServerDirectory(string assemblyDirectory)
        {
            try
            {
                string[] candidates = Directory.GetFiles(assemblyDirectory, "McpVs2010.Server.dll", SearchOption.AllDirectories);
                if (candidates.Length == 0)
                    candidates = Directory.GetFiles(assemblyDirectory, "McpVs2010.Server.exe.payload.pdb", SearchOption.AllDirectories);
                return candidates.Length == 0 ? null : Path.GetDirectoryName(candidates[0]);
            }
            catch
            {
                return null;
            }
        }

        private static string FindDotnetHost()
        {
            string host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (File.Exists(host)) return host;
            throw new FileNotFoundException("MCP 서버 실행 파일과 .NET 호스트를 모두 찾을 수 없습니다.", host);
        }

        private static string MaterializeServerDirectory(string installedDirectory)
        {
            string installedExecutable = Path.Combine(installedDirectory, "McpVs2010.Server.exe");
            string payloadExecutable = Path.Combine(installedDirectory, "McpVs2010.Server.exe.payload.pdb");
            if (File.Exists(installedExecutable))
                return installedDirectory;
            string installedDll = Path.Combine(installedDirectory, "McpVs2010.Server.dll");
            if (!File.Exists(payloadExecutable) && !File.Exists(installedDll))
                return installedDirectory;

            string version = Assembly.GetExecutingAssembly().GetName().Version == null
                ? "unknown"
                : Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "McpVs2010",
                "server-cache",
                version + "-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(cacheDirectory);

            foreach (string sourcePath in Directory.GetFiles(installedDirectory))
            {
                string sourceName = Path.GetFileName(sourcePath);
                const string payloadSuffix = ".payload.pdb";
                string targetName = sourceName.EndsWith(payloadSuffix, StringComparison.OrdinalIgnoreCase)
                    ? sourceName.Substring(0, sourceName.Length - payloadSuffix.Length)
                    : sourceName;
                File.Copy(sourcePath, Path.Combine(cacheDirectory, targetName), true);
            }

            return cacheDirectory;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (!_ownsProcess || _process == null) return;
            try
            {
                // The MCP server is independent of VS2010 and must remain alive
                // after the bridge package is unloaded. It is stopped only by
                // the server tray Exit command or an explicit process stop.
            }
            catch
            {
                // Ignore process-handle cleanup errors during VS shutdown.
            }
            finally
            {
                _process.Dispose();
            }
        }

        public void Stop()
        {
            Dispose();
        }
    }
}
