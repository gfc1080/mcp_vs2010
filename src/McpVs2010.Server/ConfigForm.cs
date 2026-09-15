using System.Drawing;
using System.Windows.Forms;
using System.ComponentModel;
using System.Reflection;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace McpVs2010.Server;

[DesignerCategory("Form")]
public sealed partial class ConfigForm : Form
{
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly System.Windows.Forms.Timer _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    private bool _restarting;

    public ConfigForm() : this(null)
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        Text = "MCP Server Configuration (v" + (version == null ? "unknown" : version.ToString(3)) + ")";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(520, 435);
        MaximizeBox = MinimizeBox = false;
        Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);

        bool designTime = LicenseManager.UsageMode == LicenseUsageMode.Designtime;
        InitializeComponent();
    }

    public ConfigForm(IHostApplicationLifetime? lifetime)
    {
        _lifetime = lifetime;
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        Text = "MCP Server Configuration (v" + (version == null ? "unknown" : version.ToString(3)) + ")";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(520, 435);
        MaximizeBox = MinimizeBox = false;
        Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);

        bool designTime = LicenseManager.UsageMode == LicenseUsageMode.Designtime;
        InitializeComponent();
        if (!designTime) _workingFolder.Text = ServerSettings.ReadWorkingFolderOrEmpty();
        _refreshTimer.Tick += (_, _) => RefreshView();
        if (!designTime && _lifetime != null)
        {
            _refreshTimer.Start();
            RefreshView();
        }
        else
        {
            _url.Text = "http://127.0.0.1:3010/stream";
            _status.Text = "Stopped";
            _status.ForeColor = Color.DarkRed;
        }
    }

    private void Copy_Click(object? sender, EventArgs e) => Clipboard.SetText(_url.Text);
    private void WorkingFolder_Leave(object? sender, EventArgs e) => SaveWorkingFolderFromText(false);
    private void Toggle_Click(object? sender, EventArgs e) => ToggleServer();
    private void Restart_Click(object? sender, EventArgs e) => RestartServer();

    private async void ConfigCodex_Click(object? sender, EventArgs e)
        => await RunAutoConfigAsync(_btn_config_codex, ConfigureCodex);

    private async void ConfigClaude_Click(object? sender, EventArgs e)
        => await RunAutoConfigAsync(_btn_config_claude, ConfigureClaude);

    private async Task RunAutoConfigAsync(Button button, Action configure)
    {
        // Disable immediately so repeated clicks cannot start concurrent writes.
        button.Enabled = false;
        try
        {
            configure();
            RefreshView();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "MCP Config");
        }
        finally
        {
            await Task.Delay(1000);
            if (!IsDisposed && !button.IsDisposed)
                button.Enabled = true;
        }
    }

    private void ConfigureCodex()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Path.Combine(profile, ".codex", "config.toml");
        if (!File.Exists(path))
            throw new FileNotFoundException("Codex config.toml was not found:\r\n" + path, path);
        McpConfigFileWriter.EnsureCodexRegistered(ReadPort());
    }

    private void ConfigureClaude()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Path.Combine(profile, ".claude.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Claude .claude.json was not found:\r\n" + path, path);
        McpConfigFileWriter.EnsureClaudeRegistered(ReadPort());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer.Dispose();
        base.Dispose(disposing);
    }

    private void RefreshView()
    {
        bool running = ServerRuntimeState.HttpEnabled && !(_lifetime?.ApplicationStopping.IsCancellationRequested ?? false);
        _status.Text = running ? "Running" : "Stopped";
        _status.ForeColor = running ? Color.DarkGreen : Color.DarkRed;
        _url.Text = $"http://127.0.0.1:{ReadPort()}/stream";
        bool codexRegistered = McpConfigFileWriter.IsCodexRegistered(ReadPort());
        _status_codex.Text = codexRegistered ? "registered" : "not registered";
        _status_codex.ForeColor = codexRegistered ? Color.DarkGreen : Color.Gray;
        bool claudeRegistered = McpConfigFileWriter.IsClaudeRegistered(ReadPort());
        _status_claude.Text = claudeRegistered ? "registered" : "not registered";
        _status_claude.ForeColor = claudeRegistered ? Color.DarkGreen : Color.Gray;
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
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                int processId = root.GetProperty("processId").GetInt32();
                string solutionPath = root.TryGetProperty("solutionPath", out var pathElement) &&
                    pathElement.ValueKind == JsonValueKind.String
                    ? pathElement.GetString() ?? "<No solution loaded>"
                    : "<No solution loaded>";
                _bridges.Items.Add($"PID {processId}  {solutionPath}");
            }
            catch { }
        }
    }

    private void ApplyPort(object? sender, EventArgs e)
    {
        if (!int.TryParse(_port.Text, out int port) || port < 1 || port > 65535) { MessageBox.Show("Port must be an integer from 1 to 65535."); return; }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\McpVs2010"); key.SetValue("HttpStreamPort", port, RegistryValueKind.DWord); RefreshView();
    }

    private void BrowseWorkingFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the MCP server working folder",
                SelectedPath = Directory.Exists(_workingFolder.Text) ? _workingFolder.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                ServerSettings.WriteWorkingFolder(dialog.SelectedPath);
                    _workingFolder.Text = ServerSettings.ReadWorkingFolderOrEmpty();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "MCP Config"); }
        }
    }

    private void SaveWorkingFolderFromText(bool showError)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_workingFolder.Text))
                throw new InvalidOperationException("Working folder must not be empty.");
            ServerSettings.WriteWorkingFolder(_workingFolder.Text.Trim());
                _workingFolder.Text = ServerSettings.ReadWorkingFolderOrEmpty();
        }
        catch (Exception ex)
        {
                _workingFolder.Text = ServerSettings.ReadWorkingFolderOrEmpty();
            if (showError) MessageBox.Show(ex.Message, "MCP Config");
        }
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
            if (!(_lifetime?.ApplicationStopping.IsCancellationRequested ?? false))
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
