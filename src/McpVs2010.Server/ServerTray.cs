using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;
using System.IO.Pipes;
using System.Text;

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
                ShowTrayVisibilityPromptIfNeeded();
            };
            _visibilityTimer.Start();
            _configRequestTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _configRequestTimer.Tick += (_, _) =>
            {
                if (Interlocked.Exchange(ref _configRequested, 0) != 0)
                    ShowConfig();
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
                    bool success = command?.IndexOf("show-config", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (success) Interlocked.Exchange(ref _configRequested, 1);
                    writer.WriteLine(success ? "{\"success\":true}" : "{\"success\":false}");
                }
                catch (ThreadInterruptedException) { return; }
                catch (IOException) when (_closing) { return; }
                catch { if (_closing) return; }
            }
        }

        private static void ShowTrayVisibilityPromptIfNeeded()
        {
            try
            {
                if (IsTrayVisibilityPromptDisabled() || !IsTrayIconHidden()) return;
                using var dialog = new TrayVisibilityForm();
                if (dialog.ShowDialog() == DialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });
                }
                if (dialog.DoNotShowAgain) SetTrayVisibilityPromptDisabled();
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
                if (string.IsNullOrWhiteSpace(itemPath) ||
                    (!string.IsNullOrWhiteSpace(executablePath) &&
                     !string.Equals(itemPath, executablePath, StringComparison.OrdinalIgnoreCase))) continue;
                matchingEntryFound = true;
                if (item?.GetValue("IsPromoted") is int promoted && promoted == 0) return true;
            }
            return !matchingEntryFound;
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
                string path = Path.Combine(AppContext.BaseDirectory, "vs2010.ico");
                if (File.Exists(path)) return new Icon(path);
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
                       "To show it, enable MCP VS2010 in Other system tray icons.\r\n\r\n" +
                       "Open the Other system tray icons settings now?",
                Left = 20, Top = 18, Width = 440, Height = 90,
                AutoSize = false
            });
            _doNotShowAgain.Text = "Do not show this message again";
            _doNotShowAgain.Left = 20; _doNotShowAgain.Top = 120; _doNotShowAgain.AutoSize = true;
            Controls.Add(_doNotShowAgain);
            var yes = new Button { Text = "YES", DialogResult = DialogResult.Yes, Left = 285, Top = 160, Width = 75 };
            var no = new Button { Text = "NO", DialogResult = DialogResult.No, Left = 370, Top = 160, Width = 75 };
            Controls.Add(yes); Controls.Add(no);
            AcceptButton = yes; CancelButton = no;
        }
    }

    private sealed class ConfigForm : Form
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly Label _status = new Label();
        private readonly TextBox _port = new TextBox();
        private readonly ListBox _bridges = new ListBox();
        private readonly CheckBox _startup = new CheckBox();
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
            ClientSize = new Size(520, 330);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            MaximizeBox = MinimizeBox = false;
            Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);

            Controls.Add(new Label { Text = "Server status:", Left = 20, Top = 22, AutoSize = true });
            _status.Left = 155; _status.Top = 22; _status.AutoSize = true;
            Controls.Add(_status);
            Controls.Add(new Label { Text = "Server port:", Left = 20, Top = 58, AutoSize = true });
            _port.Left = 155; _port.Top = 54; _port.Width = 100;
            Controls.Add(_port);
            _apply.Text = "Apply"; _apply.Left = 270; _apply.Top = 52; _apply.Width = 80;
            _apply.Click += ApplyPort; Controls.Add(_apply);
            Controls.Add(new Label { Text = "Connected bridges:", Left = 20, Top = 95, AutoSize = true });
            _bridges.Left = 20; _bridges.Top = 120; _bridges.Width = 480; _bridges.Height = 120;
            Controls.Add(_bridges);
            _startup.Text = "Start MCP server when Windows starts"; _startup.Left = 20; _startup.Top = 255; _startup.AutoSize = true;
            _startup.CheckedChanged += StartupChanged; Controls.Add(_startup);
            _toggle.Text = "STOP"; _toggle.Left = 320; _toggle.Top = 16; _toggle.Width = 70;
            _toggle.Click += (_, _) => ToggleServer(); Controls.Add(_toggle);
            _restart.Text = "RESTART"; _restart.Left = 400; _restart.Top = 16; _restart.Width = 100;
            _restart.Click += (_, _) => RestartServer(); Controls.Add(_restart);
            RefreshView();
        }

        private void RefreshView()
        {
            bool running = ServerRuntimeState.HttpEnabled && !_lifetime.ApplicationStopping.IsCancellationRequested;
            _status.Text = running ? "Running" : "Stopped";
            _status.ForeColor = running ? Color.DarkGreen : Color.DarkRed;
            _toggle.Text = running ? "STOP" : "START";
            _toggle.Enabled = !_restarting;
            _restart.Enabled = !_restarting;
            _port.Enabled = !_restarting;
            _apply.Enabled = !_restarting;
            _port.Text = ReadPort().ToString();
            _startup.Checked = IsStartupEnabled();
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

        private static int ReadPort() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\McpVs2010"); return key?.GetValue("HttpStreamPort") is int port && port > 0 && port <= 65535 ? port : 3010; }
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
