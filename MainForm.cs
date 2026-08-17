using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace BMS_Protocol_Simulator
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) {
                MessageBox.Show("程序发生未捕获异常:\n" + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "未知错误"), "严重错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) {
                MessageBox.Show("UI线程异常:\n" + (e.Exception != null ? e.Exception.ToString() : "未知错误"), "错误提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动异常:\n" + ex.ToString(), "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public enum ValidationLevel
    {
        Normal,     // 正常参数 (绿框)
        Extreme,    // 极限/边界参数 (黄框提示，允许生效)
        Invalid     // 非法/逻辑矛盾参数 (红框拦截，自动回滚)
    }

    // ── 默认值配置对话框 ──
    public class DefaultConfigDialog : Form
    {
        private NumericUpDown numDefVoltage, numDefCurrent, numDefTemp, numDefSoc, numDefSoh;
        private NumericUpDown numDefRemCap, numDefFullCap, numDefMaxChgI, numDefMaxDisI, numDefCvVolt;
        public BmsDefaultConfig ResultConfig { get; private set; }

        public DefaultConfigDialog(BmsDefaultConfig current)
        {
            ResultConfig = current ?? new BmsDefaultConfig();
            BuildDialogUI();
        }

        private void BuildDialogUI()
        {
            this.Text = "默认参数配置 (保存后下次启动生效)";
            this.ClientSize = new Size(500, 260);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);

            Panel pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = 46 };
            Button btnSave = new Button { Text = "保存为默认值", Width = 110, Height = 28, Location = new Point(135, 8), DialogResult = DialogResult.OK };
            btnSave.Click += delegate(object s, EventArgs e) {
                ResultConfig.Voltage = (double)numDefVoltage.Value;
                ResultConfig.Current = (double)numDefCurrent.Value;
                ResultConfig.Temperature = (double)numDefTemp.Value;
                ResultConfig.SOC = (double)numDefSoc.Value;
                ResultConfig.SOH = (double)numDefSoh.Value;
                ResultConfig.RemainingCapacity = (double)numDefRemCap.Value;
                ResultConfig.FullCapacity = (double)numDefFullCap.Value;
                ResultConfig.MaxChargeCurrent = (double)numDefMaxChgI.Value;
                ResultConfig.MaxDischargeCurrent = (double)numDefMaxDisI.Value;
                ResultConfig.CVVoltage = (double)numDefCvVolt.Value;
                ResultConfig.Save();
            };

            Button btnCancel = new Button { Text = "取消", Width = 80, Height = 28, Location = new Point(260, 8), DialogResult = DialogResult.Cancel };
            pnlBottom.Controls.Add(btnSave);
            pnlBottom.Controls.Add(btnCancel);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.RowCount = 5;
            grid.ColumnCount = 4;
            grid.Padding = new Padding(10, 10, 10, 2);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            for (int i = 0; i < 5; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
            }

            // 行 0
            grid.Controls.Add(new Label { Text = "默认电压 (V):", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
            numDefVoltage = new NumericUpDown { DecimalPlaces = 2, Minimum = 0, Maximum = 100, Increment = 0.1M, Value = (decimal)ResultConfig.Voltage, Width = 105 };
            grid.Controls.Add(numDefVoltage, 1, 0);

            grid.Controls.Add(new Label { Text = "默认电流 (A):", Anchor = AnchorStyles.Left, AutoSize = true }, 2, 0);
            numDefCurrent = new NumericUpDown { DecimalPlaces = 2, Minimum = -300, Maximum = 300, Increment = 1.0M, Value = (decimal)ResultConfig.Current, Width = 105 };
            grid.Controls.Add(numDefCurrent, 3, 0);

            // 行 1
            grid.Controls.Add(new Label { Text = "默认 SOC (%):", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
            numDefSoc = new NumericUpDown { DecimalPlaces = 1, Minimum = 0, Maximum = 100, Increment = 1M, Value = (decimal)ResultConfig.SOC, Width = 105 };
            grid.Controls.Add(numDefSoc, 1, 1);

            grid.Controls.Add(new Label { Text = "默认 SOH (%):", Anchor = AnchorStyles.Left, AutoSize = true }, 2, 1);
            numDefSoh = new NumericUpDown { DecimalPlaces = 0, Minimum = 0, Maximum = 100, Value = (decimal)ResultConfig.SOH, Width = 105 };
            grid.Controls.Add(numDefSoh, 3, 1);

            // 行 2
            grid.Controls.Add(new Label { Text = "默认温度 (°C):", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
            numDefTemp = new NumericUpDown { DecimalPlaces = 1, Minimum = -40, Maximum = 120, Increment = 1M, Value = (decimal)ResultConfig.Temperature, Width = 105 };
            grid.Controls.Add(numDefTemp, 1, 2);

            grid.Controls.Add(new Label { Text = "默认 CV点 (V):", Anchor = AnchorStyles.Left, AutoSize = true }, 2, 2);
            numDefCvVolt = new NumericUpDown { DecimalPlaces = 2, Minimum = 0, Maximum = 100, Increment = 0.1M, Value = (decimal)ResultConfig.CVVoltage, Width = 105 };
            grid.Controls.Add(numDefCvVolt, 3, 2);

            // 行 3
            grid.Controls.Add(new Label { Text = "默认剩余容量:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 3);
            numDefRemCap = new NumericUpDown { DecimalPlaces = 1, Minimum = 0, Maximum = 2000, Increment = 5M, Value = (decimal)ResultConfig.RemainingCapacity, Width = 105 };
            grid.Controls.Add(numDefRemCap, 1, 3);

            grid.Controls.Add(new Label { Text = "默认满充容量:", Anchor = AnchorStyles.Left, AutoSize = true }, 2, 3);
            numDefFullCap = new NumericUpDown { DecimalPlaces = 1, Minimum = 0, Maximum = 2000, Increment = 5M, Value = (decimal)ResultConfig.FullCapacity, Width = 105 };
            grid.Controls.Add(numDefFullCap, 3, 3);

            // 行 4
            grid.Controls.Add(new Label { Text = "默认充电限流:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 4);
            numDefMaxChgI = new NumericUpDown { DecimalPlaces = 1, Minimum = 0, Maximum = 500, Increment = 5M, Value = (decimal)ResultConfig.MaxChargeCurrent, Width = 105 };
            grid.Controls.Add(numDefMaxChgI, 1, 4);

            grid.Controls.Add(new Label { Text = "默认放电限流:", Anchor = AnchorStyles.Left, AutoSize = true }, 2, 4);
            numDefMaxDisI = new NumericUpDown { DecimalPlaces = 1, Minimum = 0, Maximum = 500, Increment = 5M, Value = (decimal)ResultConfig.MaxDischargeCurrent, Width = 105 };
            grid.Controls.Add(numDefMaxDisI, 3, 4);

            this.Controls.Add(grid);
            this.Controls.Add(pnlBottom);
        }
    }

    public class MainForm : Form
    {
        // ── Win32 API 锁定与防闪烁 ──
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern int GetScrollPos(IntPtr hWnd, int nBar);
        [DllImport("user32.dll")]
        private static extern int SetScrollPos(IntPtr hWnd, int nBar, int nPos, bool bRedraw);
        [DllImport("user32.dll")]
        private static extern bool PostMessageA(IntPtr hWnd, int nBar, int wParam, int lParam);
        private const int WM_SETREDRAW = 0x000B;
        private const int SB_VERT = 1;
        private const int WM_VSCROLL = 0x115;
        private const int SB_THUMBPOSITION = 4;

        private BmsDataModel _model;
        private SerialCommManager _comm;
        private SplitContainer splitMain;

        // ── 顶部配置栏控件 ──
        private ComboBox cboPort;
        private ComboBox cboBaud;
        private ComboBox cboProtocol;
        private ComboBox cboWorkMode;
        private CheckBox chkReplyAllIds;
        private NumericUpDown numSpecificId;
        private Button btnConnect;
        private Label lblPortStatus;

        // ── 模拟量控件 ──
        private NumericUpDown numVoltage, numCurrent, numTemp, numSoc, numSoh;
        private NumericUpDown numRemCap, numFullCap, numMaxChgI, numMaxDisI, numCvVolt;

        // ── 模拟量确认与回滚状态映射 ──
        private Dictionary<NumericUpDown, decimal> _committedValues = new Dictionary<NumericUpDown, decimal>();
        private Dictionary<NumericUpDown, bool> _isDirty = new Dictionary<NumericUpDown, bool>();
        private Dictionary<NumericUpDown, Timer> _flashTimers = new Dictionary<NumericUpDown, Timer>();
        private NumericUpDown _currentFocusedControl = null;

        // ── 标志位控件 ──
        private CheckBox chkChgEn, chkDisEn, chkBalance, chkSleep, chkForceChg;
        private CheckBox chkWarnSingleOv, chkWarnSingleUv, chkWarnGlobalOv, chkWarnGlobalUv;
        private CheckBox chkWarnOc, chkWarnHt, chkWarnLt, chkWarnVoltDiff, chkWarnLowCap;
        private CheckBox chkProtOv, chkProtUv, chkProtOc, chkProtSc, chkProtHt, chkProtLt, chkProtSys, chkProtSoftStart;

        // ── 监视器控件 ──
        private RichTextBox rtbLog;
        private Label lblRxCount, lblTxCount, lblErrCount, lblLatency;
        private CheckBox chkAutoScroll, chkShowTime, chkShowHex;
        private Button btnClearLog, btnExportLog;

        // ── 状态栏 ──
        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatusText, lblProtocolBadge;

        private bool _isUpdatingUi = false;

        public MainForm()
        {
            this.KeyPreview = true;
            this.KeyDown += MainForm_KeyDown;

            _model = new BmsDataModel();
            _comm = new SerialCommManager(_model);
            _comm.OnLogEvent += Comm_OnLogEvent;
            _comm.OnStatusChanged += Comm_OnStatusChanged;

            BuildUI();
            RefreshPorts();
            SyncModelToUI();
        }

        private void BuildUI()
        {
            this.Text = "BMS 多协议仿真与自动化测试工作台 V1.0 (CVTE / Growatt / Pylon / Voltronic)";
            this.Size = new Size(1200, 740);
            this.MinimumSize = new Size(1060, 640);
            this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = SystemColors.Control;

            // ══ 状态栏 ══
            statusStrip = new StatusStrip();
            statusStrip.Dock = DockStyle.Bottom;
            lblStatusText = new ToolStripStatusLabel("就绪");
            lblStatusText.BorderSides = ToolStripStatusLabelBorderSides.Right;
            lblProtocolBadge = new ToolStripStatusLabel("协议: CVTE (Modbus RTU)");
            statusStrip.Items.AddRange(new ToolStripItem[] { lblStatusText, lblProtocolBadge });

            // ══ 主分割布局 ══
            TableLayoutPanel mainLayout = new TableLayoutPanel();
            mainLayout.Dock = DockStyle.Fill;
            mainLayout.RowCount = 2;
            mainLayout.ColumnCount = 1;
            mainLayout.Padding = new Padding(6, 6, 6, 2);
            mainLayout.Margin = new Padding(0);
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65f));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            this.Controls.Add(mainLayout);
            this.Controls.Add(statusStrip);
            statusStrip.SendToBack();

            // ══════════════════════════════════════════════════════════
            // 1. 顶部串口与协议控制条
            // ══════════════════════════════════════════════════════════
            GroupBox gbTop = new GroupBox();
            gbTop.Text = "通信链路与协议设定";
            gbTop.Dock = DockStyle.Fill;
            gbTop.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);

            FlowLayoutPanel flpTop = new FlowLayoutPanel();
            flpTop.Dock = DockStyle.Fill;
            flpTop.AutoScroll = false;
            flpTop.WrapContents = false;
            flpTop.Padding = new Padding(4, 4, 4, 4);

            // 串口选择
            Label lblPort = new Label { Text = "端口:", AutoSize = true, Margin = new Padding(2, 7, 2, 0) };
            flpTop.Controls.Add(lblPort);

            cboPort = new ComboBox { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
            flpTop.Controls.Add(cboPort);

            Button btnRefresh = new Button { Text = "刷新", Width = 40, Height = 25, Margin = new Padding(2, 3, 6, 0) };
            btnRefresh.Click += delegate(object s, EventArgs e) { RefreshPorts(); };
            flpTop.Controls.Add(btnRefresh);

            // 波特率
            Label lblBaud = new Label { Text = "波特率:", AutoSize = true, Margin = new Padding(2, 7, 2, 0) };
            flpTop.Controls.Add(lblBaud);

            cboBaud = new ComboBox { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
            cboBaud.Items.AddRange(new object[] { "9600", "2400", "4800", "19200", "38400", "57600", "115200" });
            cboBaud.SelectedIndex = 0;
            flpTop.Controls.Add(cboBaud);

            // 协议类型
            Label lblProt = new Label { Text = "协议:", AutoSize = true, Margin = new Padding(4, 7, 2, 0) };
            flpTop.Controls.Add(lblProt);

            cboProtocol = new ComboBox { Width = 155, DropDownStyle = ComboBoxStyle.DropDownList };
            cboProtocol.Items.AddRange(new object[] {
                "CVTE (Modbus RTU)",
                "GROWATT (Modbus RTU)",
                "VOLTRONIC (Modbus RTU)",
                "PYLONTECH (RS485 ASCII)"
            });
            cboProtocol.SelectedIndex = 0;
            cboProtocol.SelectedIndexChanged += CboProtocol_SelectedIndexChanged;
            flpTop.Controls.Add(cboProtocol);

            // 工作模式
            Label lblMode = new Label { Text = "模式:", AutoSize = true, Margin = new Padding(4, 7, 2, 0) };
            flpTop.Controls.Add(lblMode);

            cboWorkMode = new ComboBox { Width = 125, DropDownStyle = ComboBoxStyle.DropDownList };
            cboWorkMode.Items.AddRange(new object[] { "从机模拟(测逆变器)", "主机轮询(测电池包)" });
            cboWorkMode.SelectedIndex = 0;
            cboWorkMode.SelectedIndexChanged += delegate(object s, EventArgs e) {
                _comm.CurrentWorkMode = cboWorkMode.SelectedIndex == 0 ? WorkMode.SlaveSimulator : WorkMode.MasterPoller;
            };
            flpTop.Controls.Add(cboWorkMode);

            // 轮询应答 ID
            chkReplyAllIds = new CheckBox { Text = "全ID应答", AutoSize = true, Checked = true, Margin = new Padding(4, 7, 2, 0) };
            chkReplyAllIds.CheckedChanged += delegate(object s, EventArgs e) {
                _comm.ReplyAllIds = chkReplyAllIds.Checked;
                numSpecificId.Enabled = !chkReplyAllIds.Checked;
            };
            flpTop.Controls.Add(chkReplyAllIds);

            numSpecificId = new NumericUpDown { Minimum = 0, Maximum = 15, Value = 1, Width = 38, Enabled = false, Margin = new Padding(0, 4, 6, 0) };
            numSpecificId.ValueChanged += delegate(object s, EventArgs e) { _comm.SpecificId = (int)numSpecificId.Value; };
            flpTop.Controls.Add(numSpecificId);

            // 开关连接按钮
            btnConnect = new Button { Text = "打开串口", Width = 80, Height = 26, Margin = new Padding(4, 3, 6, 0) };
            btnConnect.Click += BtnConnect_Click;
            flpTop.Controls.Add(btnConnect);

            lblPortStatus = new Label
            {
                Text = "未连接",
                AutoSize = false,
                Width = 75,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Gray,
                Margin = new Padding(2, 6, 4, 0)
            };
            flpTop.Controls.Add(lblPortStatus);

            gbTop.Controls.Add(flpTop);
            mainLayout.Controls.Add(gbTop, 0, 0);

            // ══════════════════════════════════════════════════════════
            // 2. 核心工作区
            // ══════════════════════════════════════════════════════════
            splitMain = new SplitContainer();
            splitMain.Dock = DockStyle.Fill;
            splitMain.Orientation = Orientation.Vertical;
            splitMain.SplitterWidth = 8;
            splitMain.BackColor = SystemColors.Control;
            mainLayout.Controls.Add(splitMain, 0, 1);

            // ── 左半区：TabControl 参数与位设定 ──
            TabControl tabParams = new TabControl();
            tabParams.Dock = DockStyle.Fill;
            splitMain.Panel1.Controls.Add(tabParams);

            TabPage tabAnalog = new TabPage("电池模拟量设定");
            tabAnalog.Padding = new Padding(8);
            tabAnalog.BackColor = SystemColors.Window;
            tabAnalog.AutoScroll = true;

            TabPage tabFlags = new TabPage("状态位与告警/保护");
            tabFlags.Padding = new Padding(8);
            tabFlags.BackColor = SystemColors.Window;
            tabFlags.AutoScroll = true;

            tabParams.TabPages.AddRange(new TabPage[] { tabAnalog, tabFlags });

            BuildAnalogTab(tabAnalog);
            BuildFlagsTab(tabFlags);

            // ── 右半区：实时报文监视器 ──
            BuildMonitorPanel(splitMain.Panel2);
        }

        // ══════════════════════════════════════════════════════════
        // 3. 模拟量参数设定面板构建
        // ══════════════════════════════════════════════════════════
        private void BuildAnalogTab(TabPage parent)
        {
            MouseEventHandler onBlankMouseDown = delegate(object s, MouseEventArgs e) {
                CancelAndRollbackCurrentEdit();
            };

            TableLayoutPanel pnlTab = new TableLayoutPanel();
            pnlTab.Dock = DockStyle.Fill;
            pnlTab.RowCount = 3;
            pnlTab.ColumnCount = 1;
            pnlTab.RowStyles.Add(new RowStyle(SizeType.Absolute, 190f));
            pnlTab.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
            pnlTab.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            pnlTab.MouseDown += onBlankMouseDown;
            parent.MouseDown += onBlankMouseDown;

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.RowCount = 5;
            grid.ColumnCount = 4;
            grid.Padding = new Padding(4, 4, 4, 4);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.MouseDown += onBlankMouseDown;

            for (int i = 0; i < 5; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
            }

            Func<string, Label> createLbl = delegate(string txt) {
                Label l = new Label { Text = txt, Anchor = AnchorStyles.Left, AutoSize = true };
                l.MouseDown += onBlankMouseDown;
                return l;
            };

            // 行 0: 电压 (V) / 电流 (A)
            grid.Controls.Add(createLbl("电池总电压 (V):"), 0, 0);
            numVoltage = CreateBoundNumeric(2, 0, 100, 0.1M, 28.00M);
            grid.Controls.Add(numVoltage, 1, 0);

            grid.Controls.Add(createLbl("充放电流 (A):"), 2, 0);
            numCurrent = CreateBoundNumeric(2, -300, 300, 1.0M, 0.00M);
            grid.Controls.Add(numCurrent, 3, 0);

            // 行 1: SOC (%) / SOH (%)
            grid.Controls.Add(createLbl("电池 SOC (%):"), 0, 1);
            numSoc = CreateBoundNumeric(1, 0, 100, 1M, 100M);
            grid.Controls.Add(numSoc, 1, 1);

            grid.Controls.Add(createLbl("健康度 SOH (%):"), 2, 1);
            numSoh = CreateBoundNumeric(0, 0, 100, 1M, 100M);
            grid.Controls.Add(numSoh, 3, 1);

            // 行 2: 温度 (°C) / CV 恒压点 (V)
            grid.Controls.Add(createLbl("最高温度 (°C):"), 0, 2);
            numTemp = CreateBoundNumeric(1, -40, 120, 1M, 28M);
            grid.Controls.Add(numTemp, 1, 2);

            grid.Controls.Add(createLbl("CV 恒压点 (V):"), 2, 2);
            numCvVolt = CreateBoundNumeric(2, 0, 100, 0.1M, 28.80M);
            grid.Controls.Add(numCvVolt, 3, 2);

            // 行 3: 剩余容量 (Ah) / 满充容量 (Ah)
            grid.Controls.Add(createLbl("剩余容量 (Ah):"), 0, 3);
            numRemCap = CreateBoundNumeric(1, 0, 2000, 5M, 100M);
            grid.Controls.Add(numRemCap, 1, 3);

            grid.Controls.Add(createLbl("满充容量 (Ah):"), 2, 3);
            numFullCap = CreateBoundNumeric(1, 0, 2000, 5M, 100M);
            grid.Controls.Add(numFullCap, 3, 3);

            // 行 4: 充电限流 (A) / 放电限流 (A)
            grid.Controls.Add(createLbl("充电限流 (A):"), 0, 4);
            numMaxChgI = CreateBoundNumeric(1, 0, 500, 5M, 100M);
            grid.Controls.Add(numMaxChgI, 1, 4);

            grid.Controls.Add(createLbl("放电限流 (A):"), 2, 4);
            numMaxDisI = CreateBoundNumeric(1, 0, 500, 5M, 100M);
            grid.Controls.Add(numMaxDisI, 3, 4);

            pnlTab.Controls.Add(grid, 0, 0);

            // ── 行 1: 恢复默认 | ⚙ 连体按钮栏 ──
            FlowLayoutPanel flpActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(4, 4, 4, 4),
                WrapContents = false
            };
            flpActions.MouseDown += onBlankMouseDown;

            // 连体组合按钮面板
            Panel pnlCombo = new Panel
            {
                Height = 26,
                Width = 100,
                Margin = new Padding(2, 2, 8, 2)
            };

            Button btnResetDefault = new Button
            {
                Text = "恢复默认",
                Width = 70,
                Height = 26,
                Location = new Point(0, 0),
                Margin = new Padding(0)
            };
            btnResetDefault.Click += delegate(object s, EventArgs e) {
                BmsDefaultConfig def = BmsDefaultConfig.Load();
                _model.ApplyDefaultConfig(def);
                SyncModelToUI();
                CommitAllControls();
            };

            Button btnGear = new Button
            {
                Text = "⚙",
                Width = 28,
                Height = 26,
                Location = new Point(71, 0),
                Margin = new Padding(0),
                Font = new Font("Segoe UI Symbol", 9F, FontStyle.Regular)
            };

            ToolTip tip = new ToolTip();
            tip.SetToolTip(btnGear, "修改并保存默认值参数");

            btnGear.Click += delegate(object s, EventArgs e) {
                using (DefaultConfigDialog dlg = new DefaultConfigDialog(BmsDefaultConfig.Load()))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        MessageBox.Show("默认值已成功保存！下次打开软件将自动生效。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            };

            pnlCombo.Controls.Add(btnResetDefault);
            pnlCombo.Controls.Add(btnGear);

            Label lblHint = new Label
            {
                Text = "💡 提示: 修改数值后按 [Enter] 或 [Ctrl+S] 提交生效 (绿框正常/黄框极限/红框非法)。",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                Margin = new Padding(4, 5, 2, 2)
            };
            lblHint.MouseDown += onBlankMouseDown;

            flpActions.Controls.Add(pnlCombo);
            flpActions.Controls.Add(lblHint);

            pnlTab.Controls.Add(flpActions, 0, 1);

            // ── 行 2: 底部空白占位区域 ──
            Panel pnlEmpty = new Panel { Dock = DockStyle.Fill };
            pnlEmpty.MouseDown += onBlankMouseDown;
            pnlTab.Controls.Add(pnlEmpty, 0, 2);

            parent.Controls.Add(pnlTab);
        }

        // ══════════════════════════════════════════════════════════
        // 核心 NumericUpDown 交互行为与三级参数校验
        // ══════════════════════════════════════════════════════════
        private NumericUpDown CreateBoundNumeric(int decimals, decimal min, decimal max, decimal increment, decimal initVal)
        {
            NumericUpDown num = new NumericUpDown
            {
                DecimalPlaces = decimals,
                Minimum = min,
                Maximum = max,
                Increment = increment,
                Value = initVal,
                Width = 110,
                Anchor = AnchorStyles.Left
            };

            _committedValues[num] = initVal;
            _isDirty[num] = false;

            TextBox innerTb = (num.Controls.Count > 1 && num.Controls[1] is TextBox) ? (TextBox)num.Controls[1] : null;

            num.Enter += delegate(object s, EventArgs e) {
                _currentFocusedControl = num;
            };

            if (innerTb != null)
            {
                innerTb.Enter += delegate(object s, EventArgs e) {
                    _currentFocusedControl = num;
                };

                innerTb.TextChanged += delegate(object s, EventArgs e) {
                    if (!_isUpdatingUi)
                    {
                        _isDirty[num] = true;
                    }
                };

                innerTb.KeyDown += delegate(object s, KeyEventArgs e) {
                    HandleNumericKeyDown(num, e);
                };

                innerTb.Leave += delegate(object s, EventArgs e) {
                    if (_isDirty.ContainsKey(num) && _isDirty[num])
                    {
                        RollbackNumeric(num);
                    }
                    if (_currentFocusedControl == num)
                    {
                        _currentFocusedControl = null;
                    }
                };
            }

            num.ValueChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi)
                {
                    _isDirty[num] = true;
                }
            };

            num.KeyDown += delegate(object s, KeyEventArgs e) {
                HandleNumericKeyDown(num, e);
            };

            num.Leave += delegate(object s, EventArgs e) {
                if (_isDirty.ContainsKey(num) && _isDirty[num])
                {
                    RollbackNumeric(num);
                }
                if (_currentFocusedControl == num)
                {
                    _currentFocusedControl = null;
                }
            };

            return num;
        }

        private void RollbackNumeric(NumericUpDown num)
        {
            if (num == null) return;
            _isUpdatingUi = true;
            decimal committed = _committedValues.ContainsKey(num) ? _committedValues[num] : num.Value;
            num.Value = committed;
            if (num.Controls.Count > 1 && num.Controls[1] is TextBox)
            {
                TextBox tb = (TextBox)num.Controls[1];
                tb.Text = (num.DecimalPlaces > 0) ? committed.ToString("F" + num.DecimalPlaces) : committed.ToString("F0");
                tb.SelectionLength = 0;
            }
            _isDirty[num] = false;
            _isUpdatingUi = false;
        }

        private void CancelAndRollbackCurrentEdit()
        {
            if (_currentFocusedControl != null)
            {
                RollbackNumeric(_currentFocusedControl);
                _currentFocusedControl = null;
            }
            statusStrip.Focus();
        }

        private void HandleNumericKeyDown(NumericUpDown num, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || (e.Control && e.KeyCode == Keys.S))
            {
                CommitSingleControl(num);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                RollbackNumeric(num);
                CancelAndRollbackCurrentEdit();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // ── 参数合理性校验算法 ──
        private ValidationLevel ValidateNumeric(NumericUpDown num, decimal value, out string message)
        {
            message = string.Empty;

            // 1. 剩余容量与满充容量跨参数逻辑校验
            if (num == numRemCap)
            {
                decimal currentFull = _committedValues.ContainsKey(numFullCap) ? _committedValues[numFullCap] : numFullCap.Value;
                if (value > currentFull)
                {
                    message = string.Format("非法输入: 剩余容量 ({0}Ah) 不能大于满充总容量 ({1}Ah)！", value, currentFull);
                    return ValidationLevel.Invalid;
                }
                if (value <= 0)
                {
                    message = "极限工况: 剩余容量为 0Ah (电池完全放空)";
                    return ValidationLevel.Extreme;
                }
            }
            else if (num == numFullCap)
            {
                if (value <= 0)
                {
                    message = "非法输入: 满充总容量必须大于 0Ah！";
                    return ValidationLevel.Invalid;
                }
                decimal currentRem = _committedValues.ContainsKey(numRemCap) ? _committedValues[numRemCap] : numRemCap.Value;
                if (value < currentRem)
                {
                    message = string.Format("非法输入: 满充总容量 ({0}Ah) 不能小于当前剩余容量 ({1}Ah)！", value, currentRem);
                    return ValidationLevel.Invalid;
                }
            }
            // 2. SOC / SOH 校验
            else if (num == numSoc)
            {
                if (value < 0 || value > 100)
                {
                    message = "非法输入: SOC 百分比必须在 0% ~ 100% 之间！";
                    return ValidationLevel.Invalid;
                }
                if (value <= 5 || value >= 99)
                {
                    message = string.Format("极限工况: 电池处于极限 SOC 状态 ({0}%)", value);
                    return ValidationLevel.Extreme;
                }
            }
            else if (num == numSoh)
            {
                if (value < 0 || value > 100)
                {
                    message = "非法输入: SOH 健康度必须在 0% ~ 100% 之间！";
                    return ValidationLevel.Invalid;
                }
                if (value < 60)
                {
                    message = string.Format("极限工况: 电池健康度严重衰减 SOH={0}%", value);
                    return ValidationLevel.Extreme;
                }
            }
            // 3. 电压与 CV 点校验
            else if (num == numVoltage)
            {
                if (value < 10 || value > 90)
                {
                    message = string.Format("极限工况: 电池总电压 {0:F2}V (属于异常极限欠压/过压测试点)", value);
                    return ValidationLevel.Extreme;
                }
            }
            else if (num == numCvVolt)
            {
                if (value <= 10)
                {
                    message = "非法输入: CV 恒压充电点必须大于 10V！";
                    return ValidationLevel.Invalid;
                }
                decimal currentV = _committedValues.ContainsKey(numVoltage) ? _committedValues[numVoltage] : numVoltage.Value;
                if (value < currentV)
                {
                    message = string.Format("极限工况: CV 恒压点 ({0:F2}V) 低于当前电池电压 ({1:F2}V)", value, currentV);
                    return ValidationLevel.Extreme;
                }
            }
            // 4. 电流与温度极限校验
            else if (num == numCurrent)
            {
                if (Math.Abs(value) >= 200)
                {
                    message = string.Format("极限工况: 充放电电流 {0:F2}A (大电流重载测试)", value);
                    return ValidationLevel.Extreme;
                }
            }
            else if (num == numTemp)
            {
                if (value <= -15 || value >= 65)
                {
                    message = string.Format("极限工况: 电池温度 {0:F1}°C (处于极端高低温区)", value);
                    return ValidationLevel.Extreme;
                }
            }
            else if (num == numMaxChgI || num == numMaxDisI)
            {
                if (value == 0)
                {
                    message = "极限工况: 限流设定为 0A (禁充/禁放动作)";
                    return ValidationLevel.Extreme;
                }
            }

            return ValidationLevel.Normal;
        }

        private void CommitSingleControl(NumericUpDown num)
        {
            if (num.Controls.Count > 1 && num.Controls[1] is TextBox)
            {
                TextBox tb = (TextBox)num.Controls[1];
                decimal parsed;
                if (decimal.TryParse(tb.Text, out parsed))
                {
                    if (parsed >= num.Minimum && parsed <= num.Maximum)
                    {
                        _isUpdatingUi = true;
                        num.Value = parsed;
                        _isUpdatingUi = false;
                    }
                }
            }

            string validationMsg;
            ValidationLevel level = ValidateNumeric(num, num.Value, out validationMsg);

            if (level == ValidationLevel.Invalid)
            {
                // 非法输入: 红色闪烁 2 秒，拒绝生效并自动回滚
                FlashControlColor(num, Color.FromArgb(254, 202, 202)); // 浅粉红
                RollbackNumeric(num);
                lblStatusText.Text = "⛔ " + validationMsg;
                return;
            }

            _committedValues[num] = num.Value;
            _isDirty[num] = false;

            ApplyCommittedValueToModel(num);

            if (level == ValidationLevel.Extreme)
            {
                // 极限工况: 黄色闪烁 2 秒，允许生效
                FlashControlColor(num, Color.FromArgb(254, 240, 138)); // 柔和琥珀黄
                lblStatusText.Text = "⚠️ " + validationMsg;
            }
            else
            {
                // 正常参数: 绿色闪烁 2 秒
                FlashControlColor(num, Color.FromArgb(198, 246, 213)); // 清新淡绿
                lblStatusText.Text = "✓ 数值已提交生效";
            }
        }

        private void CommitAllControls()
        {
            NumericUpDown[] controls = new NumericUpDown[] {
                numVoltage, numCurrent, numTemp, numSoc, numSoh,
                numRemCap, numFullCap, numMaxChgI, numMaxDisI, numCvVolt
            };

            foreach (NumericUpDown num in controls)
            {
                if (num != null)
                {
                    _committedValues[num] = num.Value;
                    _isDirty[num] = false;
                    ApplyCommittedValueToModel(num);
                    FlashControlColor(num, Color.FromArgb(198, 246, 213));
                }
            }
            lblStatusText.Text = "✓ 默认参数已全部提交生效";
        }

        private void ApplyCommittedValueToModel(NumericUpDown num)
        {
            lock (_model.SyncRoot)
            {
                if (num == numVoltage) _model.Voltage = (double)num.Value;
                else if (num == numCurrent) _model.Current = (double)num.Value;
                else if (num == numTemp) _model.Temperature = (double)num.Value;
                else if (num == numSoc) _model.SOC = (double)num.Value;
                else if (num == numSoh) _model.SOH = (double)num.Value;
                else if (num == numRemCap) _model.RemainingCapacity = (double)num.Value;
                else if (num == numFullCap) _model.FullCapacity = (double)num.Value;
                else if (num == numMaxChgI) _model.MaxChargeCurrent = (double)num.Value;
                else if (num == numMaxDisI) _model.MaxDischargeCurrent = (double)num.Value;
                else if (num == numCvVolt) _model.CVVoltage = (double)num.Value;
            }
        }

        private void FlashControlColor(NumericUpDown num, Color color)
        {
            num.BackColor = color;
            if (num.Controls.Count > 1 && num.Controls[1] is TextBox)
            {
                num.Controls[1].BackColor = color;
            }

            if (!_flashTimers.ContainsKey(num))
            {
                Timer tmr = new Timer { Interval = 2000 };
                tmr.Tick += delegate(object s, EventArgs e) {
                    tmr.Stop();
                    num.BackColor = SystemColors.Window;
                    if (num.Controls.Count > 1 && num.Controls[1] is TextBox)
                    {
                        num.Controls[1].BackColor = SystemColors.Window;
                    }
                };
                _flashTimers[num] = tmr;
            }

            _flashTimers[num].Stop();
            _flashTimers[num].Start();
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                if (_currentFocusedControl != null)
                {
                    CommitSingleControl(_currentFocusedControl);
                }
                else
                {
                    CommitAllControls();
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 4. 状态位与告警/保护矩阵面板构建
        // ══════════════════════════════════════════════════════════
        private void BuildFlagsTab(TabPage parent)
        {
            TableLayoutPanel mainFlags = new TableLayoutPanel();
            mainFlags.Dock = DockStyle.Fill;
            mainFlags.RowCount = 3;
            mainFlags.ColumnCount = 1;
            mainFlags.AutoScroll = true;
            mainFlags.RowStyles.Add(new RowStyle(SizeType.Absolute, 70f));
            mainFlags.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            mainFlags.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            // ── 1. 状态位 ──
            GroupBox gbStatus = new GroupBox();
            gbStatus.Text = "系统状态标志位";
            gbStatus.Dock = DockStyle.Fill;

            FlowLayoutPanel flpStatus = new FlowLayoutPanel();
            flpStatus.Dock = DockStyle.Fill;
            flpStatus.AutoScroll = true;

            chkChgEn = new CheckBox(); chkChgEn.Text = "充电使能"; chkChgEn.Checked = true; chkChgEn.AutoSize = true; chkChgEn.Margin = new Padding(6);
            chkChgEn.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.StatusChargeEnable = chkChgEn.Checked; };

            chkDisEn = new CheckBox(); chkDisEn.Text = "放电使能"; chkDisEn.Checked = true; chkDisEn.AutoSize = true; chkDisEn.Margin = new Padding(6);
            chkDisEn.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.StatusDischargeEnable = chkDisEn.Checked; };

            chkBalance = new CheckBox(); chkBalance.Text = "电芯均衡中"; chkBalance.AutoSize = true; chkBalance.Margin = new Padding(6);
            chkBalance.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.StatusBalancing = chkBalance.Checked; };

            chkSleep = new CheckBox(); chkSleep.Text = "休眠状态"; chkSleep.AutoSize = true; chkSleep.Margin = new Padding(6);
            chkSleep.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.StatusSleep = chkSleep.Checked; };

            chkForceChg = new CheckBox(); chkForceChg.Text = "请求强制充电"; chkForceChg.AutoSize = true; chkForceChg.Margin = new Padding(6);
            chkForceChg.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.StatusForceCharge = chkForceChg.Checked; };

            flpStatus.Controls.AddRange(new Control[] { chkChgEn, chkDisEn, chkBalance, chkSleep, chkForceChg });
            gbStatus.Controls.Add(flpStatus);
            mainFlags.Controls.Add(gbStatus, 0, 0);

            // ── 2. 警告位 ──
            GroupBox gbWarn = new GroupBox();
            gbWarn.Text = "警告状态矩阵";
            gbWarn.Dock = DockStyle.Fill;

            FlowLayoutPanel flpWarn = new FlowLayoutPanel();
            flpWarn.Dock = DockStyle.Fill;
            flpWarn.AutoScroll = true;

            chkWarnSingleOv = new CheckBox(); chkWarnSingleOv.Text = "单体过压警告"; chkWarnSingleOv.AutoSize = true; chkWarnSingleOv.Margin = new Padding(6);
            chkWarnSingleOv.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.WarnSingleOverVolt = chkWarnSingleOv.Checked; };

            chkWarnSingleUv = new CheckBox(); chkWarnSingleUv.Text = "单体欠压警告"; chkWarnSingleUv.AutoSize = true; chkWarnSingleUv.Margin = new Padding(6);
            chkWarnSingleUv.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.WarnSingleUnderVolt = chkWarnSingleUv.Checked; };

            chkWarnGlobalOv = new CheckBox(); chkWarnGlobalOv.Text = "组端过压警告"; chkWarnGlobalOv.AutoSize = true; chkWarnGlobalOv.Margin = new Padding(6);
            chkWarnGlobalOv.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.WarnGlobalOverVolt = chkWarnGlobalOv.Checked; };

            chkWarnGlobalUv = new CheckBox(); chkWarnGlobalUv.Text = "组端欠压警告"; chkWarnGlobalUv.AutoSize = true; chkWarnGlobalUv.Margin = new Padding(6);
            chkWarnGlobalUv.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.WarnGlobalUnderVolt = chkWarnGlobalUv.Checked; };

            chkWarnOc = new CheckBox(); chkWarnOc.Text = "充放电过流警告"; chkWarnOc.AutoSize = true; chkWarnOc.Margin = new Padding(6);
            chkWarnOc.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.WarnOverCurrent = chkWarnOc.Checked; };

            chkWarnHt = new CheckBox(); chkWarnHt.Text = "高温警告"; chkWarnHt.AutoSize = true; chkWarnHt.Margin = new Padding(6);
            chkWarnHt.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.WarnHighTemp = chkWarnHt.Checked; };

            chkWarnLt = new CheckBox(); chkWarnLt.Text = "低温警告"; chkWarnLt.AutoSize = true; chkWarnLt.Margin = new Padding(6);
            chkWarnLt.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.WarnLowTemp = chkWarnLt.Checked; };

            chkWarnVoltDiff = new CheckBox(); chkWarnVoltDiff.Text = "电芯压差过大"; chkWarnVoltDiff.AutoSize = true; chkWarnVoltDiff.Margin = new Padding(6);
            chkWarnVoltDiff.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.WarnVoltDiff = chkWarnVoltDiff.Checked; };

            chkWarnLowCap = new CheckBox(); chkWarnLowCap.Text = "电池低电量告警"; chkWarnLowCap.AutoSize = true; chkWarnLowCap.Margin = new Padding(6);
            chkWarnLowCap.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.WarnLowCapacity = chkWarnLowCap.Checked; };

            flpWarn.Controls.AddRange(new Control[] {
                chkWarnSingleOv, chkWarnSingleUv, chkWarnGlobalOv, chkWarnGlobalUv,
                chkWarnOc, chkWarnHt, chkWarnLt, chkWarnVoltDiff, chkWarnLowCap
            });
            gbWarn.Controls.Add(flpWarn);
            mainFlags.Controls.Add(gbWarn, 0, 1);

            // ── 3. 故障保护位 ──
            GroupBox gbProt = new GroupBox();
            gbProt.Text = "故障与保护跳闸矩阵";
            gbProt.Dock = DockStyle.Fill;

            FlowLayoutPanel flpProt = new FlowLayoutPanel();
            flpProt.Dock = DockStyle.Fill;
            flpProt.AutoScroll = true;

            chkProtOv = new CheckBox(); chkProtOv.Text = "总压/单体过压保护"; chkProtOv.AutoSize = true; chkProtOv.Margin = new Padding(6);
            chkProtOv.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.ProtOverVolt = chkProtOv.Checked; };

            chkProtUv = new CheckBox(); chkProtUv.Text = "总压/单体欠压保护"; chkProtUv.AutoSize = true; chkProtUv.Margin = new Padding(6);
            chkProtUv.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.ProtUnderVolt = chkProtUv.Checked; };

            chkProtOc = new CheckBox(); chkProtOc.Text = "过流保护"; chkProtOc.AutoSize = true; chkProtOc.Margin = new Padding(6);
            chkProtOc.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.ProtOverCurrent = chkProtOc.Checked; };

            chkProtSc = new CheckBox(); chkProtSc.Text = "输出短路保护"; chkProtSc.AutoSize = true; chkProtSc.Margin = new Padding(6);
            chkProtSc.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.ProtShortCircuit = chkProtSc.Checked; };

            chkProtHt = new CheckBox(); chkProtHt.Text = "电芯过温保护"; chkProtHt.AutoSize = true; chkProtHt.Margin = new Padding(6);
            chkProtHt.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.ProtHighTemp = chkProtHt.Checked; };

            chkProtLt = new CheckBox(); chkProtLt.Text = "电芯低温保护"; chkProtLt.AutoSize = true; chkProtLt.Margin = new Padding(6);
            chkProtLt.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.ProtUnderTemp = chkProtLt.Checked; };

            chkProtSys = new CheckBox(); chkProtSys.Text = "BMS系统故障"; chkProtSys.AutoSize = true; chkProtSys.Margin = new Padding(6);
            chkProtSys.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.ProtSystemFault = chkProtSys.Checked; };

            chkProtSoftStart = new CheckBox(); chkProtSoftStart.Text = "软起动失败"; chkProtSoftStart.AutoSize = true; chkProtSoftStart.Margin = new Padding(6);
            chkProtSoftStart.CheckedChanged += delegate(object s, EventArgs e) { if (!_isUpdatingUi) _model.ProtSoftStart = chkProtSoftStart.Checked; };

            flpProt.Controls.AddRange(new Control[] {
                chkProtOv, chkProtUv, chkProtOc, chkProtSc,
                chkProtHt, chkProtLt, chkProtSys, chkProtSoftStart
            });
            gbProt.Controls.Add(flpProt);
            mainFlags.Controls.Add(gbProt, 0, 2);

            parent.Controls.Add(mainFlags);
        }

        // ══════════════════════════════════════════════════════════
        // 5. 报文监视器与日志面板构建 (实时监视 & 告警/保护摘要颜色高亮)
        // ══════════════════════════════════════════════════════════
        private void BuildMonitorPanel(Panel parent)
        {
            GroupBox gbMonitor = new GroupBox();
            gbMonitor.Text = "实时监视";
            gbMonitor.Dock = DockStyle.Fill;
            gbMonitor.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);

            TableLayoutPanel pnlMon = new TableLayoutPanel();
            pnlMon.Dock = DockStyle.Fill;
            pnlMon.RowCount = 3;
            pnlMon.ColumnCount = 1;
            pnlMon.Padding = new Padding(4, 4, 4, 4);
            pnlMon.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            pnlMon.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            pnlMon.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));

            // 统计栏
            FlowLayoutPanel flpStats = new FlowLayoutPanel();
            flpStats.Dock = DockStyle.Fill;
            flpStats.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);

            lblRxCount = new Label(); lblRxCount.Text = "RX: 0"; lblRxCount.AutoSize = true; lblRxCount.Margin = new Padding(2, 4, 8, 0);
            lblTxCount = new Label(); lblTxCount.Text = "TX: 0"; lblTxCount.AutoSize = true; lblTxCount.Margin = new Padding(2, 4, 8, 0);
            lblErrCount = new Label(); lblErrCount.Text = "错误: 0"; lblErrCount.AutoSize = true; lblErrCount.Margin = new Padding(2, 4, 8, 0);
            lblLatency = new Label(); lblLatency.Text = "响应时延: - ms"; lblLatency.AutoSize = true; lblLatency.Margin = new Padding(2, 4, 8, 0);
            flpStats.Controls.AddRange(new Control[] { lblRxCount, lblTxCount, lblErrCount, lblLatency });
            pnlMon.Controls.Add(flpStats, 0, 0);

            // 日志文本框
            rtbLog = new RichTextBox();
            rtbLog.Dock = DockStyle.Fill;
            rtbLog.BackColor = Color.FromArgb(20, 24, 30);
            rtbLog.ForeColor = Color.FromArgb(220, 225, 230);
            rtbLog.Font = new Font("Consolas", 9F, FontStyle.Regular);
            rtbLog.ReadOnly = true;
            rtbLog.WordWrap = false;
            rtbLog.HideSelection = false;
            pnlMon.Controls.Add(rtbLog, 0, 1);

            // 底部操作栏
            FlowLayoutPanel flpLogTools = new FlowLayoutPanel();
            flpLogTools.Dock = DockStyle.Fill;
            flpLogTools.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            flpLogTools.Padding = new Padding(2, 4, 2, 2);

            chkAutoScroll = new CheckBox(); chkAutoScroll.Text = "自动滚屏"; chkAutoScroll.Checked = true; chkAutoScroll.AutoSize = true; chkAutoScroll.Margin = new Padding(2, 6, 8, 0);
            chkShowTime = new CheckBox(); chkShowTime.Text = "显示时间"; chkShowTime.Checked = true; chkShowTime.AutoSize = true; chkShowTime.Margin = new Padding(2, 6, 8, 0);
            chkShowHex = new CheckBox(); chkShowHex.Text = "显示HEX"; chkShowHex.Checked = true; chkShowHex.AutoSize = true; chkShowHex.Margin = new Padding(2, 6, 8, 0);

            btnClearLog = new Button(); btnClearLog.Text = "清空"; btnClearLog.Width = 55; btnClearLog.Height = 26; btnClearLog.Margin = new Padding(2, 2, 6, 0);
            btnClearLog.Click += delegate(object s, EventArgs e) {
                rtbLog.Clear();
                _comm.ResetStatistics();
                UpdateStatsLabels();
            };

            btnExportLog = new Button(); btnExportLog.Text = "导出日志"; btnExportLog.Width = 75; btnExportLog.Height = 26; btnExportLog.Margin = new Padding(2, 2, 6, 0);
            btnExportLog.Click += BtnExportLog_Click;

            flpLogTools.Controls.AddRange(new Control[] { chkAutoScroll, chkShowTime, chkShowHex, btnClearLog, btnExportLog });
            pnlMon.Controls.Add(flpLogTools, 0, 2);

            gbMonitor.Controls.Add(pnlMon);
            parent.Controls.Add(gbMonitor);
        }

        // ══════════════════════════════════════════════════════════
        // 6. UI 事件处理与数据同步
        // ══════════════════════════════════════════════════════════
        private void RefreshPorts()
        {
            cboPort.Items.Clear();
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length > 0)
            {
                cboPort.Items.AddRange(ports);
                cboPort.SelectedIndex = 0;
            }
            else
            {
                cboPort.Items.Add("无可用串口");
                cboPort.SelectedIndex = 0;
            }
        }

        private void CboProtocol_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (cboProtocol.SelectedIndex)
            {
                case 0:
                    _comm.ProtocolHandler = new CvteModbusHandler();
                    lblProtocolBadge.Text = "协议: CVTE (Modbus RTU)";
                    break;
                case 1:
                    _comm.ProtocolHandler = new GrowattModbusHandler();
                    lblProtocolBadge.Text = "协议: GROWATT (Modbus RTU)";
                    break;
                case 2:
                    _comm.ProtocolHandler = new VoltronicModbusHandler();
                    lblProtocolBadge.Text = "协议: VOLTRONIC (Modbus RTU)";
                    break;
                case 3:
                    _comm.ProtocolHandler = new PylontechAsciiHandler();
                    lblProtocolBadge.Text = "协议: PYLONTECH (RS485 ASCII)";
                    break;
            }
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            if (_comm.IsOpen)
            {
                _comm.Close();
                btnConnect.Text = "打开串口";
                lblPortStatus.Text = "未连接";
                lblPortStatus.ForeColor = Color.Gray;
                cboPort.Enabled = true;
                cboBaud.Enabled = true;
            }
            else
            {
                if (cboPort.SelectedItem == null || cboPort.SelectedItem.ToString().Contains("无"))
                {
                    MessageBox.Show("请先插入 USB-485 转换器并选择有效串口！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string portName = cboPort.SelectedItem.ToString();
                int baud = int.Parse(cboBaud.SelectedItem.ToString());

                if (_comm.Open(portName, baud))
                {
                    btnConnect.Text = "关闭串口";
                    lblPortStatus.Text = "● 运行中";
                    lblPortStatus.ForeColor = Color.Green;
                    cboPort.Enabled = false;
                    cboBaud.Enabled = false;
                }
            }
        }

        private void SyncModelToUI()
        {
            _isUpdatingUi = true;
            lock (_model.SyncRoot)
            {
                UpdateNumericCommitted(numVoltage, (decimal)_model.Voltage);
                UpdateNumericCommitted(numCurrent, (decimal)_model.Current);
                UpdateNumericCommitted(numTemp, (decimal)_model.Temperature);
                UpdateNumericCommitted(numSoc, (decimal)_model.SOC);
                UpdateNumericCommitted(numSoh, (decimal)_model.SOH);
                UpdateNumericCommitted(numRemCap, (decimal)_model.RemainingCapacity);
                UpdateNumericCommitted(numFullCap, (decimal)_model.FullCapacity);
                UpdateNumericCommitted(numMaxChgI, (decimal)_model.MaxChargeCurrent);
                UpdateNumericCommitted(numMaxDisI, (decimal)_model.MaxDischargeCurrent);
                UpdateNumericCommitted(numCvVolt, (decimal)_model.CVVoltage);

                chkChgEn.Checked = _model.StatusChargeEnable;
                chkDisEn.Checked = _model.StatusDischargeEnable;
                chkBalance.Checked = _model.StatusBalancing;
                chkSleep.Checked = _model.StatusSleep;
                chkForceChg.Checked = _model.StatusForceCharge;

                chkWarnSingleOv.Checked = _model.WarnSingleOverVolt;
                chkWarnSingleUv.Checked = _model.WarnSingleUnderVolt;
                chkWarnGlobalOv.Checked = _model.WarnGlobalOverVolt;
                chkWarnGlobalUv.Checked = _model.WarnGlobalUnderVolt;
                chkWarnOc.Checked = _model.WarnOverCurrent;
                chkWarnHt.Checked = _model.WarnHighTemp;
                chkWarnLt.Checked = _model.WarnLowTemp;
                chkWarnVoltDiff.Checked = _model.WarnVoltDiff;
                chkWarnLowCap.Checked = _model.WarnLowCapacity;

                chkProtOv.Checked = _model.ProtOverVolt;
                chkProtUv.Checked = _model.ProtUnderVolt;
                chkProtOc.Checked = _model.ProtOverCurrent;
                chkProtSc.Checked = _model.ProtShortCircuit;
                chkProtHt.Checked = _model.ProtHighTemp;
                chkProtLt.Checked = _model.ProtUnderTemp;
                chkProtSys.Checked = _model.ProtSystemFault;
                chkProtSoftStart.Checked = _model.ProtSoftStart;
            }
            _isUpdatingUi = false;
        }

        private void UpdateNumericCommitted(NumericUpDown num, decimal val)
        {
            if (num == null) return;
            num.Value = val;
            _committedValues[num] = val;
            _isDirty[num] = false;
        }

        private void Comm_OnLogEvent(object sender, LogEventArgs e)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            this.BeginInvoke(new Action(delegate()
            {
                string timeStr = e.Timestamp.ToString("HH:mm:ss");
                string dirStr = e.IsTx ? "TX" : "RX";

                // 默认颜色: TX天蓝, RX浅绿
                Color textColor = e.IsTx ? Color.FromArgb(120, 180, 250) : Color.FromArgb(100, 220, 140);

                // 告警/保护报文摘要智能高亮:
                if (!string.IsNullOrEmpty(e.Summary))
                {
                    if (e.Summary.Contains("保护") || e.Summary.Contains("故障") || e.Summary.Contains("Prot"))
                    {
                        textColor = Color.FromArgb(248, 113, 113); // 亮红 (保护跳闸)
                    }
                    else if (e.Summary.Contains("告警") || e.Summary.Contains("警告") || e.Summary.Contains("Warn"))
                    {
                        textColor = Color.FromArgb(251, 191, 36);  // 亮橙黄 (告警提示)
                    }
                }

                StringBuilder sb = new StringBuilder();
                if (chkShowTime.Checked)
                {
                    sb.Append(string.Format("[{0}] {1}: ", timeStr, dirStr));
                }
                else
                {
                    sb.Append(string.Format("{0}: ", dirStr));
                }

                if (chkShowHex.Checked)
                {
                    sb.Append(BitConverter.ToString(e.RawBytes).Replace("-", " "));
                }

                if (!string.IsNullOrEmpty(e.Summary))
                {
                    sb.Append("  " + e.Summary);
                }

                if (e.LatencyMs > 0)
                {
                    sb.Append(string.Format(" (时延: {0}ms)", e.LatencyMs));
                }
                sb.AppendLine();

                string logText = sb.ToString();
                bool autoScroll = chkAutoScroll.Checked;

                if (autoScroll)
                {
                    rtbLog.SelectionStart = rtbLog.TextLength;
                    rtbLog.SelectionLength = 0;
                    rtbLog.SelectionColor = textColor;
                    rtbLog.AppendText(logText);
                    rtbLog.ScrollToCaret();
                }
                else
                {
                    SendMessage(rtbLog.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                    try
                    {
                        int vScroll = GetScrollPos(rtbLog.Handle, SB_VERT);
                        int selStart = rtbLog.SelectionStart;
                        int selLen = rtbLog.SelectionLength;

                        rtbLog.SelectionStart = rtbLog.TextLength;
                        rtbLog.SelectionLength = 0;
                        rtbLog.SelectionColor = textColor;
                        rtbLog.AppendText(logText);

                        rtbLog.Select(selStart, selLen);
                        SetScrollPos(rtbLog.Handle, SB_VERT, vScroll, true);
                        PostMessageA(rtbLog.Handle, WM_VSCROLL, SB_THUMBPOSITION + 0x10000 * vScroll, 0);
                    }
                    finally
                    {
                        SendMessage(rtbLog.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                        rtbLog.Invalidate();
                    }
                }

                UpdateStatsLabels();
                if (e.LatencyMs > 0)
                {
                    lblLatency.Text = string.Format("响应时延: {0} ms", e.LatencyMs);
                }
            }));
        }

        private void UpdateStatsLabels()
        {
            lblRxCount.Text = string.Format("RX: {0}", _comm.RxCount);
            lblTxCount.Text = string.Format("TX: {0}", _comm.TxCount);
            lblErrCount.Text = string.Format("错误: {0}", _comm.ErrorCount);
        }

        private void Comm_OnStatusChanged(object sender, string msg)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(new Action(delegate()
            {
                lblStatusText.Text = msg;
            }));
        }

        private void BtnExportLog_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "文本文件 (*.txt)|*.txt|日志文件 (*.log)|*.log";
                sfd.FileName = string.Format("BMS_Log_{0:yyyyMMdd_HHmmss}.txt", DateTime.Now);
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllText(sfd.FileName, rtbLog.Text, Encoding.UTF8);
                    MessageBox.Show("日志已成功保存！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                if (splitMain != null && splitMain.Width > 750)
                {
                    splitMain.SplitterDistance = (int)(splitMain.Width * 0.52);
                    splitMain.Panel1MinSize = 350;
                    splitMain.Panel2MinSize = 350;
                }
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _comm.Close();
            base.OnFormClosing(e);
        }
    }
}
