using System;
using System.Runtime.InteropServices;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Windows.Forms;

namespace McpVs2010.Bridge
{
    [Guid(PackageGuidString)]
    public sealed class McpVs2010Package : Package
    {
        public const string PackageGuidString = "4b44ee54-9118-43b8-894c-947d70701352";

        private BridgeHost _host;
        private McpServerProcess _serverProcess;
        private McpConfigMenu _configMenu;
        private Timer _menuTimer;

        protected override void Initialize()
        {
            base.Initialize();
            Windows11RegistryCompatibility.Install();
            try
            {
                _serverProcess = McpServerProcess.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("MCP VS2010 서버 자동 시작 실패: " + ex);
            }

            DTE2 dte = GetService(typeof(SDTE)) as DTE2;
            if (dte == null)
            {
                throw new InvalidOperationException("Visual Studio DTE 서비스를 가져올 수 없습니다.");
            }

            IVsSolution solutionService = GetService(typeof(SVsSolution)) as IVsSolution;
            if (solutionService == null)
            {
                throw new InvalidOperationException("Visual Studio 솔루션 서비스를 가져올 수 없습니다.");
            }

            IVsShell shellService = GetService(typeof(SVsShell)) as IVsShell;
            if (shellService == null)
            {
                throw new InvalidOperationException("Visual Studio 셸 서비스를 가져올 수 없습니다.");
            }

            _host = new BridgeHost(dte, solutionService, shellService);
            _host.Start();
            _menuTimer = new Timer { Interval = 2000 };
            _menuTimer.Tick += delegate
            {
                try
                {
                    if (_configMenu == null)
                    {
                        _configMenu = McpConfigMenu.Create(dte, () => _serverProcess, server => _serverProcess = server);
                        _menuTimer.Stop();
                    }
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("MCP server menu creation failed: " + ex); }
            };
            _menuTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _host != null)
            {
                _host.Dispose();
                _host = null;
            }

            if (disposing && _serverProcess != null)
            {
                _serverProcess.Dispose();
                _serverProcess = null;
            }

            if (disposing && _configMenu != null)
            {
                _configMenu.Dispose();
                _configMenu = null;
            }
            if (disposing && _menuTimer != null)
            {
                _menuTimer.Stop();
                _menuTimer.Dispose();
                _menuTimer = null;
            }

            base.Dispose(disposing);
        }
    }
}
