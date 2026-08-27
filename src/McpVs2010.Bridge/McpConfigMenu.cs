using System;
using System.Drawing;
using System.Windows.Forms;
using EnvDTE80;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.Win32;

namespace McpVs2010.Bridge
{
    internal sealed class McpConfigMenu : IDisposable
    {
        private const string RegistryPath = @"Software\McpVs2010";
        private const string RegistryValue = "HttpStreamPort";
        private readonly Func<McpServerProcess> _getServer;
        private readonly Action<McpServerProcess> _setServer;
        private readonly CommandBarButton _menuItem;

        private McpConfigMenu(DTE2 dte, Func<McpServerProcess> getServer, Action<McpServerProcess> setServer)
        {
            _getServer = getServer; _setServer = setServer;
            CommandBars bars = (CommandBars)dte.CommandBars;
            CommandBar tools = bars["Tools"];
            _menuItem = (CommandBarButton)tools.Controls.Add(MsoControlType.msoControlButton, Type.Missing, Type.Missing, Type.Missing, true);
            _menuItem.Caption = "MCP server";
            _menuItem.Click += OpenDialog;
        }

        internal static McpConfigMenu Create(DTE2 dte, Func<McpServerProcess> getServer, Action<McpServerProcess> setServer)
        { return new McpConfigMenu(dte, getServer, setServer); }

        private void OpenDialog(CommandBarButton Ctrl, ref bool CancelDefault)
        { using (var dialog = new ServerDialog(_getServer, _setServer)) dialog.ShowDialog(); }

        public void Dispose()
        { try { if (_menuItem != null) _menuItem.Delete(true); } catch { } }

        private sealed class ServerDialog : Form
        {
            private readonly Func<McpServerProcess> _getServer;
            private readonly Action<McpServerProcess> _setServer;
            private readonly Label _url = new Label();
            private readonly Label _status = new Label();
            private readonly TextBox _port = new TextBox();
            private readonly Button _stop = new Button();
            private readonly Button _start = new Button();

            internal ServerDialog(Func<McpServerProcess> getServer, Action<McpServerProcess> setServer)
            {
                _getServer = getServer; _setServer = setServer;
                Text = "MCP server"; FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterScreen;
                AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96F, 96F);
                Font = CreateFixedFont();
                MinimizeBox = false; MaximizeBox = false; ClientSize = new Size(500, 250);
                var group = new GroupBox { Text = "MCP server", Left = 10, Top = 10, Width = 480, Height = 225 };
                var urlLabel = new Label { Text = "Server URL:", AutoSize = true, Left = 15, Top = 25 };
                _url.AutoSize = false; _url.Left = 125; _url.Top = 25; _url.Width = 275; _url.Height = 25;
                var copy = new Button { Text = "copy", Left = 410, Top = 20, Width = 55, Height = 30 };
                var stateLabel = new Label { Text = "Server status:", AutoSize = true, Left = 15, Top = 70 };
                _status.AutoSize = true; _status.Left = stateLabel.Right + 12; _status.Top = 70;
                var portLabel = new Label { Text = "Port:", AutoSize = true, Left = 15, Top = 105 };
                _port.Left = 125; _port.Top = 101; _port.Width = 120; _port.Height = 28;
                var apply = new Button { Text = "Apply", Left = 260, Top = 99, Width = 85, Height = 30 };
                _stop.Text = "STOP"; _stop.Left = 125; _stop.Top = 150; _stop.Width = 90; _stop.Height = 30; _stop.Click += StopServer;
                _start.Text = "START"; _start.Left = 225; _start.Top = 150; _start.Width = 90; _start.Height = 30; _start.Click += StartServer;
                var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Left = 380, Top = 190, Width = 85, Height = 30 };
                apply.Click += ApplyPort;
                copy.Click += CopyUrl;
                group.Controls.Add(urlLabel); group.Controls.Add(_url); group.Controls.Add(copy); group.Controls.Add(stateLabel); group.Controls.Add(_status); group.Controls.Add(portLabel); group.Controls.Add(_port); group.Controls.Add(apply); group.Controls.Add(_stop); group.Controls.Add(_start); group.Controls.Add(close);
                Controls.Add(group);
                CancelButton = close;
                RefreshView();
            }

            private void RefreshView()
            {
                int port = ReadPort();
                _port.Text = port.ToString();
                _url.Text = "http://127.0.0.1:" + port + "/stream";
                bool running = _getServer() != null && _getServer().IsRunning;
                _status.Text = running ? "Running" : "Stopped";
                _status.ForeColor = running ? Color.DarkGreen : Color.DarkRed;
                _stop.Enabled = running; _start.Enabled = !running;
            }

            private void StopServer(object sender, EventArgs e)
            { var server = _getServer(); if (server != null) server.Stop(); RefreshView(); }

            private void CopyUrl(object sender, EventArgs e)
            { Clipboard.SetText(_url.Text); }

            private void StartServer(object sender, EventArgs e)
            {
                try { if (_getServer() == null || !_getServer().IsRunning) _setServer(McpServerProcess.Start()); RefreshView(); }
                catch (Exception ex) { MessageBox.Show("Failed to start the MCP server.\r\n" + ex.Message, "MCP server", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }

            private void ApplyPort(object sender, EventArgs e)
            {
                int port;
                if (!int.TryParse(_port.Text.Trim(), out port) || port < 1 || port > 65535)
                { MessageBox.Show("Port must be an integer from 1 to 65535.", "MCP server", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                try
                {
                    int oldPort = ReadPort();
                    using (var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32).CreateSubKey(RegistryPath)) key.SetValue(RegistryValue, port, RegistryValueKind.DWord);
                    var server = _getServer();
                    if (server != null && server.IsRunning && oldPort != port) { server.Stop(); _setServer(McpServerProcess.Start()); }
                    RefreshView();
                }
                catch (Exception ex) { MessageBox.Show("Failed to apply the port.\r\n" + ex.Message, "MCP server", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }

            private static int ReadPort()
            { using (var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32).OpenSubKey(RegistryPath, false)) { object value = key == null ? null : key.GetValue(RegistryValue); int port = value is int ? (int)value : 0; return port > 0 && port <= 65535 ? port : 3010; } }

            private static Font CreateFixedFont()
            {
                try { return new Font("Fixedsys", 10F, GraphicsUnit.Point); }
                catch { return new Font("Consolas", 10F, GraphicsUnit.Point); }
            }
        }
    }
}
