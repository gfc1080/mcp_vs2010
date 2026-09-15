using System.Drawing;
using System.Windows.Forms;

namespace McpVs2010.Server;

public sealed partial class ConfigForm
{
    private void InitializeComponent()
    {
        label1 = new Label();
        copy = new Button();
        label2 = new Label();
        label3 = new Label();
        label4 = new Label();
        label5 = new Label();
        browse = new Button();
        close = new Button();
        _status = new Label();
        _url = new Label();
        _port = new TextBox();
        _workingFolder = new TextBox();
        _bridges = new ListBox();
        _startup = new CheckBox();
        _trayPrompt = new CheckBox();
        _toggle = new Button();
        _apply = new Button();
        _restart = new Button();
        tableLayoutPanel1 = new TableLayoutPanel();
        tableLayoutPanel2 = new TableLayoutPanel();
        label6 = new Label();
        label7 = new Label();
        _status_codex = new Label();
        _status_claude = new Label();
        _btn_config_codex = new Button();
        _btn_config_claude = new Button();
        tableLayoutPanel1.SuspendLayout();
        tableLayoutPanel2.SuspendLayout();
        SuspendLayout();
        // 
        // label1
        // 
        label1.AutoSize = true;
        label1.Dock = DockStyle.Fill;
        label1.Location = new Point(5, 2);
        label1.MinimumSize = new Size(120, 0);
        label1.Name = "label1";
        label1.Size = new Size(157, 39);
        label1.TabIndex = 0;
        label1.Text = "Server URL:";
        label1.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // copy
        // 
        copy.AutoSize = true;
        copy.Dock = DockStyle.Fill;
        copy.Location = new Point(537, 5);
        copy.Name = "copy";
        copy.Size = new Size(172, 33);
        copy.TabIndex = 1;
        copy.Text = "Copy";
        copy.Click += Copy_Click;
        // 
        // label2
        // 
        label2.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        label2.AutoSize = true;
        label2.Location = new Point(5, 41);
        label2.MinimumSize = new Size(120, 0);
        label2.Name = "label2";
        label2.Size = new Size(157, 39);
        label2.TabIndex = 2;
        label2.Text = "Server status:";
        label2.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // label3
        // 
        label3.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        label3.Location = new Point(5, 80);
        label3.MinimumSize = new Size(120, 0);
        label3.Name = "label3";
        label3.Size = new Size(157, 39);
        label3.TabIndex = 3;
        label3.Text = "Server port:";
        label3.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // label4
        // 
        label4.Dock = DockStyle.Fill;
        label4.Location = new Point(5, 119);
        label4.MinimumSize = new Size(120, 0);
        label4.Name = "label4";
        label4.Size = new Size(157, 39);
        label4.TabIndex = 4;
        label4.Text = "Working folder:";
        label4.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // label5
        // 
        label5.AutoSize = true;
        label5.Location = new Point(20, 272);
        label5.Name = "label5";
        label5.Size = new Size(141, 20);
        label5.TabIndex = 5;
        label5.Text = "Connected bridges:";
        // 
        // browse
        // 
        browse.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        browse.Location = new Point(537, 122);
        browse.Name = "browse";
        browse.Size = new Size(172, 33);
        browse.TabIndex = 6;
        browse.Text = "Browse";
        browse.Click += BrowseWorkingFolder;
        // 
        // close
        // 
        close.AutoSize = true;
        close.DialogResult = DialogResult.Cancel;
        close.Location = new Point(648, 448);
        close.Name = "close";
        close.Size = new Size(85, 30);
        close.TabIndex = 7;
        close.Text = "Close";
        // 
        // _status
        // 
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _status.AutoSize = true;
        _status.Location = new Point(168, 41);
        _status.MinimumSize = new Size(120, 0);
        _status.Name = "_status";
        _status.Size = new Size(363, 39);
        _status.TabIndex = 8;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _url
        // 
        _url.Dock = DockStyle.Fill;
        _url.Location = new Point(168, 2);
        _url.Name = "_url";
        _url.Size = new Size(363, 39);
        _url.TabIndex = 9;
        _url.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _port
        // 
        _port.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        _port.Location = new Point(168, 83);
        _port.Name = "_port";
        _port.Size = new Size(100, 27);
        _port.TabIndex = 10;
        _port.Text = "3010";
        // 
        // _workingFolder
        // 
        _workingFolder.Dock = DockStyle.Fill;
        _workingFolder.Location = new Point(168, 122);
        _workingFolder.MaxLength = 255;
        _workingFolder.Name = "_workingFolder";
        _workingFolder.PlaceholderText = "Working folder must be specified";
        _workingFolder.ReadOnly = true;
        _workingFolder.Size = new Size(363, 27);
        _workingFolder.TabIndex = 11;
        _workingFolder.WordWrap = false;
        _workingFolder.Leave += WorkingFolder_Leave;
        // 
        // _bridges
        // 
        _bridges.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _bridges.Location = new Point(20, 300);
        _bridges.Name = "_bridges";
        _bridges.Size = new Size(712, 104);
        _bridges.TabIndex = 12;
        // 
        // _startup
        // 
        _startup.AutoSize = true;
        _startup.Location = new Point(20, 425);
        _startup.Name = "_startup";
        _startup.Size = new Size(293, 24);
        _startup.TabIndex = 13;
        _startup.Text = "Start MCP server when Windows starts";
        _startup.CheckedChanged += StartupChanged;
        // 
        // _trayPrompt
        // 
        _trayPrompt.AutoSize = true;
        _trayPrompt.Location = new Point(20, 452);
        _trayPrompt.Name = "_trayPrompt";
        _trayPrompt.Size = new Size(340, 24);
        _trayPrompt.TabIndex = 14;
        _trayPrompt.Text = "Always check whether the tray icon is hidden";
        _trayPrompt.CheckedChanged += TrayPromptChanged;
        // 
        // _toggle
        // 
        _toggle.Dock = DockStyle.Fill;
        _toggle.Location = new Point(0, 0);
        _toggle.Margin = new Padding(0);
        _toggle.Name = "_toggle";
        _toggle.Size = new Size(86, 33);
        _toggle.TabIndex = 15;
        _toggle.Text = "STOP";
        _toggle.Click += Toggle_Click;
        // 
        // _apply
        // 
        _apply.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _apply.Location = new Point(537, 83);
        _apply.Name = "_apply";
        _apply.Size = new Size(172, 33);
        _apply.TabIndex = 16;
        _apply.Text = "Apply";
        _apply.Click += ApplyPort;
        // 
        // _restart
        // 
        _restart.Dock = DockStyle.Fill;
        _restart.Location = new Point(86, 0);
        _restart.Margin = new Padding(0);
        _restart.Name = "_restart";
        _restart.Size = new Size(86, 33);
        _restart.TabIndex = 17;
        _restart.Text = "RESTART";
        _restart.Click += Restart_Click;
        // 
        // tableLayoutPanel1
        // 
        tableLayoutPanel1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        tableLayoutPanel1.AutoSize = true;
        tableLayoutPanel1.ColumnCount = 3;
        tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23F));
        tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));
        tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        tableLayoutPanel1.Controls.Add(tableLayoutPanel2, 2, 1);
        tableLayoutPanel1.Controls.Add(label4, 0, 3);
        tableLayoutPanel1.Controls.Add(browse, 2, 3);
        tableLayoutPanel1.Controls.Add(label3, 0, 2);
        tableLayoutPanel1.Controls.Add(label1, 0, 0);
        tableLayoutPanel1.Controls.Add(label2, 0, 1);
        tableLayoutPanel1.Controls.Add(copy, 2, 0);
        tableLayoutPanel1.Controls.Add(_port, 1, 2);
        tableLayoutPanel1.Controls.Add(_url, 1, 0);
        tableLayoutPanel1.Controls.Add(_status, 1, 1);
        tableLayoutPanel1.Controls.Add(_apply, 2, 2);
        tableLayoutPanel1.Controls.Add(_workingFolder, 1, 3);
        tableLayoutPanel1.Controls.Add(label6, 0, 4);
        tableLayoutPanel1.Controls.Add(label7, 0, 5);
        tableLayoutPanel1.Controls.Add(_status_codex, 1, 4);
        tableLayoutPanel1.Controls.Add(_status_claude, 1, 5);
        tableLayoutPanel1.Controls.Add(_btn_config_codex, 2, 4);
        tableLayoutPanel1.Controls.Add(_btn_config_claude, 2, 5);
        tableLayoutPanel1.Location = new Point(20, 20);
        tableLayoutPanel1.Name = "tableLayoutPanel1";
        tableLayoutPanel1.Padding = new Padding(2);
        tableLayoutPanel1.RowCount = 6;
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 16.66667F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 16.666666F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 16.666666F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 16.666666F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 16.666666F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 16.666666F));
        tableLayoutPanel1.Size = new Size(714, 238);
        tableLayoutPanel1.TabIndex = 18;
        // 
        // tableLayoutPanel2
        // 
        tableLayoutPanel2.ColumnCount = 2;
        tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        tableLayoutPanel2.Controls.Add(_toggle, 0, 0);
        tableLayoutPanel2.Controls.Add(_restart, 1, 0);
        tableLayoutPanel2.Dock = DockStyle.Fill;
        tableLayoutPanel2.Location = new Point(537, 44);
        tableLayoutPanel2.Name = "tableLayoutPanel2";
        tableLayoutPanel2.RowCount = 1;
        tableLayoutPanel2.RowStyles.Add(new RowStyle());
        tableLayoutPanel2.Size = new Size(172, 33);
        tableLayoutPanel2.TabIndex = 19;
        // 
        // label6
        // 
        label6.AutoSize = true;
        label6.Dock = DockStyle.Fill;
        label6.Location = new Point(5, 158);
        label6.Name = "label6";
        label6.Size = new Size(157, 39);
        label6.TabIndex = 20;
        label6.Text = "Open AI Codex";
        label6.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // label7
        // 
        label7.AutoSize = true;
        label7.Dock = DockStyle.Fill;
        label7.Location = new Point(5, 197);
        label7.Name = "label7";
        label7.Size = new Size(157, 39);
        label7.TabIndex = 21;
        label7.Text = "Claude";
        label7.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _status_codex
        // 
        _status_codex.AutoSize = true;
        _status_codex.Dock = DockStyle.Fill;
        _status_codex.Location = new Point(168, 158);
        _status_codex.MinimumSize = new Size(120, 0);
        _status_codex.Name = "_status_codex";
        _status_codex.Size = new Size(363, 39);
        _status_codex.TabIndex = 22;
        _status_codex.Text = "registered";
        _status_codex.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _status_claude
        // 
        _status_claude.AutoSize = true;
        _status_claude.Dock = DockStyle.Fill;
        _status_claude.Location = new Point(168, 197);
        _status_claude.MinimumSize = new Size(120, 0);
        _status_claude.Name = "_status_claude";
        _status_claude.Size = new Size(363, 39);
        _status_claude.TabIndex = 23;
        _status_claude.Text = "registered";
        _status_claude.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _btn_config_codex
        // 
        _btn_config_codex.Dock = DockStyle.Fill;
        _btn_config_codex.Location = new Point(537, 161);
        _btn_config_codex.Name = "_btn_config_codex";
        _btn_config_codex.Size = new Size(172, 33);
        _btn_config_codex.TabIndex = 24;
        _btn_config_codex.Text = "AutoConfig";
        _btn_config_codex.UseVisualStyleBackColor = true;
        _btn_config_codex.Click += ConfigCodex_Click;
        // 
        // _btn_config_claude
        // 
        _btn_config_claude.Dock = DockStyle.Fill;
        _btn_config_claude.Location = new Point(537, 200);
        _btn_config_claude.Name = "_btn_config_claude";
        _btn_config_claude.Size = new Size(172, 33);
        _btn_config_claude.TabIndex = 25;
        _btn_config_claude.Text = "AutoConfig";
        _btn_config_claude.UseVisualStyleBackColor = true;
        _btn_config_claude.Click += ConfigClaude_Click;
        // 
        // ConfigForm
        // 
        AutoScaleDimensions = new SizeF(120F, 120F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        CancelButton = close;
        ClientSize = new Size(749, 495);
        Controls.Add(tableLayoutPanel1);
        Controls.Add(label5);
        Controls.Add(close);
        Controls.Add(_bridges);
        Controls.Add(_startup);
        Controls.Add(_trayPrompt);
        Name = "ConfigForm";
        SizeGripStyle = SizeGripStyle.Hide;
        tableLayoutPanel1.ResumeLayout(false);
        tableLayoutPanel1.PerformLayout();
        tableLayoutPanel2.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();

    }
    private Label label1;
    private Label label2;
    private Label label3;
    private Label label4;
    private Label label5;

    private Button copy;
    private Button browse;
    private Button close;

    private Label _status;
    private  Label _url;
    private  TextBox _port;
    private  TextBox _workingFolder;
    private  ListBox _bridges;
    private  CheckBox _startup;
    private  CheckBox _trayPrompt;
    private  Button _toggle;
    private  Button _apply;
    private  Button _restart;
    private TableLayoutPanel tableLayoutPanel1;
    private TableLayoutPanel tableLayoutPanel2;
    private Label label6;
    private Label label7;
    private Label _status_codex;
    private Label _status_claude;
    private Button _btn_config_codex;
    private Button _btn_config_claude;
}
