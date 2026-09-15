using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;
using System.IO.Pipes;
using System.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace McpVs2010.Server;

internal static class ServerTray
{
    private static Thread? _thread;

    public static void Start(IHostApplicationLifetime lifetime)
    {
        _thread = new Thread(() => Application.Run(new TrayContext(lifetime)))
        {
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly Thread _controlThread;
        private readonly System.Windows.Forms.Timer _visibilityTimer;
        private readonly System.Windows.Forms.Timer _configRequestTimer;
        private volatile bool _closing;
        private int _configRequested;
        private int _trayCheckRequested;
        private TrayVisibilityForm? _trayVisibilityForm;
        private ConfigForm? _configForm;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HwndTop = IntPtr.Zero;
        private const int SwShownormal = 1;
        private const uint SwpNosize = 0x0001;
        private const uint SwpNomove = 0x0002;
        private const uint SwpShowwindow = 0x0040;

        public TrayContext(IHostApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
            var menu = new ContextMenuStrip();
            menu.Items.Add("MCP Config", null, (_, _) => ShowConfig());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitServer());
            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "MCP VS2010: Running",
                ContextMenuStrip = menu,
                Visible = true
            };
            _controlThread = new Thread(ControlLoop) { IsBackground = true };
            _controlThread.Start();
            // Check only after the WinForms message loop has started and Explorer
            // has had time to register the notification icon.
            _visibilityTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _visibilityTimer.Tick += (_, _) =>
            {
                _visibilityTimer.Stop();
                _visibilityTimer.Dispose();
                // Explorer has registered the icon now. Remove stale entries
                // left by older versioned server paths, while preserving the
                // current executable's notification setting.
                RemoveDuplicateTrayEntries();
                ShowTrayVisibilityPromptIfNeeded();
            };
            _visibilityTimer.Start();
            _configRequestTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _configRequestTimer.Tick += (_, _) =>
            {
                if (Interlocked.Exchange(ref _configRequested, 0) != 0)
                    ShowConfig();
                if (Interlocked.Exchange(ref _trayCheckRequested, 0) != 0)
                    ShowTrayVisibilityPromptIfNeeded();
            };
            _configRequestTimer.Start();
            lifetime.ApplicationStopping.Register(() =>
            {
                try { _icon.Text = "MCP VS2010: Stopped"; } catch { }
                _closing = true;
                try { _controlThread.Interrupt(); } catch { }
                ExitThread();
            });
        }

        private void ControlLoop()
        {
            while (!_closing)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream("McpVs2010.Control", PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                    string? command = reader.ReadLine();
                    bool showConfig = command?.IndexOf("show-config", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool checkTray = command?.IndexOf("check-tray", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool exit = command?.IndexOf("exit", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool success = showConfig || checkTray || exit;
                    if (showConfig) Interlocked.Exchange(ref _configRequested, 1);
                    if (checkTray) Interlocked.Exchange(ref _trayCheckRequested, 1);
                    if (exit) _lifetime.StopApplication();
                    writer.WriteLine(success ? "{\"success\":true}" : "{\"success\":false}");
                }
                catch (ThreadInterruptedException) { return; }
                catch (IOException) when (_closing) { return; }
                catch { if (_closing) return; }
            }
        }

        private void ShowTrayVisibilityPromptIfNeeded()
        {
            try
            {
                if (IsTrayVisibilityPromptDisabled() || !IsTrayIconHidden()) return;
                if (_trayVisibilityForm != null && !_trayVisibilityForm.IsDisposed) return;
                _trayVisibilityForm = new TrayVisibilityForm();
                _trayVisibilityForm.FormClosed += (_, _) =>
                {
                    try
                    {
                        if (_trayVisibilityForm!.OpenSettingsRequested)
                            Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });
                        if (_trayVisibilityForm.DoNotShowAgain) SetTrayVisibilityPromptDisabled();
                    }
                    catch { }
                    finally { _trayVisibilityForm = null; }
                };
                _trayVisibilityForm.Show();
            }
            catch { }
        }

        private static bool IsTrayIconHidden()
        {
            using var settings = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\NotifyIconSettings", false);
            // No entry means Explorer has not promoted this icon yet. Treat it as
            // hidden so the first run can guide the user to the correct setting.
            if (settings == null) return true;
            string executablePath = Environment.ProcessPath ?? string.Empty;
            bool matchingEntryFound = false;
            foreach (string name in settings.GetSubKeyNames())
            {
                using var item = settings.OpenSubKey(name, false);
                string? itemPath = item?.GetValue("ExecutablePath") as string;
                string? iconPath = item?.GetValue("IconPath") as string;
                bool pathMatches = !string.IsNullOrWhiteSpace(executablePath) &&
                    (string.Equals(itemPath, executablePath, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(iconPath, executablePath, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(Path.GetFileName(itemPath), Path.GetFileName(executablePath), StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(Path.GetFileName(iconPath), Path.GetFileName(executablePath), StringComparison.OrdinalIgnoreCase));
                if (!pathMatches) continue;
                matchingEntryFound = true;
                if (item?.GetValue("IsPromoted") is not int promoted || promoted == 0) return true;
            }
            return !matchingEntryFound;
        }

        private static void RemoveDuplicateTrayEntries()
        {
            try
            {
                string executablePath = Environment.ProcessPath ?? string.Empty;
                string executableName = Path.GetFileName(executablePath);
                if (string.IsNullOrWhiteSpace(executableName)) return;
                using var settings = Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\NotifyIconSettings", true);
                if (settings == null) return;

                string? keptPath = null;
                foreach (string name in settings.GetSubKeyNames())
                {
                    using var item = settings.OpenSubKey(name, false);
                    string? itemPath = item?.GetValue("ExecutablePath") as string
                        ?? item?.GetValue("IconPath") as string;
                    if (!string.Equals(Path.GetFileName(itemPath), executableName,
                        StringComparison.OrdinalIgnoreCase)) continue;

                    if (string.Equals(itemPath, executablePath, StringComparison.OrdinalIgnoreCase) && keptPath == null)
                    {
                        keptPath = itemPath;
                        continue;
                    }
                    settings.DeleteSubKeyTree(name, false);
                }
            }
            catch { }
        }

        private static bool IsTrayVisibilityPromptDisabled()
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\McpVs2010");
            object? value = key?.GetValue("TrayVisibilityPromptDisabled");
            if (value is int disabled && (disabled == 0 || disabled == 1))
                return disabled == 1;

            key?.SetValue("TrayVisibilityPromptDisabled", 0, RegistryValueKind.DWord);
            return false;
        }

        private static void SetTrayVisibilityPromptDisabled()
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\McpVs2010");
            key?.SetValue("TrayVisibilityPromptDisabled", 1, RegistryValueKind.DWord);
        }

        private static Icon LoadIcon()
        {
            try
            {
                foreach (string resourceName in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                {
                    if (!resourceName.EndsWith("vs2010.ico", StringComparison.OrdinalIgnoreCase)) continue;
                    using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                    if (stream == null) continue;
                    using var embeddedIcon = new Icon(stream);
                    return new Icon(embeddedIcon, embeddedIcon.Size);
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        private void ShowConfig()
        {
            // A running server can outlive Explorer's notification-area state. Re-register
            // the icon whenever Config is requested from the VSIX so a missing icon is
            // recovered without restarting the server.
            RefreshTrayIcon();

            if (_configForm != null && !_configForm.IsDisposed)
            {
                if (_configForm.WindowState == FormWindowState.Minimized)
                    _configForm.WindowState = FormWindowState.Normal;
                FocusConfigWindow(_configForm);
                return;
            }

            _configForm = new ConfigForm(_lifetime);
            _configForm.Shown += (_, _) => FocusConfigWindow(_configForm);
            try { _configForm.ShowDialog(); }
            finally
            {
                _configForm.Dispose();
                _configForm = null;
            }
        }

        private void RefreshTrayIcon()
        {
            try
            {
                _icon.Visible = false;
                _icon.Icon = LoadIcon();
                _icon.Text = ServerRuntimeState.HttpEnabled
                    ? "MCP VS2010: Running"
                    : "MCP VS2010: Stopped";
                _icon.Visible = true;
            }
            catch
            {
                // Notification-area refresh is best effort; Config must still open.
            }
        }

        private static void FocusConfigWindow(Form form)
        {
            if (!form.IsHandleCreated) form.CreateControl();
            ShowWindowAsync(form.Handle, SwShownormal);
            SetWindowPos(form.Handle, HwndTop, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpShowwindow);
            form.BringToFront();
            BringWindowToTop(form.Handle);
            form.Activate();
            SetForegroundWindow(form.Handle);
        }

        private void ExitServer()
        {
            _lifetime.StopApplication();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closing = true;
                try { _visibilityTimer.Stop(); _visibilityTimer.Dispose(); } catch { }
                try { _configRequestTimer.Stop(); _configRequestTimer.Dispose(); } catch { }
                _icon.Visible = false;
                _icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TrayVisibilityForm : Form
    {
        private readonly CheckBox _doNotShowAgain = new CheckBox();
        public bool DoNotShowAgain => _doNotShowAgain.Checked;
        public bool OpenSettingsRequested { get; private set; }

        public TrayVisibilityForm()
        {
            Text = "MCP server";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(480, 210);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
            MaximizeBox = MinimizeBox = false;

            Controls.Add(new Label
            {
                Text = "The MCP server tray icon is hidden.\r\n\r\n" +
                       "In the settings window, expand Other system tray icons\r\n" +
                       "and turn on McpVs2010.Server.\r\n\r\n" +
                       "Open the taskbar settings now?",
                Left = 20, Top = 18, Width = 440, Height = 90,
                AutoSize = false
            });
            _doNotShowAgain.Text = "Do not show this message again";
            _doNotShowAgain.Left = 20; _doNotShowAgain.Top = 120; _doNotShowAgain.AutoSize = true;
            Controls.Add(_doNotShowAgain);
            var yes = new Button { Text = "YES", Left = 285, Top = 160, Width = 75 };
            var no = new Button { Text = "NO", Left = 370, Top = 160, Width = 75 };
            yes.Click += (_, _) => { OpenSettingsRequested = true; Close(); };
            no.Click += (_, _) => Close();
            Controls.Add(yes); Controls.Add(no);
            AcceptButton = yes; CancelButton = no;
        }
    }


}
