using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;
using System.IO.Pipes;
using System.Text;
using System.Reflection;

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
                    bool success = showConfig || checkTray;
                    if (showConfig) Interlocked.Exchange(ref _configRequested, 1);
                    if (checkTray) Interlocked.Exchange(ref _trayCheckRequested, 1);
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
            if (_configForm != null && !_configForm.IsDisposed)
            {
                if (_configForm.WindowState == FormWindowState.Minimized)
                    _configForm.WindowState = FormWindowState.Normal;
                _configForm.Activate();
                return;
            }

            _configForm = new ConfigForm(_lifetime);
            try { _configForm.ShowDialog(); }
            finally
            {
                _configForm.Dispose();
                _configForm = null;
            }
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

    private sealed class ConfigForm : Form
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly Label _status = new Label();
        private readonly Label _url = new Label();
        private readonly TextBox _port = new TextBox();
        private readonly ListBox _bridges = new ListBox();
        private readonly CheckBox _startup = new CheckBox();
        private readonly CheckBox _trayPrompt = new CheckBox();
        private readonly Button _toggle = new Button();
        private readonly Button _apply = new Button();
        private readonly Button _restart = new Button();
        private bool _restarting;

        public ConfigForm(IHostApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
            Text = "MCP Server Configuration";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(520, 390);
            MaximizeBox = MinimizeBox = false;
            Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);

            Controls.Add(new Label { Text = "Server URL:", Left = 20, Top = 22, AutoSize = true });
            _url.Left = 155; _url.Top = 22; _url.Width = 275; _url.AutoSize = false;
            Controls.Add(_url);
            var copy = new Button { Text = "copy", Left = 440, Top = 17, Width = 60 };
            copy.Click += (_, _) => Clipboard.SetText(_url.Text); Controls.Add(copy);
            Controls.Add(new Label { Text = "Server status:", Left = 20, Top = 62, AutoSize = true });
            _status.Left = 155; _status.Top = 62; _status.AutoSize = true;
            Controls.Add(_status);
            Controls.Add(new Label { Text = "Server port:", Left = 20, Top = 98, AutoSize = true });
            _port.Left = 155; _port.Top = 94; _port.Width = 100;
            Controls.Add(_port);
            _apply.Text = "Apply"; _apply.Left = 270; _apply.Top = 92; _apply.Width = 80;
            _apply.Click += ApplyPort; Controls.Add(_apply);
            Controls.Add(new Label { Text = "Connected bridges:", Left = 20, Top = 135, AutoSize = true });
            _bridges.Left = 20; _bridges.Top = 160; _bridges.Width = 480; _bridges.Height = 110;
            Controls.Add(_bridges);
            _startup.Text = "Start MCP server when Windows starts"; _startup.Left = 20; _startup.Top = 285; _startup.AutoSize = true;
            _startup.CheckedChanged += StartupChanged; Controls.Add(_startup);
            _trayPrompt.Text = "Always check whether the tray icon is hidden"; _trayPrompt.Left = 20; _trayPrompt.Top = 312; _trayPrompt.AutoSize = true;
            _trayPrompt.CheckedChanged += TrayPromptChanged; Controls.Add(_trayPrompt);
            _toggle.Text = "STOP"; _toggle.Left = 320; _toggle.Top = 56; _toggle.Width = 70;
            _toggle.Click += (_, _) => ToggleServer(); Controls.Add(_toggle);
            _restart.Text = "RESTART"; _restart.Left = 400; _restart.Top = 56; _restart.Width = 100;
            _restart.Click += (_, _) => RestartServer(); Controls.Add(_restart);
            var close = new Button { Text = "Close", Left = 415, Top = 345, Width = 85, DialogResult = DialogResult.Cancel };
            Controls.Add(close); CancelButton = close;
            RefreshView();
        }

        private void RefreshView()
        {
            bool running = ServerRuntimeState.HttpEnabled && !_lifetime.ApplicationStopping.IsCancellationRequested;
            _status.Text = running ? "Running" : "Stopped";
            _status.ForeColor = running ? Color.DarkGreen : Color.DarkRed;
            _url.Text = $"http://127.0.0.1:{ReadPort()}/stream";
            _toggle.Text = running ? "STOP" : "START";
            _toggle.Enabled = !_restarting;
            _restart.Enabled = !_restarting;
            _port.Enabled = !_restarting;
            _apply.Enabled = !_restarting;
            _port.Text = ReadPort().ToString();
            _startup.Checked = IsStartupEnabled();
            _trayPrompt.Checked = !IsTrayVisibilityPromptDisabled();
            _bridges.Items.Clear();
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "McpVs2010", "instances");
            if (Directory.Exists(dir)) foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try { using var doc = JsonDocument.Parse(File.ReadAllText(file)); var root = doc.RootElement; _bridges.Items.Add($"PID {root.GetProperty("processId").GetInt32()}  {root.GetProperty("solutionPath").GetString()}"); } catch { }
            }
        }

        private void ApplyPort(object? sender, EventArgs e)
        {
            if (!int.TryParse(_port.Text, out int port) || port < 1 || port > 65535) { MessageBox.Show("Port must be an integer from 1 to 65535."); return; }
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\McpVs2010"); key.SetValue("HttpStreamPort", port, RegistryValueKind.DWord); RefreshView();
        }

        private async void ToggleServer()
        {
            if (ServerRuntimeState.HttpEnabled)
            {
                ServerRuntimeState.SetHttpEnabled(false);
                RefreshView();
            }
            else
            {
                await StartServerAsync();
            }
        }

        private void StartupChanged(object? sender, EventArgs e)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (_startup.Checked)
            {
                string executablePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Environment.ProcessPath
                    ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(executablePath))
                    key.SetValue("McpVs2010.Server", '"' + executablePath + '"');
            }
            else key.DeleteValue("McpVs2010.Server", false);
        }

        private void TrayPromptChanged(object? sender, EventArgs e)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\McpVs2010");
            key?.SetValue("TrayVisibilityPromptDisabled", _trayPrompt.Checked ? 0 : 1, RegistryValueKind.DWord);
        }

        private static int ReadPort() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\McpVs2010"); return key?.GetValue("HttpStreamPort") is int port && port > 0 && port <= 65535 ? port : 3010; }
        private static bool IsTrayVisibilityPromptDisabled()
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\McpVs2010");
            object? value = key?.GetValue("TrayVisibilityPromptDisabled");
            return value is int disabled && disabled == 1;
        }
        private static bool IsStartupEnabled() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("McpVs2010.Server") != null; }
        private async Task StartServerAsync()
        {
            try
            {
                ServerRuntimeState.SetHttpEnabled(true);
                if (!await ProbeHttpServerAsync())
                {
                    ServerRuntimeState.SetHttpEnabled(false);
                    RefreshView();
                    MessageBox.Show("The MCP HTTP server did not respond after startup.", "MCP Config");
                    return;
                }
                RefreshView();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "MCP Config"); }
        }

        private async void RestartServer()
        {
            if (_restarting) return;

            _restarting = true;
            ServerRuntimeState.SetHttpEnabled(false);
            RefreshView();
            try
            {
                await Task.Delay(3000);
                if (!_lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    ServerRuntimeState.SetHttpEnabled(true);
                    if (!await ProbeHttpServerAsync())
                    {
                        ServerRuntimeState.SetHttpEnabled(false);
                        MessageBox.Show("The MCP HTTP server failed to restart after 3 seconds.", "MCP Config");
                    }
                }
            }
            finally
            {
                _restarting = false;
                if (!IsDisposed) RefreshView();
            }
        }

        private static async Task<bool> ProbeHttpServerAsync()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    $"http://127.0.0.1:{ReadPort()}/health");
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
        }
    }
}
