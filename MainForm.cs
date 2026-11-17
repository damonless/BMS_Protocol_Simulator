using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("BMS 协议仿真工具")]
[assembly: AssemblyProduct("BMS 协议仿真工具")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

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

    // ── 应用程序图标辅助加载器 ──
    public static class AppIconHelper
    {
        private static Icon _cachedIcon;

        public static Icon GetAppIcon()
        {
            if (_cachedIcon != null) return _cachedIcon;

            // 1. 优先尝试从本地 app.ico 文件加载
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    _cachedIcon = new Icon(iconPath);
                    return _cachedIcon;
                }
            }
            catch { }

            // 2. 尝试从当前可执行文件嵌入的 Win32 PE 资源中提取
            try
            {
                Icon assoc = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (assoc != null)
                {
                    _cachedIcon = assoc;
                    return _cachedIcon;
                }
            }
            catch { }

            return null;
        }
    }

    // ── 在线从机扫描进度提示弹窗 ──
    public class ScanProgressDialog : Form
    {
        private SerialCommManager _comm;
        private ProgressBar pbProgress;
        private Label lblCurrent;
        private Label lblFound;
        private Button btnCancel;
        private bool _isCompleted = false;

        public ScanProgressDialog(SerialCommManager comm)
        {
            _comm = comm;
            BuildUI();
            if (_comm != null)
            {
                _comm.OnScanProgress += Comm_OnScanProgress;
                _comm.OnScanPacksCompleted += Comm_OnScanPacksCompleted;
                _comm.OnPortDisconnected += Comm_OnPortDisconnected;
            }
        }

        private void BuildUI()
        {
            this.Text = "扫描从机";
            this.ClientSize = new Size(390, 155);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            this.BackColor = Color.White;

            try
            {
                Icon icon = AppIconHelper.GetAppIcon();
                if (icon != null) this.Icon = icon;
            }
            catch { }

            Label lblTitle = new Label
            {
                Text = "正在扫描总线在线电池包...",
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 41, 59),
                Location = new Point(22, 16),
                AutoSize = true
            };

            lblCurrent = new Label
            {
                Text = "准备扫描 (ID 1 ~ 15, 0)...",
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular),
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(22, 40),
                Size = new Size(346, 18),
                AutoEllipsis = true
            };

            pbProgress = new ProgressBar
            {
                Location = new Point(22, 62),
                Size = new Size(346, 18),
                Minimum = 0,
                Maximum = 16,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };

            lblFound = new Label
            {
                Text = "已发现从机: 暂无",
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular),
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(22, 98),
                AutoSize = true
            };

            btnCancel = new Button
            {
                Text = "取消",
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
                Size = new Size(78, 28),
                Location = new Point(290, 92),
                UseVisualStyleBackColor = true
            };
            btnCancel.Click += delegate(object s, EventArgs e) {
                if (_comm != null) _comm.CancelScan();
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };

            this.Controls.Add(lblTitle);
            this.Controls.Add(lblCurrent);
            this.Controls.Add(pbProgress);
            this.Controls.Add(lblFound);
            this.Controls.Add(btnCancel);
        }

        private void Comm_OnScanProgress(object sender, ScanProgressEventArgs e)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(new Action(delegate()
            {
                if (this.IsDisposed) return;
                pbProgress.Value = Math.Max(0, Math.Min(pbProgress.Maximum, e.CurrentStep));
                lblCurrent.Text = string.Format("正在探测从机 ID: {0} (进度: {1}/{2})", e.CurrentId, e.CurrentStep, e.TotalSteps);
                if (e.FoundPacks != null && e.FoundPacks.Count > 0)
                {
                    lblFound.Text = string.Format("已发现从机: ID {0}", string.Join(", ", e.FoundPacks.ConvertAll<string>(delegate(byte b) { return b.ToString(); }).ToArray()));
                    lblFound.ForeColor = Color.FromArgb(22, 101, 52);
                }
            }));
        }

        private void Comm_OnScanPacksCompleted(object sender, List<byte> foundPacks)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(new Action(delegate()
            {
                if (this.IsDisposed) return;
                _isCompleted = true;
                this.DialogResult = DialogResult.OK;
                this.Close();
            }));
        }

        private void Comm_OnPortDisconnected(object sender, PortDisconnectedEventArgs e)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(new Action(delegate()
            {
                if (this.IsDisposed) return;
                _isCompleted = true;
                this.DialogResult = DialogResult.Abort;
                this.Close();
            }));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_comm != null)
            {
                _comm.StartScanOnlinePacks();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_isCompleted && _comm != null)
            {
                _comm.CancelScan();
            }
            if (_comm != null)
            {
                _comm.OnScanProgress -= Comm_OnScanProgress;
                _comm.OnScanPacksCompleted -= Comm_OnScanPacksCompleted;
                _comm.OnPortDisconnected -= Comm_OnPortDisconnected;
            }
            base.OnFormClosing(e);
        }
    }

    // ── 报文深度解剖弹窗 (双击实时监视器任意报文行触发) ──
    public class FrameInspectorDialog : Form
    {
        private Font _rtbFont;

        public FrameInspectorDialog(string reportText)
        {
            this.Text = "报文深度解剖器 (Frame Anatomy Inspector)";
            this.MinimumSize = new Size(680, 420);
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            this.MinimizeBox = false;
            this.KeyPreview = true;

            try
            {
                Icon icon = AppIconHelper.GetAppIcon();
                if (icon != null) this.Icon = icon;
            }
            catch { }

            Panel pnlBody = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42),
                Padding = new Padding(16, 12, 16, 12)
            };

            RichTextBox rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(226, 232, 240),
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                DetectUrls = false
            };

            pnlBody.Controls.Add(rtb);

            Panel pnlBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Size = new Size(780, 44),
                BackColor = Color.FromArgb(241, 245, 249)
            };

            Button btnCopy = new Button
            {
                Text = "复制报告",
                Width = 100,
                Height = 28,
                Location = new Point(16, 8),
                UseVisualStyleBackColor = true
            };
            btnCopy.Click += delegate(object s, EventArgs e) {
                try
                {
                    Clipboard.SetText(rtb.Text);
                    btnCopy.Text = "✓ 已复制";
                }
                catch { }
            };

            Button btnClose = new Button
            {
                Text = "关闭 (Esc)",
                Width = 90,
                Height = 28,
                Location = new Point(pnlBottom.Width - 90 - 16, 8),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
                UseVisualStyleBackColor = true
            };
            pnlBottom.Resize += delegate(object s, EventArgs e) {
                btnClose.Location = new Point(Math.Max(130, pnlBottom.ClientSize.Width - btnClose.Width - 16), 8);
            };

            pnlBottom.Controls.Add(btnCopy);
            pnlBottom.Controls.Add(btnClose);

            this.CancelButton = btnClose;
            this.Controls.Add(pnlBody);
            this.Controls.Add(pnlBottom);

            // 强制先创建 RichTextBox 原生句柄并关闭 AutoFont/DualFont，防止 CJK 与制表符触发非等宽字体回退
            IntPtr h = rtb.Handle;
            rtb.LanguageOption = (RichTextBoxLanguageOptions)0;
            _rtbFont = new Font("Consolas", 10F, FontStyle.Regular);
            rtb.Font = _rtbFont;
            rtb.Text = reportText ?? "";
            rtb.SelectionStart = 0;
            rtb.SelectionLength = 0;

            // 根据实际渲染像素尺寸与当前屏幕工作区动态计算弹窗最佳大小，免去手动全屏
            AdjustDialogSizeToContent(rtb, pnlBody, pnlBottom);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_rtbFont != null)
            {
                _rtbFont.Dispose();
                _rtbFont = null;
            }
            base.OnFormClosed(e);
        }

        private void AdjustDialogSizeToContent(RichTextBox rtb, Panel pnlBody, Panel pnlBottom)
        {
            try
            {
                Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
                string[] lines = rtb.Lines;
                int lineCount = (lines != null && lines.Length > 0) ? lines.Length : 1;

                int maxLinePixelX = 0;
                int maxCols = 0;
                for (int i = 0; i < lineCount; i++)
                {
                    string line = lines[i] ?? "";
                    int cols = FrameAnatomyInspector.GetDisplayWidth(line);
                    if (cols > maxCols) maxCols = cols;

                    int firstChar = rtb.GetFirstCharIndexFromLine(i);
                    if (firstChar >= 0)
                    {
                        Point ptEnd = rtb.GetPositionFromCharIndex(firstChar + line.Length);
                        if (ptEnd.X > maxLinePixelX) maxLinePixelX = ptEnd.X;
                    }
                }

                int fontHeight = rtb.Font.Height;
                int lastCharIdx = Math.Max(0, rtb.TextLength - 1);
                Point ptBottom = rtb.GetPositionFromCharIndex(lastCharIdx);
                int measuredContentHeight = Math.Max(lineCount * (fontHeight + 2), ptBottom.Y + fontHeight + 16);
                int measuredContentWidth = Math.Max(maxCols * 8, maxLinePixelX + 20);

                int padH = pnlBody.Padding.Left + pnlBody.Padding.Right + 24;
                int padV = pnlBody.Padding.Top + pnlBody.Padding.Bottom + pnlBottom.Height + 24;

                int maxAllowedClientW = (int)(workArea.Width * 0.90);
                int maxAllowedClientH = (int)(workArea.Height * 0.90);

                int targetClientW = Math.Max(780, Math.Min(measuredContentWidth + padH, maxAllowedClientW));
                int targetClientH = Math.Max(420, Math.Min(measuredContentHeight + padV, maxAllowedClientH));

                this.ClientSize = new Size(targetClientW, targetClientH);
            }
            catch
            {
                this.Size = new Size(820, 680);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                Rectangle workArea = Screen.FromControl(this).WorkingArea;
                int newX = this.Left;
                int newY = this.Top;
                if (this.Right > workArea.Right) newX = Math.Max(workArea.Left, workArea.Right - this.Width);
                if (this.Bottom > workArea.Bottom) newY = Math.Max(workArea.Top, workArea.Bottom - this.Height);
                if (newX < workArea.Left) newX = workArea.Left;
                if (newY < workArea.Top) newY = workArea.Top;
                this.Location = new Point(newX, newY);
            }
            catch { }
        }
    }

    public class MainForm : Form
    {
        private class TelemetryNumericUpDown : NumericUpDown
        {
            public override void UpButton()
            {
                if (this.ReadOnly) return;
                base.UpButton();
            }

            public override void DownButton()
            {
                if (this.ReadOnly) return;
                base.DownButton();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (this.ReadOnly) return;
                base.OnMouseWheel(e);
            }
        }

        // ── Win32 API 锁定、热插拔监听与双向防闪烁滚动 ──
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref POINT lParam);
        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETSCROLLPOS = 0x04DD;
        private const int EM_SETSCROLLPOS = 0x04DE;
        private const int EM_GETLINECOUNT = 0x00BA;
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        private BmsDataModel _model;
        private SerialCommManager _comm;
        private SplitContainer splitMain;
        private Button btnProfile3KU;
        private Button btnProfile6KU;

        // ── 顶部配置栏控件 ──
        private ComboBox cboPort;
        private Button btnRefresh;
        private ComboBox cboBaud;
        private ComboBox cboProtocol;
        private ComboBox cboWorkMode;
        private CheckBox chkReplyAllIds;
        private Label lblIdTag;
        private NumericUpDown numSpecificId;
        private Button btnConnect;
        private Label lblPortStatus;

        // ── 主机与从机模式专属控制栏控件 ──
        private Label lblPollInterval;
        private CheckBox chkSimSilence;
        private CheckBox chkSimCrcErr;
        private Label lblSimDelay;
        private ComboBox cboSimDelay;
        private Label lblCellDelta;
        private ComboBox cboCellDelta;
        private Button btnScanPacks;
        private ComboBox cboPollInterval;
        private Button btnPollPause;
        private Button btnSinglePoll;
        private Label lblHint;
        private FlowLayoutPanel flpQuickPresets;

        // ── 模拟量控件 ──
        private NumericUpDown numVoltage, numCurrent, numTemp, numSoc, numSoh;
        private NumericUpDown numRemCap, numFullCap, numMaxChgI, numMaxDisI, numCvVolt, numCutoffVolt, numCycleCount;

        // ── 模拟量确认与回滚状态映射 ──
        private Dictionary<NumericUpDown, decimal> _committedValues = new Dictionary<NumericUpDown, decimal>();
        private Dictionary<NumericUpDown, bool> _isDirty = new Dictionary<NumericUpDown, bool>();
        private Dictionary<Control, Timer> _flashTimers = new Dictionary<Control, Timer>();
        private NumericUpDown _currentFocusedControl = null;

        // ── 标志位控件 ──
        private CheckBox chkChgEn, chkDisEn, chkForceChg, chkFullChg, chkBalance, chkSleep;
        private CheckBox chkWarnSingleOv, chkWarnSingleUv, chkWarnGlobalOv, chkWarnGlobalUv;
        private CheckBox chkWarnOc, chkWarnHt, chkWarnLt, chkWarnVoltDiff, chkWarnLowCap;
        private CheckBox chkProtOv, chkProtUv, chkProtOc, chkProtSc, chkProtHt, chkProtLt, chkProtSys, chkProtSoftStart;

        // 用户主动配置的最大充放电电流记忆缓存
        private double _userConfiguredMaxChgI = 100.0;
        private double _userConfiguredMaxDisI = 100.0;

        // ── 监视器控件 ──
        private RichTextBox rtbLog;
        private Label lblRxCount, lblTxCount, lblErrCount, lblLatency;
        private CheckBox chkAutoScroll, chkShowTime, chkShowHex, chkFilterAlarmOnly;
        private TextBox txtLogFilter;
        private Button btnClearLog, btnExportLog;

        // ── 状态栏 ──
        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatusText, lblProtocolBadge;

        // ── 智能机型与协议自适应识别与扩展设定 ──
        private CheckBox chkAutoAdapt;
        private Label lblCellCount;
        private ComboBox cboCellCount;
        private Label lblFaultInject;
        private ComboBox cboFaultInject;

        private Font _flagFontRegular;
        private Font _flagFontBold;
        private Timer _deviceChangeTimer;

        private bool _isUpdatingUi = false;
        private bool _isConfigInitialized = false;
        private bool _hasMasterTelemetryReceived = false;

        public MainForm()
        {
            this.KeyPreview = true;
            this.KeyDown += MainForm_KeyDown;

            _flagFontRegular = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            _flagFontBold = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

            _deviceChangeTimer = new Timer { Interval = 250 };
            _deviceChangeTimer.Tick += DeviceChangeTimer_Tick;

            _model = new BmsDataModel();
            _comm = new SerialCommManager(_model);
            _comm.OnLogEvent += Comm_OnLogEvent;
            _comm.OnStatusChanged += Comm_OnStatusChanged;
            _comm.OnMasterTelemetryUpdated += Comm_OnMasterTelemetryUpdated;
            _comm.OnScanPacksCompleted += Comm_OnScanPacksCompleted;
            _comm.OnAutoAdapted += Comm_OnAutoAdapted;
            _comm.OnPortDisconnected += Comm_OnPortDisconnected;

            BuildUI();
            RefreshPorts(true);
            ApplyLoadedStartupConfig();
            SyncModelToUI();
            UpdateWorkModeUI();
            UpdateProtocolControlStates();
            UpdateProtocolBadge();
            _isConfigInitialized = true;
        }

        private void BuildUI()
        {
            this.Text = "BMS 协议仿真工具 V1.1";
            this.Size = new Size(1200, 740);
            this.MinimumSize = new Size(1060, 640);
            this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = SystemColors.Control;

            try
            {
                Icon icon = AppIconHelper.GetAppIcon();
                if (icon != null) this.Icon = icon;
            }
            catch { }

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
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 95f));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            this.Controls.Add(mainLayout);
            this.Controls.Add(statusStrip);
            statusStrip.SendToBack();

            // ══════════════════════════════════════════════════════════
            // 1. 顶部串口与协议控制条 (两行规范化布局，杜绝遮挡与截断)
            // ══════════════════════════════════════════════════════════
            GroupBox gbTop = new GroupBox();
            gbTop.Text = "通信链路与协议设定";
            gbTop.Dock = DockStyle.Fill;
            gbTop.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);

            TableLayoutPanel tlpTop = new TableLayoutPanel();
            tlpTop.Dock = DockStyle.Fill;
            tlpTop.RowCount = 2;
            tlpTop.ColumnCount = 1;
            tlpTop.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            tlpTop.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            tlpTop.Padding = new Padding(2, 0, 2, 2);
            tlpTop.Margin = new Padding(0);

            // ── 第 1 行：基础通信配置与连接 ──
            FlowLayoutPanel flpTopRow1 = new FlowLayoutPanel();
            flpTopRow1.Dock = DockStyle.Fill;
            flpTopRow1.Margin = new Padding(0);
            flpTopRow1.Padding = new Padding(2, 2, 2, 0);
            flpTopRow1.WrapContents = false;
            flpTopRow1.AutoScroll = false;

            Label lblPort = new Label { Text = "端口:", AutoSize = true, Margin = new Padding(2, 6, 2, 0) };
            flpTopRow1.Controls.Add(lblPort);

            cboPort = new ComboBox { Width = 75, DropDownStyle = ComboBoxStyle.DropDownList };
            flpTopRow1.Controls.Add(cboPort);

            btnRefresh = new Button { Text = "刷新", Width = 44, Height = 25, Margin = new Padding(2, 2, 6, 0) };
            btnRefresh.Click += delegate(object s, EventArgs e) { RefreshPorts(true); };
            flpTopRow1.Controls.Add(btnRefresh);

            Label lblBaud = new Label { Text = "波特率:", AutoSize = true, Margin = new Padding(4, 6, 2, 0) };
            flpTopRow1.Controls.Add(lblBaud);

            cboBaud = new ComboBox { Width = 75, DropDownStyle = ComboBoxStyle.DropDownList };
            cboBaud.Items.AddRange(new object[] { "9600", "2400", "4800", "19200", "38400", "57600", "115200" });
            cboBaud.SelectedIndex = 0;
            cboBaud.SelectedIndexChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) SavePersistentConfig();
            };
            flpTopRow1.Controls.Add(cboBaud);

            Label lblProt = new Label { Text = "协议:", AutoSize = true, Margin = new Padding(6, 6, 2, 0) };
            flpTopRow1.Controls.Add(lblProt);

            cboProtocol = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            cboProtocol.Items.AddRange(new object[] {
                "CVTE",
                "GROWATT",
                "VOLTRONIC",
                "PYLONTECH",
                "SHENGYANG",
                "JBD"
            });
            cboProtocol.SelectedIndex = 0;
            cboProtocol.SelectedIndexChanged += CboProtocol_SelectedIndexChanged;
            flpTopRow1.Controls.Add(cboProtocol);

            Label lblMode = new Label { Text = "工作模式:", AutoSize = true, Margin = new Padding(6, 6, 2, 0) };
            flpTopRow1.Controls.Add(lblMode);

            cboWorkMode = new ComboBox { Width = 95, DropDownStyle = ComboBoxStyle.DropDownList };
            cboWorkMode.Items.AddRange(new object[] { "从机模拟", "主机轮询" });
            cboWorkMode.SelectedIndex = 0;
            cboWorkMode.SelectedIndexChanged += delegate(object s, EventArgs e) {
                UpdateWorkModeUI();
            };
            flpTopRow1.Controls.Add(cboWorkMode);

            btnConnect = new Button { Text = "打开串口", Width = 85, Height = 25, Margin = new Padding(8, 2, 6, 0) };
            btnConnect.Click += BtnConnect_Click;
            flpTopRow1.Controls.Add(btnConnect);

            lblPortStatus = new Label
            {
                Text = "未连接",
                AutoSize = false,
                Width = 70,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Gray,
                Margin = new Padding(2, 5, 2, 0)
            };
            flpTopRow1.Controls.Add(lblPortStatus);

            tlpTop.Controls.Add(flpTopRow1, 0, 0);

            // ── 第 2 行：模式专属参数与控制动作 ──
            FlowLayoutPanel flpTopRow2 = new FlowLayoutPanel();
            flpTopRow2.Dock = DockStyle.Fill;
            flpTopRow2.Margin = new Padding(0);
            flpTopRow2.Padding = new Padding(2, 2, 2, 0);
            flpTopRow2.WrapContents = false;
            flpTopRow2.AutoScroll = false;

            chkReplyAllIds = new CheckBox { Text = "全ID应答", AutoSize = true, Checked = true, Margin = new Padding(2, 6, 4, 0) };
            chkReplyAllIds.CheckedChanged += delegate(object s, EventArgs e) {
                _comm.ReplyAllIds = chkReplyAllIds.Checked;
                numSpecificId.Enabled = !chkReplyAllIds.Checked;
                if (!_isUpdatingUi) SavePersistentConfig();
            };
            flpTopRow2.Controls.Add(chkReplyAllIds);

            lblIdTag = new Label { Text = "从机ID:", AutoSize = true, Margin = new Padding(2, 6, 2, 0) };
            flpTopRow2.Controls.Add(lblIdTag);

            numSpecificId = new NumericUpDown { Minimum = 0, Maximum = 15, Value = 1, Width = 42, Enabled = false, Margin = new Padding(0, 3, 8, 0) };
            numSpecificId.ValueChanged += delegate(object s, EventArgs e) {
                int newId = (int)numSpecificId.Value;
                bool idChanged = (_comm.TargetSlaveId != (byte)newId);
                _comm.SpecificId = newId;
                _comm.TargetSlaveId = (byte)newId;
                if (!_isUpdatingUi)
                {
                    if (idChanged && _comm.CurrentWorkMode == WorkMode.MasterPoller)
                    {
                        _hasMasterTelemetryReceived = false;
                        ClearMasterTelemetryDisplay();
                        _comm.NotifyContextReset();
                    }
                    SavePersistentConfig();
                }
            };
            flpTopRow2.Controls.Add(numSpecificId);

            // 从机通信层故障注入与单芯压差模拟控件 (简洁无图标、标准文本色)
            chkSimSilence = new CheckBox { Text = "模拟断线", AutoSize = true, Margin = new Padding(4, 6, 6, 0) };
            chkSimSilence.CheckedChanged += delegate(object s, EventArgs e) {
                _comm.SimulateSilence = chkSimSilence.Checked;
                lblStatusText.Text = chkSimSilence.Checked
                    ? "已开启 [模拟断线]：从机不回传应答帧，用于验证逆变器通信失联故障"
                    : "已关闭 [模拟断线]：从机恢复正常应答";
            };
            flpTopRow2.Controls.Add(chkSimSilence);

            chkSimCrcErr = new CheckBox { Text = "校验错包", AutoSize = true, Margin = new Padding(4, 6, 6, 0) };
            chkSimCrcErr.CheckedChanged += delegate(object s, EventArgs e) {
                _comm.SimulateCorruptChecksum = chkSimCrcErr.Checked;
                lblStatusText.Text = chkSimCrcErr.Checked
                    ? "已开启 [校验错包]：从机应答帧注入 CRC/Checksum 错误，用于验证防误码容错"
                    : "已关闭 [校验错包]：从机恢复正常校验码";
            };
            flpTopRow2.Controls.Add(chkSimCrcErr);

            lblSimDelay = new Label { Text = "延时:", AutoSize = true, Margin = new Padding(4, 6, 2, 0) };
            flpTopRow2.Controls.Add(lblSimDelay);

            cboSimDelay = new ComboBox { Width = 76, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 6, 0) };
            cboSimDelay.Items.AddRange(new object[] { "正常", "+100ms", "+300ms", "+800ms" });
            cboSimDelay.SelectedIndex = 0;
            cboSimDelay.SelectedIndexChanged += delegate(object s, EventArgs e) {
                switch (cboSimDelay.SelectedIndex)
                {
                    case 0: _comm.ExtraResponseDelayMs = 0; break;
                    case 1: _comm.ExtraResponseDelayMs = 100; break;
                    case 2: _comm.ExtraResponseDelayMs = 300; break;
                    case 3: _comm.ExtraResponseDelayMs = 800; break;
                }
            };
            flpTopRow2.Controls.Add(cboSimDelay);

            lblCellDelta = new Label { Text = "压差:", AutoSize = true, Margin = new Padding(4, 6, 2, 0) };
            flpTopRow2.Controls.Add(lblCellDelta);

            cboCellDelta = new ComboBox { Width = 76, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 4, 0) };
            cboCellDelta.Items.AddRange(new object[] { "20mV", "50mV", "150mV", "300mV" });
            cboCellDelta.SelectedIndex = 0;
            cboCellDelta.SelectedIndexChanged += delegate(object s, EventArgs e) {
                int deltaMv = 20;
                if (cboCellDelta.SelectedIndex == 1) deltaMv = 50;
                else if (cboCellDelta.SelectedIndex == 2) deltaMv = 150;
                else if (cboCellDelta.SelectedIndex == 3) deltaMv = 300;
                lock (_model.SyncRoot) { _model.CellImbalanceMv = deltaMv; }
                lblStatusText.Text = string.Format("单芯压差已设为 {0}mV", deltaMv);
            };
            flpTopRow2.Controls.Add(cboCellDelta);

            lblFaultInject = new Label { Text = "异常注入:", AutoSize = true, Margin = new Padding(4, 6, 2, 0) };
            flpTopRow2.Controls.Add(lblFaultInject);

            cboFaultInject = new ComboBox { Width = 148, DropDownWidth = 210, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 4, 0) };
            cboFaultInject.Items.AddRange(new object[] {
                "无 (正常编码)",
                "圣阳: 非关键字段置0xFFFF",
                "圣阳: 总压与SOC置0xFFFF",
                "JBD: 强制FET Bit7=1 (100mA)"
            });
            cboFaultInject.SelectedIndex = 0;
            cboFaultInject.SelectedIndexChanged += delegate(object s, EventArgs e) {
                SlaveFaultInjectionMode mode = SlaveFaultInjectionMode.None;
                if (cboFaultInject.SelectedIndex == 1) mode = SlaveFaultInjectionMode.ShengyangInvalidNonCritical;
                else if (cboFaultInject.SelectedIndex == 2) mode = SlaveFaultInjectionMode.ShengyangInvalidCritical;
                else if (cboFaultInject.SelectedIndex == 3) mode = SlaveFaultInjectionMode.JbdForceUnit100mA;
                lock (_model.SyncRoot) { _model.FaultInjection = mode; }
                if (!_isUpdatingUi)
                {
                    lblStatusText.Text = string.Format("从机异常注入模式: {0}", cboFaultInject.SelectedItem);
                }
            };
            flpTopRow2.Controls.Add(cboFaultInject);

            btnScanPacks = new Button { Text = "扫描从机", Width = 72, Height = 25, Margin = new Padding(4, 2, 6, 0), Visible = false };
            btnScanPacks.Click += delegate(object s, EventArgs e) {
                if (_comm.IsRunning && !_comm.IsPhysicalPortOpen)
                {
                    _comm.NotifyExternalPortLost("检测到串口物理连接已断开");
                    return;
                }
                if (!_comm.IsOpen) { MessageBox.Show("请先打开串口再执行扫描！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                btnScanPacks.Enabled = false;
                try
                {
                    using (ScanProgressDialog dlg = new ScanProgressDialog(_comm))
                    {
                        dlg.ShowDialog(this);
                    }
                }
                finally
                {
                    if (!btnScanPacks.IsDisposed) btnScanPacks.Enabled = true;
                }
            };
            flpTopRow2.Controls.Add(btnScanPacks);

            lblPollInterval = new Label { Text = "轮询周期:", AutoSize = true, Margin = new Padding(4, 6, 2, 0), Visible = false };
            flpTopRow2.Controls.Add(lblPollInterval);

            cboPollInterval = new ComboBox { Width = 76, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(2, 3, 6, 0), Visible = false };
            cboPollInterval.Items.AddRange(new object[] { "100ms", "200ms", "500ms", "1000ms", "2000ms", "手动单步" });
            cboPollInterval.SelectedIndex = 2; // 默认 500ms
            cboPollInterval.SelectedIndexChanged += delegate(object s, EventArgs e) {
                switch (cboPollInterval.SelectedIndex)
                {
                    case 0: _comm.PollIntervalMs = 100; break;
                    case 1: _comm.PollIntervalMs = 200; break;
                    case 2: _comm.PollIntervalMs = 500; break;
                    case 3: _comm.PollIntervalMs = 1000; break;
                    case 4: _comm.PollIntervalMs = 2000; break;
                    case 5: _comm.PollIntervalMs = 0; break; // 手动单步
                }
                _comm.NotifyContextReset();
            };
            flpTopRow2.Controls.Add(cboPollInterval);

            btnPollPause = new Button { Text = "暂停轮询", Width = 72, Height = 25, Margin = new Padding(4, 2, 6, 0), Visible = false };
            btnPollPause.Click += delegate(object s, EventArgs e) {
                _comm.IsPollingPaused = !_comm.IsPollingPaused;
                btnPollPause.Text = _comm.IsPollingPaused ? "恢复轮询" : "暂停轮询";
                lblStatusText.Text = _comm.IsPollingPaused ? "轮询已暂停" : "轮询已恢复运行";
                if (!_comm.IsPollingPaused) _comm.NotifyContextReset();
            };
            flpTopRow2.Controls.Add(btnPollPause);

            btnSinglePoll = new Button { Text = "单步轮询", Width = 72, Height = 25, Margin = new Padding(2, 2, 6, 0), Visible = false };
            btnSinglePoll.Click += delegate(object s, EventArgs e) {
                if (_comm.IsRunning && !_comm.IsPhysicalPortOpen)
                {
                    _comm.NotifyExternalPortLost("检测到串口物理连接已断开");
                    return;
                }
                if (!_comm.IsOpen) { MessageBox.Show("请先打开串口再执行单步轮询！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                _comm.TriggerSinglePoll();
            };
            flpTopRow2.Controls.Add(btnSinglePoll);

            tlpTop.Controls.Add(flpTopRow2, 0, 1);

            gbTop.Controls.Add(tlpTop);
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
                if (_currentFocusedControl != null)
                {
                    if (_isDirty.ContainsKey(_currentFocusedControl) && _isDirty[_currentFocusedControl])
                    {
                        CommitSingleControl(_currentFocusedControl);
                    }
                    _currentFocusedControl = null;
                }
                statusStrip.Focus();
            };

            TableLayoutPanel pnlTab = new TableLayoutPanel();
            pnlTab.Dock = DockStyle.Fill;
            pnlTab.RowCount = 2;
            pnlTab.ColumnCount = 1;
            pnlTab.RowStyles.Add(new RowStyle(SizeType.Absolute, 226f));
            pnlTab.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            pnlTab.MouseDown += onBlankMouseDown;
            parent.MouseDown += onBlankMouseDown;

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.RowCount = 6;
            grid.ColumnCount = 4;
            grid.Padding = new Padding(4, 4, 4, 4);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.MouseDown += onBlankMouseDown;

            for (int i = 0; i < 6; i++)
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
            numVoltage = CreateBoundNumeric(2, 0, 1000, 0.1M, 28.00M);
            grid.Controls.Add(numVoltage, 1, 0);

            grid.Controls.Add(createLbl("充放电流 (A):"), 2, 0);
            numCurrent = CreateBoundNumeric(2, -1000, 1000, 1.0M, 0.00M);
            grid.Controls.Add(numCurrent, 3, 0);

            // 行 1: SOC (%) / SOH (%)
            grid.Controls.Add(createLbl("电池 SOC (%):"), 0, 1);
            numSoc = CreateBoundNumeric(1, 0, 100, 1M, 100M);
            grid.Controls.Add(numSoc, 1, 1);

            grid.Controls.Add(createLbl("健康度 SOH (%):"), 2, 1);
            numSoh = CreateBoundNumeric(0, 0, 100, 1M, 100M);
            grid.Controls.Add(numSoh, 3, 1);

            // 行 2: CV 恒压点 (V) / 放电截止点 (V)
            grid.Controls.Add(createLbl("CV 恒压点 (V):"), 0, 2);
            numCvVolt = CreateBoundNumeric(2, 0, 1000, 0.1M, 28.80M);
            grid.Controls.Add(numCvVolt, 1, 2);

            grid.Controls.Add(createLbl("放电截止点 (V):"), 2, 2);
            numCutoffVolt = CreateBoundNumeric(2, 0, 1000, 0.1M, 21.00M);
            grid.Controls.Add(numCutoffVolt, 3, 2);

            // 行 3: 剩余容量 (Ah) / 满充容量 (Ah)
            grid.Controls.Add(createLbl("剩余容量 (Ah):"), 0, 3);
            numRemCap = CreateBoundNumeric(1, 0, 10000, 5M, 100M);
            grid.Controls.Add(numRemCap, 1, 3);

            grid.Controls.Add(createLbl("满充容量 (Ah):"), 2, 3);
            numFullCap = CreateBoundNumeric(1, 0, 10000, 5M, 100M);
            grid.Controls.Add(numFullCap, 3, 3);

            // 行 4: 最大充电电流 (A) / 最大放电电流 (A)
            grid.Controls.Add(createLbl("最大充电电流 (A):"), 0, 4);
            numMaxChgI = CreateBoundNumeric(1, 0, 1000, 5M, 100M);
            grid.Controls.Add(numMaxChgI, 1, 4);

            grid.Controls.Add(createLbl("最大放电电流 (A):"), 2, 4);
            numMaxDisI = CreateBoundNumeric(1, 0, 1000, 5M, 100M);
            grid.Controls.Add(numMaxDisI, 3, 4);

            // 行 5: 最高温度 (°C) / 循环次数 (次)
            grid.Controls.Add(createLbl("最高温度 (°C):"), 0, 5);
            numTemp = CreateBoundNumeric(1, -50, 150, 1M, 28M);
            grid.Controls.Add(numTemp, 1, 5);

            grid.Controls.Add(createLbl("循环次数 (次):"), 2, 5);
            numCycleCount = CreateBoundNumeric(0, 0, 65535, 10M, 50M);
            grid.Controls.Add(numCycleCount, 3, 5);

            pnlTab.Controls.Add(grid, 0, 0);

            // ── 行 1: 24V / 48V 一键基准与串数设定栏 ──
            FlowLayoutPanel flpActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(4, 4, 4, 4),
                WrapContents = true
            };
            flpActions.MouseDown += onBlankMouseDown;

            btnProfile3KU = new Button
            {
                Text = "24V (3KU)",
                Width = 92,
                Height = 26,
                Margin = new Padding(2, 2, 4, 2)
            };
            btnProfile3KU.Click += delegate(object s, EventArgs e) {
                _model.ApplyProfile("3KU");
                _comm.ResetAdaptedModel();
                SyncModelToUI();
                CommitAllControls();
                lblStatusText.Text = "已应用 [24V (3KU) / 8S] 基准参数套系";
                SavePersistentConfig();
            };

            btnProfile6KU = new Button
            {
                Text = "48V (6KU)",
                Width = 92,
                Height = 26,
                Margin = new Padding(2, 2, 6, 2)
            };
            btnProfile6KU.Click += delegate(object s, EventArgs e) {
                _model.ApplyProfile("6KU");
                _comm.ResetAdaptedModel();
                SyncModelToUI();
                CommitAllControls();
                lblStatusText.Text = "已应用 [48V (6KU) / 16S] 基准参数套系";
                SavePersistentConfig();
            };

            lblCellCount = new Label
            {
                Text = "串数:",
                AutoSize = true,
                Margin = new Padding(4, 6, 2, 2)
            };
            lblCellCount.MouseDown += onBlankMouseDown;

            cboCellCount = new ComboBox
            {
                Width = 88,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 3, 6, 2)
            };
            cboCellCount.Items.AddRange(new object[] { "8S (24V)", "15S (48V)", "16S (48V)" });
            cboCellCount.SelectedIndex = 2;
            cboCellCount.SelectedIndexChanged += delegate(object s, EventArgs e) {
                if (_isUpdatingUi) return;
                int cells = 16;
                if (cboCellCount.SelectedIndex == 0) cells = 8;
                else if (cboCellCount.SelectedIndex == 1) cells = 15;
                double vPack;
                lock (_model.SyncRoot)
                {
                    _model.CellCount = cells;
                    vPack = _model.Voltage;
                }
                _comm.ResetAdaptedModel();
                lblStatusText.Text = string.Format("模拟串数已设为 {0} (当前总压 {1:F2}V)", cboCellCount.SelectedItem, vPack);
                SavePersistentConfig();
            };

            chkAutoAdapt = new CheckBox
            {
                Text = "自适应识别",
                AutoSize = true,
                Checked = false,
                Margin = new Padding(4, 5, 4, 2),
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular)
            };
            chkAutoAdapt.CheckedChanged += delegate(object s, EventArgs e) {
                _comm.AutoAdaptEnabled = chkAutoAdapt.Checked;
                if (!_isUpdatingUi)
                {
                    lblStatusText.Text = chkAutoAdapt.Checked
                        ? "智能自适应已开启：收到逆变器报文时将自动匹配机型与协议"
                        : "智能自适应已关闭：保持当前手动协议与套系配置";
                    SavePersistentConfig();
                }
            };

            lblHint = new Label
            {
                Text = "按 Enter 或失焦自动生效",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                Margin = new Padding(6, 6, 2, 2)
            };
            lblHint.MouseDown += onBlankMouseDown;

            flpActions.Controls.Add(btnProfile3KU);
            flpActions.Controls.Add(btnProfile6KU);
            flpActions.Controls.Add(lblCellCount);
            flpActions.Controls.Add(cboCellCount);
            flpActions.Controls.Add(chkAutoAdapt);
            flpActions.Controls.Add(lblHint);

            pnlTab.Controls.Add(flpActions, 0, 1);

            parent.Controls.Add(pnlTab);
        }

        // ══════════════════════════════════════════════════════════
        // 核心 NumericUpDown 交互行为与参数校验
        // ══════════════════════════════════════════════════════════
        private static TextBox GetInnerTextBox(NumericUpDown num)
        {
            if (num == null) return null;
            for (int i = 0; i < num.Controls.Count; i++)
            {
                if (num.Controls[i] is TextBox)
                    return (TextBox)num.Controls[i];
            }
            return null;
        }

        private NumericUpDown CreateBoundNumeric(int decimals, decimal min, decimal max, decimal increment, decimal initVal)
        {
            TelemetryNumericUpDown num = new TelemetryNumericUpDown
            {
                DecimalPlaces = decimals,
                Minimum = min,
                Maximum = max,
                Increment = increment,
                Value = initVal,
                Width = 110,
                Anchor = AnchorStyles.Left | AnchorStyles.Right
            };

            _committedValues[num] = initVal;
            _isDirty[num] = false;

            TextBox innerTb = GetInnerTextBox(num);

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
                        CommitSingleControl(num);
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
                    CommitSingleControl(num);
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
            TextBox tb = GetInnerTextBox(num);
            if (tb != null)
            {
                if (_comm != null && _comm.CurrentWorkMode == WorkMode.MasterPoller && !_hasMasterTelemetryReceived)
                {
                    tb.Text = "";
                }
                else
                {
                    tb.Text = (num.DecimalPlaces > 0) ? committed.ToString("F" + num.DecimalPlaces) : committed.ToString("F0");
                }
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

        // ── 参数非法边界拦截校验 ──
        private bool ValidateNumeric(NumericUpDown num, decimal value, out string message)
        {
            message = string.Empty;

            if (num == numRemCap)
            {
                decimal currentFull = _committedValues.ContainsKey(numFullCap) ? _committedValues[numFullCap] : numFullCap.Value;
                if (value > currentFull)
                {
                    message = string.Format("非法输入: 剩余容量 ({0}Ah) 不能大于满充总容量 ({1}Ah)！", value, currentFull);
                    return false;
                }
            }
            else if (num == numFullCap)
            {
                if (value <= 0)
                {
                    message = "非法输入: 满充总容量必须大于 0Ah！";
                    return false;
                }
                decimal currentRem = _committedValues.ContainsKey(numRemCap) ? _committedValues[numRemCap] : numRemCap.Value;
                if (value < currentRem)
                {
                    message = string.Format("非法输入: 满充总容量 ({0}Ah) 不能小于当前剩余容量 ({1}Ah)！", value, currentRem);
                    return false;
                }
            }
            else if (num == numSoc)
            {
                if (value < 0 || value > 100)
                {
                    message = "非法输入: SOC 百分比必须在 0% ~ 100% 之间！";
                    return false;
                }
            }
            else if (num == numSoh)
            {
                if (value < 0 || value > 100)
                {
                    message = "非法输入: SOH 健康度必须在 0% ~ 100% 之间！";
                    return false;
                }
            }
            else if (num == numVoltage)
            {
                if (value <= 0)
                {
                    message = "非法输入: 电池总电压必须大于 0V！";
                    return false;
                }
            }
            else if (num == numCvVolt)
            {
                if (value <= 10)
                {
                    message = "非法输入: CV 恒压充电点必须大于 10V！";
                    return false;
                }
            }
            else if (num == numCutoffVolt)
            {
                if (value <= 0)
                {
                    message = "非法输入: 放电截止点必须大于 0V！";
                    return false;
                }
                decimal currentCv = _committedValues.ContainsKey(numCvVolt) ? _committedValues[numCvVolt] : numCvVolt.Value;
                if (value >= currentCv)
                {
                    message = string.Format("非法输入: 放电截止点 ({0:F2}V) 不能大于或等于 CV 恒压点 ({1:F2}V)！", value, currentCv);
                    return false;
                }
            }
            else if (num == numCycleCount)
            {
                if (value < 0)
                {
                    message = "非法输入: 循环次数不能为负数！";
                    return false;
                }
            }

            if (_comm == null || _comm.CurrentWorkMode == WorkMode.SlaveSimulator)
            {
                int protIdx = (cboProtocol != null && cboProtocol.SelectedIndex >= 0) ? cboProtocol.SelectedIndex : 0;
                if (protIdx == 4) // SHENGYANG (uint16 0.01V / 0.01Ah, int16 0.01A)
                {
                    if (num == numCurrent && (value < -327.68M || value > 327.67M))
                    {
                        message = "非法输入: 圣阳协议电流寄存器 (0x1001, signed 0.01A) 有效范围为 -327.68A ~ +327.67A！";
                        return false;
                    }
                    if (num == numMaxChgI && (value < 0 || value > 327.67M))
                    {
                        message = "非法输入: 圣阳协议最大充电电流 (0x100C) 有效范围为 0 ~ 327.67A！";
                        return false;
                    }
                    if ((num == numRemCap || num == numFullCap) && value > 655.35M)
                    {
                        message = "非法输入: 圣阳协议容量寄存器 (0.01Ah, uint16) 最大支持 655.35Ah！";
                        return false;
                    }
                    if ((num == numVoltage || num == numCvVolt) && value > 655.35M)
                    {
                        message = "非法输入: 圣阳协议电压寄存器 (0.01V, uint16) 最大支持 655.35V！";
                        return false;
                    }
                }
                else if (protIdx == 5) // JBD (uint16 0.01V; Bit7=1 支持 0.1Ah 最高 6553.5Ah)
                {
                    if (num == numVoltage && value > 655.35M)
                    {
                        message = "非法输入: JBD 协议总压寄存器 (0.01V, uint16) 最大支持 655.35V！";
                        return false;
                    }
                    if ((num == numRemCap || num == numFullCap) && value > 6553.5M)
                    {
                        message = "非法输入: JBD 协议容量即使切换 FET Bit7=1 (0.1Ah) 最大仅支持 6553.5Ah！";
                        return false;
                    }
                }
            }

            return true;
        }

        private void CommitSingleControl(NumericUpDown num)
        {
            if (_comm != null && _comm.CurrentWorkMode == WorkMode.MasterPoller)
            {
                RollbackNumeric(num);
                return;
            }

            TextBox tb = GetInnerTextBox(num);
            if (tb != null)
            {
                decimal parsed;
                if (decimal.TryParse(tb.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) ||
                    decimal.TryParse(tb.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed))
                {
                    if (parsed < num.Minimum || parsed > num.Maximum)
                    {
                        FlashControlColor(num, Color.FromArgb(254, 202, 202));
                        RollbackNumeric(num);
                        lblStatusText.Text = string.Format("⛔ 非法输入: 数值 ({0}) 超出允许量程 [{1} ~ {2}]，已自动拦截回滚！", parsed, num.Minimum, num.Maximum);
                        return;
                    }

                    string validationMsg;
                    if (!ValidateNumeric(num, parsed, out validationMsg))
                    {
                        FlashControlColor(num, Color.FromArgb(254, 202, 202));
                        RollbackNumeric(num);
                        lblStatusText.Text = "⛔ " + validationMsg;
                        return;
                    }

                    _isUpdatingUi = true;
                    num.Value = parsed;
                    tb.Text = (num.DecimalPlaces > 0) ? parsed.ToString("F" + num.DecimalPlaces) : parsed.ToString("F0");
                    tb.SelectionLength = 0;
                    _isUpdatingUi = false;
                }
                else
                {
                    FlashControlColor(num, Color.FromArgb(254, 202, 202));
                    RollbackNumeric(num);
                    lblStatusText.Text = "⛔ 非法输入: 格式解析失败，已自动拦截回滚！";
                    return;
                }
            }
            else
            {
                string validationMsg;
                if (!ValidateNumeric(num, num.Value, out validationMsg))
                {
                    FlashControlColor(num, Color.FromArgb(254, 202, 202));
                    RollbackNumeric(num);
                    lblStatusText.Text = "⛔ " + validationMsg;
                    return;
                }
            }

            _committedValues[num] = num.Value;
            _isDirty[num] = false;

            ApplyCommittedValueToModel(num);

            FlashControlColor(num, Color.FromArgb(198, 246, 213));
            lblStatusText.Text = "✓ 数值已提交生效";
        }

        private void CommitAllControls()
        {
            NumericUpDown[] controls = new NumericUpDown[] {
                numVoltage, numCurrent, numTemp, numSoc, numSoh,
                numRemCap, numFullCap, numMaxChgI, numMaxDisI, numCvVolt, numCutoffVolt, numCycleCount
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
                else if (num == numMaxChgI)
                {
                    _model.MaxChargeCurrent = (double)num.Value;
                    if (_model.MaxChargeCurrent > 0)
                    {
                        _userConfiguredMaxChgI = _model.MaxChargeCurrent;
                        if (!_model.StatusChargeEnable)
                        {
                            _model.StatusChargeEnable = true;
                            _isUpdatingUi = true;
                            chkChgEn.Checked = true;
                            _isUpdatingUi = false;
                        }
                    }
                }
                else if (num == numMaxDisI)
                {
                    _model.MaxDischargeCurrent = (double)num.Value;
                    if (_model.MaxDischargeCurrent > 0)
                    {
                        _userConfiguredMaxDisI = _model.MaxDischargeCurrent;
                        if (!_model.StatusDischargeEnable)
                        {
                            _model.StatusDischargeEnable = true;
                            _isUpdatingUi = true;
                            chkDisEn.Checked = true;
                            _isUpdatingUi = false;
                        }
                    }
                }
                else if (num == numCvVolt) _model.CVVoltage = (double)num.Value;
                else if (num == numCutoffVolt) _model.CutoffVoltage = (double)num.Value;
                else if (num == numCycleCount) _model.CycleCount = (int)num.Value;
            }
        }

        private void FlashControlColor(Control ctrl, Color color, int intervalMs = 1200)
        {
            if (ctrl == null || ctrl.IsDisposed) return;

            Color defaultBg = (ctrl is NumericUpDown || ctrl is TextBox) ? SystemColors.Window : SystemColors.Control;

            ctrl.BackColor = color;
            if (ctrl is NumericUpDown)
            {
                TextBox tb = GetInnerTextBox((NumericUpDown)ctrl);
                if (tb != null) tb.BackColor = color;
            }

            if (!_flashTimers.ContainsKey(ctrl))
            {
                Timer tmr = new Timer { Interval = intervalMs };
                tmr.Tick += delegate(object s, EventArgs e) {
                    tmr.Stop();
                    if (!ctrl.IsDisposed)
                    {
                        ctrl.BackColor = defaultBg;
                        if (ctrl is NumericUpDown)
                        {
                            TextBox tb = GetInnerTextBox((NumericUpDown)ctrl);
                            if (tb != null) tb.BackColor = defaultBg;
                        }
                    }
                };
                _flashTimers[ctrl] = tmr;
            }

            _flashTimers[ctrl].Interval = intervalMs;
            _flashTimers[ctrl].Stop();
            _flashTimers[ctrl].Start();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_currentFocusedControl != null && _committedValues.ContainsKey(_currentFocusedControl))
            {
                if (keyData == Keys.Enter || keyData == (Keys.Control | Keys.S))
                {
                    CommitSingleControl(_currentFocusedControl);
                    return true;
                }
                if (keyData == Keys.Escape)
                {
                    RollbackNumeric(_currentFocusedControl);
                    CancelAndRollbackCurrentEdit();
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                if (_comm != null && _comm.CurrentWorkMode == WorkMode.MasterPoller)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
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
            mainFlags.RowCount = 4;
            mainFlags.ColumnCount = 1;
            mainFlags.AutoScroll = true;
            mainFlags.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
            mainFlags.RowStyles.Add(new RowStyle(SizeType.Absolute, 100f));
            mainFlags.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            mainFlags.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            // ── 0. 告警与保护一键复位栏 ──
            flpQuickPresets = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(2, 3, 2, 1),
                WrapContents = false
            };

            Button btnPresetClear = new Button
            {
                Text = "全清恢复 (清除所有告警与保护)",
                Width = 210,
                Height = 25,
                Margin = new Padding(2, 1, 4, 1)
            };
            btnPresetClear.Click += delegate(object s, EventArgs e) {
                lock (_model.SyncRoot)
                {
                    _model.ClearAllAlarmsAndProtections();
                    _model.MaxChargeCurrent = _userConfiguredMaxChgI > 0 ? _userConfiguredMaxChgI : 100.0;
                    _model.MaxDischargeCurrent = _userConfiguredMaxDisI > 0 ? _userConfiguredMaxDisI : 100.0;
                }
                SyncModelToUI();
                lblStatusText.Text = "已清除所有告警与保护跳闸标志，并恢复正常充放电限流";
            };

            flpQuickPresets.Controls.Add(btnPresetClear);
            mainFlags.Controls.Add(flpQuickPresets, 0, 0);

            // ── 1. 状态位 ──
            GroupBox gbStatus = new GroupBox();
            gbStatus.Text = "系统状态标志位";
            gbStatus.Dock = DockStyle.Fill;

            FlowLayoutPanel flpStatus = new FlowLayoutPanel();
            flpStatus.Dock = DockStyle.Fill;
            flpStatus.AutoScroll = true;

            Action refreshChargeDischargeLimits = delegate() {
                if (!_isUpdatingUi)
                {
                    lock (_model.SyncRoot)
                    {
                        bool canCharge = _model.CanCharge;
                        bool canDischarge = _model.CanDischarge;

                        _isUpdatingUi = true;
                        if (!canCharge)
                        {
                            if (_model.MaxChargeCurrent > 0) _userConfiguredMaxChgI = _model.MaxChargeCurrent;
                            _model.MaxChargeCurrent = 0;
                            UpdateNumericCommitted(numMaxChgI, 0M);
                        }
                        else if (_model.MaxChargeCurrent == 0)
                        {
                            _model.MaxChargeCurrent = _userConfiguredMaxChgI > 0 ? _userConfiguredMaxChgI : 100.0;
                            UpdateNumericCommitted(numMaxChgI, (decimal)_model.MaxChargeCurrent);
                        }

                        if (!canDischarge)
                        {
                            if (_model.MaxDischargeCurrent > 0) _userConfiguredMaxDisI = _model.MaxDischargeCurrent;
                            _model.MaxDischargeCurrent = 0;
                            UpdateNumericCommitted(numMaxDisI, 0M);
                        }
                        else if (_model.MaxDischargeCurrent == 0)
                        {
                            _model.MaxDischargeCurrent = _userConfiguredMaxDisI > 0 ? _userConfiguredMaxDisI : 100.0;
                            UpdateNumericCommitted(numMaxDisI, (decimal)_model.MaxDischargeCurrent);
                        }
                        _isUpdatingUi = false;
                    }
                }
            };

            chkChgEn = new CheckBox(); chkChgEn.Text = "充电使能"; chkChgEn.Checked = true; chkChgEn.AutoSize = true; chkChgEn.Margin = new Padding(6);
            chkChgEn.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi)
                {
                    lock (_model.SyncRoot) { _model.StatusChargeEnable = chkChgEn.Checked; }
                    refreshChargeDischargeLimits();
                }
            };

            chkDisEn = new CheckBox(); chkDisEn.Text = "放电使能"; chkDisEn.Checked = true; chkDisEn.AutoSize = true; chkDisEn.Margin = new Padding(6);
            chkDisEn.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi)
                {
                    lock (_model.SyncRoot) { _model.StatusDischargeEnable = chkDisEn.Checked; }
                    refreshChargeDischargeLimits();
                }
            };

            chkForceChg = new CheckBox(); chkForceChg.Text = "请求强制充电"; chkForceChg.AutoSize = true; chkForceChg.Margin = new Padding(6);
            chkForceChg.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi)
                {
                    lock (_model.SyncRoot) { _model.StatusForceCharge = chkForceChg.Checked; }
                }
            };

            chkFullChg = new CheckBox(); chkFullChg.Text = "请求满充"; chkFullChg.AutoSize = true; chkFullChg.Margin = new Padding(6);
            chkFullChg.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.StatusFullCharge = chkFullChg.Checked; }
                }
            };

            chkBalance = new CheckBox(); chkBalance.Text = "电芯均衡中"; chkBalance.AutoSize = true; chkBalance.Margin = new Padding(6);
            chkBalance.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.StatusBalancing = chkBalance.Checked; }
                }
            };

            chkSleep = new CheckBox(); chkSleep.Text = "休眠状态"; chkSleep.AutoSize = true; chkSleep.Margin = new Padding(6);
            chkSleep.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.StatusSleep = chkSleep.Checked; }
                }
            };

            flpStatus.Controls.AddRange(new Control[] { chkChgEn, chkDisEn, chkForceChg, chkFullChg, chkBalance, chkSleep });
            gbStatus.Controls.Add(flpStatus);
            mainFlags.Controls.Add(gbStatus, 0, 1);

            // ── 2. 警告位 ──
            GroupBox gbWarn = new GroupBox();
            gbWarn.Text = "警告状态";
            gbWarn.Dock = DockStyle.Fill;

            FlowLayoutPanel flpWarn = new FlowLayoutPanel();
            flpWarn.Dock = DockStyle.Fill;
            flpWarn.AutoScroll = true;

            chkWarnSingleOv = new CheckBox(); chkWarnSingleOv.Text = "单体过压警告"; chkWarnSingleOv.AutoSize = true; chkWarnSingleOv.Margin = new Padding(6);
            chkWarnSingleOv.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) { lock (_model.SyncRoot) { _model.WarnSingleOverVolt = chkWarnSingleOv.Checked; } }
            };

            chkWarnSingleUv = new CheckBox(); chkWarnSingleUv.Text = "单体欠压警告"; chkWarnSingleUv.AutoSize = true; chkWarnSingleUv.Margin = new Padding(6);
            chkWarnSingleUv.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) { lock (_model.SyncRoot) { _model.WarnSingleUnderVolt = chkWarnSingleUv.Checked; } }
            };

            chkWarnGlobalOv = new CheckBox(); chkWarnGlobalOv.Text = "组端过压警告"; chkWarnGlobalOv.AutoSize = true; chkWarnGlobalOv.Margin = new Padding(6);
            chkWarnGlobalOv.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) { lock (_model.SyncRoot) { _model.WarnGlobalOverVolt = chkWarnGlobalOv.Checked; } }
            };

            chkWarnGlobalUv = new CheckBox(); chkWarnGlobalUv.Text = "组端欠压警告"; chkWarnGlobalUv.AutoSize = true; chkWarnGlobalUv.Margin = new Padding(6);
            chkWarnGlobalUv.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) { lock (_model.SyncRoot) { _model.WarnGlobalUnderVolt = chkWarnGlobalUv.Checked; } }
            };

            chkWarnOc = new CheckBox(); chkWarnOc.Text = "充放电过流警告"; chkWarnOc.AutoSize = true; chkWarnOc.Margin = new Padding(6);
            chkWarnOc.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) { lock (_model.SyncRoot) { _model.WarnOverCurrent = chkWarnOc.Checked; } }
            };

            chkWarnHt = new CheckBox(); chkWarnHt.Text = "高温警告"; chkWarnHt.AutoSize = true; chkWarnHt.Margin = new Padding(6);
            chkWarnHt.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) { lock (_model.SyncRoot) { _model.WarnHighTemp = chkWarnHt.Checked; } }
            };

            chkWarnLt = new CheckBox(); chkWarnLt.Text = "低温警告"; chkWarnLt.AutoSize = true; chkWarnLt.Margin = new Padding(6);
            chkWarnLt.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) { lock (_model.SyncRoot) { _model.WarnLowTemp = chkWarnLt.Checked; } }
            };

            chkWarnVoltDiff = new CheckBox(); chkWarnVoltDiff.Text = "电芯压差过大"; chkWarnVoltDiff.AutoSize = true; chkWarnVoltDiff.Margin = new Padding(6);
            chkWarnVoltDiff.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.WarnVoltDiff = chkWarnVoltDiff.Checked; }
                }
            };

            chkWarnLowCap = new CheckBox(); chkWarnLowCap.Text = "电池低电量告警"; chkWarnLowCap.AutoSize = true; chkWarnLowCap.Margin = new Padding(6);
            chkWarnLowCap.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.WarnLowCapacity = chkWarnLowCap.Checked; }
                }
            };

            flpWarn.Controls.AddRange(new Control[] {
                chkWarnSingleOv, chkWarnSingleUv, chkWarnGlobalOv, chkWarnGlobalUv,
                chkWarnOc, chkWarnHt, chkWarnLt, chkWarnVoltDiff, chkWarnLowCap
            });
            gbWarn.Controls.Add(flpWarn);
            mainFlags.Controls.Add(gbWarn, 0, 2);

            // ── 3. 故障保护位 ──
            GroupBox gbProt = new GroupBox();
            gbProt.Text = "故障与保护跳闸";
            gbProt.Dock = DockStyle.Fill;

            FlowLayoutPanel flpProt = new FlowLayoutPanel();
            flpProt.Dock = DockStyle.Fill;
            flpProt.AutoScroll = true;

            chkProtOv = new CheckBox(); chkProtOv.Text = "总压/单体过压保护"; chkProtOv.AutoSize = true; chkProtOv.Margin = new Padding(6);
            chkProtOv.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.ProtOverVolt = chkProtOv.Checked; }
                    refreshChargeDischargeLimits();
                }
            };

            chkProtUv = new CheckBox(); chkProtUv.Text = "总压/单体欠压保护"; chkProtUv.AutoSize = true; chkProtUv.Margin = new Padding(6);
            chkProtUv.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.ProtUnderVolt = chkProtUv.Checked; }
                    refreshChargeDischargeLimits();
                }
            };

            chkProtOc = new CheckBox(); chkProtOc.Text = "过流保护"; chkProtOc.AutoSize = true; chkProtOc.Margin = new Padding(6);
            chkProtOc.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.ProtOverCurrent = chkProtOc.Checked; }
                    refreshChargeDischargeLimits();
                }
            };

            chkProtSc = new CheckBox(); chkProtSc.Text = "输出短路保护"; chkProtSc.AutoSize = true; chkProtSc.Margin = new Padding(6);
            chkProtSc.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.ProtShortCircuit = chkProtSc.Checked; }
                    refreshChargeDischargeLimits();
                }
            };

            chkProtHt = new CheckBox(); chkProtHt.Text = "电芯过温保护"; chkProtHt.AutoSize = true; chkProtHt.Margin = new Padding(6);
            chkProtHt.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.ProtHighTemp = chkProtHt.Checked; }
                    refreshChargeDischargeLimits();
                }
            };

            chkProtLt = new CheckBox(); chkProtLt.Text = "电芯低温保护"; chkProtLt.AutoSize = true; chkProtLt.Margin = new Padding(6);
            chkProtLt.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.ProtUnderTemp = chkProtLt.Checked; }
                    refreshChargeDischargeLimits();
                }
            };

            chkProtSys = new CheckBox(); chkProtSys.Text = "BMS系统故障"; chkProtSys.AutoSize = true; chkProtSys.Margin = new Padding(6);
            chkProtSys.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.ProtSystemFault = chkProtSys.Checked; }
                    refreshChargeDischargeLimits();
                }
            };

            chkProtSoftStart = new CheckBox(); chkProtSoftStart.Text = "软起动失败"; chkProtSoftStart.AutoSize = true; chkProtSoftStart.Margin = new Padding(6);
            chkProtSoftStart.CheckedChanged += delegate(object s, EventArgs e) {
                if (!_isUpdatingUi) {
                    lock (_model.SyncRoot) { _model.ProtSoftStart = chkProtSoftStart.Checked; }
                    refreshChargeDischargeLimits();
                }
            };

            flpProt.Controls.AddRange(new Control[] {
                chkProtOv, chkProtUv, chkProtOc, chkProtSc,
                chkProtHt, chkProtLt, chkProtSys, chkProtSoftStart
            });
            gbProt.Controls.Add(flpProt);
            mainFlags.Controls.Add(gbProt, 0, 3);

            CheckBox[] allFlagChks = new CheckBox[] {
                chkChgEn, chkDisEn, chkForceChg, chkFullChg, chkBalance, chkSleep,
                chkWarnSingleOv, chkWarnSingleUv, chkWarnGlobalOv, chkWarnGlobalUv,
                chkWarnOc, chkWarnHt, chkWarnLt, chkWarnVoltDiff, chkWarnLowCap,
                chkProtOv, chkProtUv, chkProtOc, chkProtSc, chkProtHt, chkProtLt, chkProtSys, chkProtSoftStart
            };
            for (int i = 0; i < allFlagChks.Length; i++)
            {
                if (allFlagChks[i] != null)
                {
                    allFlagChks[i].CheckedChanged += delegate(object s, EventArgs e) {
                        if (!_isUpdatingUi) UpdateAlarmProtectionVisualStyles();
                    };
                }
            }

            parent.Controls.Add(mainFlags);
        }

        // ══════════════════════════════════════════════════════════
        // 5. 报文监视器与日志面板构建 (实时监视 & 告警/保护摘要颜色高亮)
        // ══════════════════════════════════════════════════════════
        private void BuildMonitorPanel(Panel parent)
        {
            GroupBox gbMonitor = new GroupBox();
            gbMonitor.Text = "实时监视 (双击报文行可查看详情)";
            gbMonitor.Dock = DockStyle.Fill;
            gbMonitor.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);

            TableLayoutPanel pnlMon = new TableLayoutPanel();
            pnlMon.Dock = DockStyle.Fill;
            pnlMon.RowCount = 3;
            pnlMon.ColumnCount = 1;
            pnlMon.Padding = new Padding(4, 4, 4, 4);
            pnlMon.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            pnlMon.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            pnlMon.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));

            // 统计栏 + 过滤栏
            FlowLayoutPanel flpStats = new FlowLayoutPanel();
            flpStats.Dock = DockStyle.Fill;
            flpStats.WrapContents = false;
            flpStats.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);

            lblRxCount = new Label(); lblRxCount.Text = "RX: 0"; lblRxCount.AutoSize = true; lblRxCount.Margin = new Padding(2, 4, 6, 0);
            lblTxCount = new Label(); lblTxCount.Text = "TX: 0"; lblTxCount.AutoSize = true; lblTxCount.Margin = new Padding(2, 4, 6, 0);
            lblErrCount = new Label(); lblErrCount.Text = "错误: 0"; lblErrCount.AutoSize = true; lblErrCount.Margin = new Padding(2, 4, 6, 0);
            lblLatency = new Label(); lblLatency.Text = "时延: - ms"; lblLatency.AutoSize = true; lblLatency.Margin = new Padding(2, 4, 8, 0);

            chkFilterAlarmOnly = new CheckBox
            {
                Text = "仅看告警",
                AutoSize = true,
                Margin = new Padding(4, 3, 4, 0)
            };

            Label lblFilterKw = new Label
            {
                Text = "过滤:",
                AutoSize = true,
                Margin = new Padding(2, 4, 0, 0),
                ForeColor = Color.DimGray
            };
            txtLogFilter = new TextBox
            {
                Width = 90,
                Margin = new Padding(2, 1, 2, 0),
                Font = new Font("Microsoft YaHei UI", 8f)
            };

            flpStats.Controls.AddRange(new Control[] {
                lblRxCount, lblTxCount, lblErrCount, lblLatency, chkFilterAlarmOnly, lblFilterKw, txtLogFilter
            });
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
            rtbLog.MouseDoubleClick += RtbLog_MouseDoubleClick;
            pnlMon.Controls.Add(rtbLog, 0, 1);

            // 底部操作栏
            FlowLayoutPanel flpLogTools = new FlowLayoutPanel();
            flpLogTools.Dock = DockStyle.Fill;
            flpLogTools.WrapContents = false;
            flpLogTools.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            flpLogTools.Padding = new Padding(2, 3, 2, 2);

            chkAutoScroll = new CheckBox(); chkAutoScroll.Text = "自动滚屏"; chkAutoScroll.Checked = true; chkAutoScroll.AutoSize = true; chkAutoScroll.Margin = new Padding(2, 5, 6, 0);
            chkShowTime = new CheckBox(); chkShowTime.Text = "显示时间"; chkShowTime.Checked = true; chkShowTime.AutoSize = true; chkShowTime.Margin = new Padding(2, 5, 6, 0);
            chkShowHex = new CheckBox(); chkShowHex.Text = "显示HEX"; chkShowHex.Checked = true; chkShowHex.AutoSize = true; chkShowHex.Margin = new Padding(2, 5, 6, 0);

            btnClearLog = new Button(); btnClearLog.Text = "清空"; btnClearLog.Width = 54; btnClearLog.Height = 25; btnClearLog.Margin = new Padding(2, 1, 4, 0);
            btnClearLog.Click += delegate(object s, EventArgs e) {
                rtbLog.Clear();
                _comm.ResetStatistics();
                UpdateStatsLabels();
            };

            btnExportLog = new Button(); btnExportLog.Text = "导出日志"; btnExportLog.Width = 70; btnExportLog.Height = 25; btnExportLog.Margin = new Padding(2, 1, 4, 0);
            btnExportLog.Click += BtnExportLog_Click;

            flpLogTools.Controls.AddRange(new Control[] {
                chkAutoScroll, chkShowTime, chkShowHex, btnClearLog, btnExportLog
            });
            pnlMon.Controls.Add(flpLogTools, 0, 2);

            gbMonitor.Controls.Add(pnlMon);
            parent.Controls.Add(gbMonitor);
        }

        // ══════════════════════════════════════════════════════════
        // 6. UI 事件处理与数据同步
        // ══════════════════════════════════════════════════════════
        private void RefreshPorts()
        {
            RefreshPorts(true);
        }

        private void RefreshPorts(bool preserveSelection)
        {
            if (_comm != null && _comm.IsRunning) return;

            string prevSelected = (preserveSelection && cboPort.SelectedItem != null)
                ? cboPort.SelectedItem.ToString()
                : null;
            if (string.Equals(prevSelected, "None", StringComparison.OrdinalIgnoreCase))
                prevSelected = null;

            cboPort.Items.Clear();
            string[] ports = SerialPort.GetPortNames();
            if (ports != null && ports.Length > 0)
            {
                Array.Sort(ports, delegate(string a, string b)
                {
                    int na = 0, nb = 0;
                    bool pa = a != null && a.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(a.Substring(3), out na);
                    bool pb = b != null && b.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(b.Substring(3), out nb);
                    if (pa && pb) return na.CompareTo(nb);
                    return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                });
                cboPort.Items.AddRange(ports);

                int matchIdx = 0;
                if (!string.IsNullOrEmpty(prevSelected))
                {
                    for (int i = 0; i < cboPort.Items.Count; i++)
                    {
                        if (string.Equals(cboPort.Items[i].ToString(), prevSelected, StringComparison.OrdinalIgnoreCase))
                        {
                            matchIdx = i;
                            break;
                        }
                    }
                }
                cboPort.SelectedIndex = matchIdx;
            }
            else
            {
                cboPort.Items.Add("None");
                cboPort.SelectedIndex = 0;
            }
        }

        private void UpdateProtocolBadge()
        {
            if (_comm == null || lblProtocolBadge == null) return;
            string modeStr = _comm.CurrentWorkMode == WorkMode.MasterPoller ? "[主机轮询模式]" : "[从机模拟模式]";
            lblProtocolBadge.Text = string.Format("协议: {0} {1}", _comm.ProtocolHandler.ProtocolName, modeStr);
        }

        private void ClearMasterTelemetryDisplay()
        {
            _isUpdatingUi = true;
            lock (_model.SyncRoot)
            {
                _model.ClearAllAlarmsAndProtections();
                _model.StatusChargeEnable = false;
                _model.StatusDischargeEnable = false;
                _model.StatusForceCharge = false;
                _model.StatusFullCharge = false;
                _model.StatusBalancing = false;
                _model.StatusSleep = false;
            }

            NumericUpDown[] allNums = new NumericUpDown[] {
                numVoltage, numCurrent, numTemp, numSoc, numSoh,
                numRemCap, numFullCap, numMaxChgI, numMaxDisI, numCvVolt, numCutoffVolt, numCycleCount
            };
            for (int i = 0; i < allNums.Length; i++)
            {
                if (allNums[i] != null)
                {
                    TextBox tb = GetInnerTextBox(allNums[i]);
                    if (tb != null)
                    {
                        tb.Text = "";
                    }
                    _isDirty[allNums[i]] = false;
                }
            }

            CheckBox[] allChks = new CheckBox[] {
                chkChgEn, chkDisEn, chkForceChg, chkFullChg, chkBalance, chkSleep,
                chkWarnSingleOv, chkWarnSingleUv, chkWarnGlobalOv, chkWarnGlobalUv,
                chkWarnOc, chkWarnHt, chkWarnLt, chkWarnVoltDiff, chkWarnLowCap,
                chkProtOv, chkProtUv, chkProtOc, chkProtSc, chkProtHt, chkProtLt, chkProtSys, chkProtSoftStart
            };
            for (int i = 0; i < allChks.Length; i++)
            {
                if (allChks[i] != null)
                {
                    allChks[i].Checked = false;
                }
            }

            UpdateAlarmProtectionVisualStyles();
            _isUpdatingUi = false;
        }

        private void ApplyFlagVisualStyle(CheckBox[] boxes, Color activeColor)
        {
            if (boxes == null) return;
            Font regFont = _flagFontRegular ?? this.Font;
            Font boldFont = _flagFontBold ?? this.Font;
            for (int i = 0; i < boxes.Length; i++)
            {
                CheckBox chk = boxes[i];
                if (chk == null) continue;
                bool active = chk.Checked;
                Color targetColor = active ? activeColor : SystemColors.ControlText;
                Font targetFont = active ? boldFont : regFont;
                if (chk.ForeColor != targetColor) chk.ForeColor = targetColor;
                if (!object.ReferenceEquals(chk.Font, targetFont)) chk.Font = targetFont;
            }
        }

        private void UpdateAlarmProtectionVisualStyles()
        {
            ApplyFlagVisualStyle(new CheckBox[] {
                chkChgEn, chkDisEn, chkForceChg, chkFullChg, chkBalance, chkSleep
            }, Color.FromArgb(21, 128, 61));

            ApplyFlagVisualStyle(new CheckBox[] {
                chkWarnSingleOv, chkWarnSingleUv, chkWarnGlobalOv, chkWarnGlobalUv,
                chkWarnOc, chkWarnHt, chkWarnLt, chkWarnVoltDiff, chkWarnLowCap
            }, Color.FromArgb(180, 83, 9));

            ApplyFlagVisualStyle(new CheckBox[] {
                chkProtOv, chkProtUv, chkProtOc, chkProtSc, chkProtHt, chkProtLt, chkProtSys, chkProtSoftStart
            }, Color.FromArgb(185, 28, 28));
        }

        // 根据当前选定的协议直接置灰当前协议不支持的模拟量与标志位控件 (仅在从机模拟模式下禁用，主机模式下保持启用只读回显)
        private void UpdateProtocolControlStates()
        {
            bool isMaster = (_comm != null && _comm.CurrentWorkMode == WorkMode.MasterPoller);
            int idx = (cboProtocol != null && cboProtocol.SelectedIndex >= 0) ? cboProtocol.SelectedIndex : 0;
            // idx: 0=CVTE, 1=GROWATT, 2=VOLTRONIC, 3=PYLONTECH, 4=SHENGYANG, 5=JBD

            if (chkProtSoftStart != null)
            {
                chkProtSoftStart.Text = (idx == 5) ? "软件锁MOS(Bit12)" : "软起动失败";
            }

            if (isMaster)
            {
                NumericUpDown[] allNums = new NumericUpDown[] {
                    numVoltage, numCurrent, numTemp, numSoc, numSoh,
                    numRemCap, numFullCap, numMaxChgI, numMaxDisI, numCvVolt, numCutoffVolt, numCycleCount
                };
                for (int i = 0; i < allNums.Length; i++)
                {
                    if (allNums[i] != null)
                    {
                        allNums[i].Enabled = true;
                        allNums[i].ReadOnly = true;
                        if (allNums[i].Controls.Count > 0) allNums[i].Controls[0].Enabled = false;
                    }
                }

                CheckBox[] allChks = new CheckBox[] {
                    chkChgEn, chkDisEn, chkForceChg, chkFullChg, chkBalance, chkSleep,
                    chkWarnSingleOv, chkWarnSingleUv, chkWarnGlobalOv, chkWarnGlobalUv,
                    chkWarnOc, chkWarnHt, chkWarnLt, chkWarnVoltDiff, chkWarnLowCap,
                    chkProtOv, chkProtUv, chkProtOc, chkProtSc, chkProtHt, chkProtLt, chkProtSys, chkProtSoftStart
                };
                for (int i = 0; i < allChks.Length; i++)
                {
                    if (allChks[i] != null)
                    {
                        allChks[i].Enabled = true;
                        allChks[i].AutoCheck = false;
                    }
                }
                return;
            }

            // 从机模拟模式：先恢复可编辑与自动勾选，再按协议寄存器支持矩阵精确控制 Enabled
            NumericUpDown[] slaveNums = new NumericUpDown[] {
                numVoltage, numCurrent, numTemp, numSoc, numSoh,
                numRemCap, numFullCap, numMaxChgI, numMaxDisI, numCvVolt, numCutoffVolt, numCycleCount
            };
            for (int i = 0; i < slaveNums.Length; i++)
            {
                if (slaveNums[i] != null)
                {
                    slaveNums[i].ReadOnly = false;
                    if (slaveNums[i].Controls.Count > 0) slaveNums[i].Controls[0].Enabled = true;
                }
            }

            CheckBox[] slaveChks = new CheckBox[] {
                chkChgEn, chkDisEn, chkForceChg, chkFullChg, chkBalance, chkSleep,
                chkWarnSingleOv, chkWarnSingleUv, chkWarnGlobalOv, chkWarnGlobalUv,
                chkWarnOc, chkWarnHt, chkWarnLt, chkWarnVoltDiff, chkWarnLowCap,
                chkProtOv, chkProtUv, chkProtOc, chkProtSc, chkProtHt, chkProtLt, chkProtSys, chkProtSoftStart
            };
            for (int i = 0; i < slaveChks.Length; i++)
            {
                if (slaveChks[i] != null)
                {
                    slaveChks[i].AutoCheck = true;
                    slaveChks[i].Enabled = true;
                }
            }

            if (numVoltage != null) numVoltage.Enabled = true;
            if (numCurrent != null) numCurrent.Enabled = true;
            if (numSoc != null) numSoc.Enabled = true;
            if (numCvVolt != null) numCvVolt.Enabled = (idx != 5);
            if (numFullCap != null) numFullCap.Enabled = true;
            if (numMaxChgI != null) numMaxChgI.Enabled = (idx != 5);
            if (numTemp != null) numTemp.Enabled = true;

            if (numCutoffVolt != null) numCutoffVolt.Enabled = (idx == 2 || idx == 3);
            if (numMaxDisI != null) numMaxDisI.Enabled = (idx == 1 || idx == 2 || idx == 3);
            if (numCycleCount != null) numCycleCount.Enabled = (idx != 2);
            if (numRemCap != null) numRemCap.Enabled = (idx != 2);
            if (numSoh != null) numSoh.Enabled = (idx == 0 || idx == 1 || idx == 3 || idx == 4);

            if (chkForceChg != null) chkForceChg.Enabled = (idx == 0 || idx == 1 || idx == 2 || idx == 3);
            if (chkFullChg != null) chkFullChg.Enabled = (idx == 2 || idx == 3);
            if (chkBalance != null) chkBalance.Enabled = (idx == 0 || idx == 1 || idx == 5);
            if (chkSleep != null) chkSleep.Enabled = (idx == 0 || idx == 1);
            if (chkWarnVoltDiff != null) chkWarnVoltDiff.Enabled = (idx == 0 || idx == 1 || idx == 3 || idx == 5);
            if (chkWarnLowCap != null) chkWarnLowCap.Enabled = (idx == 0 || idx == 1 || idx == 4 || idx == 5);
            if (chkProtSoftStart != null) chkProtSoftStart.Enabled = (idx == 0 || idx == 1 || idx == 5);
        }

        private void UpdateWorkModeUI()
        {
            bool isMaster = (cboWorkMode.SelectedIndex == 1);
            bool wasMaster = (_comm.CurrentWorkMode == WorkMode.MasterPoller);
            _comm.CurrentWorkMode = isMaster ? WorkMode.MasterPoller : WorkMode.SlaveSimulator;
            _comm.TargetSlaveId = (byte)numSpecificId.Value;
            _comm.SpecificId = (int)numSpecificId.Value;
            if (wasMaster != isMaster)
            {
                _comm.NotifyContextReset();
            }

            if (isMaster)
            {
                chkReplyAllIds.Visible = false;
                if (chkSimSilence != null) chkSimSilence.Visible = false;
                if (chkSimCrcErr != null) chkSimCrcErr.Visible = false;
                if (lblSimDelay != null) lblSimDelay.Visible = false;
                if (cboSimDelay != null) cboSimDelay.Visible = false;
                if (lblCellDelta != null) lblCellDelta.Visible = false;
                if (cboCellDelta != null) cboCellDelta.Visible = false;
                if (lblFaultInject != null) lblFaultInject.Visible = false;
                if (cboFaultInject != null) cboFaultInject.Visible = false;

                lblIdTag.Text = "目标ID:";
                numSpecificId.Enabled = true;

                btnScanPacks.Visible = true;
                if (lblPollInterval != null) lblPollInterval.Visible = true;
                cboPollInterval.Visible = true;
                btnPollPause.Visible = true;
                if (btnSinglePoll != null) btnSinglePoll.Visible = true;

                if (btnProfile3KU != null) btnProfile3KU.Visible = false;
                if (btnProfile6KU != null) btnProfile6KU.Visible = false;
                if (lblCellCount != null) lblCellCount.Visible = false;
                if (cboCellCount != null) cboCellCount.Visible = false;
                if (chkAutoAdapt != null) chkAutoAdapt.Visible = false;
                if (flpQuickPresets != null) flpQuickPresets.Enabled = false;
                if (lblHint != null)
                {
                    lblHint.Text = "实时监控模式：接收外部电池遥测数据 (只读回显)";
                    lblHint.ForeColor = Color.DimGray;
                }

                _hasMasterTelemetryReceived = false;
                ClearMasterTelemetryDisplay();

                if (_comm != null && _comm.IsOpen)
                {
                    lblStatusText.Text = string.Format("主机轮询模式：正在采集目标从机 ID:{0} 遥测数据", _comm.TargetSlaveId);
                }
                else
                {
                    lblStatusText.Text = "已进入 [主机轮询模式] (主动向从机电池下发轮询指令)";
                }
            }
            else
            {
                chkReplyAllIds.Visible = true;
                if (chkSimSilence != null) chkSimSilence.Visible = true;
                if (chkSimCrcErr != null) chkSimCrcErr.Visible = true;
                if (lblSimDelay != null) lblSimDelay.Visible = true;
                if (cboSimDelay != null) cboSimDelay.Visible = true;
                if (lblCellDelta != null) lblCellDelta.Visible = true;
                if (cboCellDelta != null) cboCellDelta.Visible = true;
                if (lblFaultInject != null) lblFaultInject.Visible = true;
                if (cboFaultInject != null) cboFaultInject.Visible = true;

                lblIdTag.Text = "从机ID:";
                numSpecificId.Enabled = !chkReplyAllIds.Checked;

                btnScanPacks.Visible = false;
                if (lblPollInterval != null) lblPollInterval.Visible = false;
                cboPollInterval.Visible = false;
                btnPollPause.Visible = false;
                if (btnSinglePoll != null) btnSinglePoll.Visible = false;

                if (btnProfile3KU != null) btnProfile3KU.Visible = true;
                if (btnProfile6KU != null) btnProfile6KU.Visible = true;
                if (lblCellCount != null) lblCellCount.Visible = true;
                if (cboCellCount != null) cboCellCount.Visible = true;
                if (chkAutoAdapt != null) chkAutoAdapt.Visible = true;
                if (flpQuickPresets != null) flpQuickPresets.Enabled = true;
                if (lblHint != null)
                {
                    lblHint.Text = "按 Enter 或失焦自动生效";
                    lblHint.ForeColor = Color.DimGray;
                }

                if (wasMaster)
                {
                    int preservedCells;
                    lock (_model.SyncRoot)
                    {
                        preservedCells = _model.GetEffectiveCellCount();
                    }
                    _model.ApplyProfile(_model.ActiveProfileName);
                    lock (_model.SyncRoot)
                    {
                        _model.CellCount = preservedCells;
                    }
                    SyncModelToUI();
                }

                if (_comm != null && _comm.IsOpen)
                {
                    lblStatusText.Text = "从机模拟模式：正在监听总线，等待外部逆变器查询...";
                }
                else
                {
                    lblStatusText.Text = "已进入 [从机模拟模式] (作为BMS电池包响应逆变器)";
                }
            }

            UpdateProtocolControlStates();
            UpdateProtocolBadge();
        }

        private void Comm_OnMasterTelemetryUpdated(object sender, BmsDataModel model)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(new Action(delegate()
            {
                if (this.IsDisposed) return;

                _hasMasterTelemetryReceived = true;
                SyncModelToUI();

                lblStatusText.Text = string.Format("✓ [主机遥测回显] 目标从机ID:{0} 遥测已更新 (时延: {1}ms | {2:HH:mm:ss})",
                    _comm.TargetSlaveId, _comm.LastResponseTimeMs, DateTime.Now);
            }));
        }

        private void Comm_OnScanPacksCompleted(object sender, List<byte> foundPacks)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(new Action(delegate()
            {
                if (this.IsDisposed) return;
                btnScanPacks.Enabled = true;
                if (_comm != null && _comm.WasScanAbortedByDisconnect)
                {
                    return;
                }
                if (_comm != null && _comm.WasScanCancelled)
                {
                    lblStatusText.Text = "扫描已由用户手动取消";
                    return;
                }

                if (foundPacks != null && foundPacks.Count > 0)
                {
                    if (_comm.TargetSlaveId != foundPacks[0])
                    {
                        _hasMasterTelemetryReceived = false;
                        ClearMasterTelemetryDisplay();
                    }
                    _isUpdatingUi = true;
                    numSpecificId.Value = foundPacks[0];
                    _isUpdatingUi = false;
                    _comm.TargetSlaveId = foundPacks[0];
                    MessageBox.Show(string.Format("总线扫描完成！\n\n共探测到 {0} 个在线电池包:\nID: {1}\n\n已自动将当前目标从机 ID 切换为: {2}",
                        foundPacks.Count, string.Join(", ", foundPacks.ConvertAll<string>(delegate(byte b) { return b.ToString(); }).ToArray()), foundPacks[0]),
                        "在线从机探测成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("总线扫描完成，未探测到任何在线响应的电池包。\n请检查通信接线 (A/B) 与电池包供电状态！", "未发现从机", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }));
        }

        private void ApplyProtocolHandlerByIndex(int idx)
        {
            switch (idx)
            {
                case 0:
                    _comm.ProtocolHandler = new CvteModbusHandler();
                    break;
                case 1:
                    _comm.ProtocolHandler = new GrowattModbusHandler();
                    break;
                case 2:
                    _comm.ProtocolHandler = new VoltronicModbusHandler();
                    break;
                case 3:
                    _comm.ProtocolHandler = new PylontechAsciiHandler();
                    break;
                case 4:
                    _comm.ProtocolHandler = new LibModbusHandler();
                    break;
                case 5:
                    _comm.ProtocolHandler = new JbdBinaryHandler();
                    break;
            }
        }

        private void ApplyLoadedStartupConfig()
        {
            SimulatorDefaultsConfig cfg = SimulatorDefaultsConfig.Load(SimulatorDefaultsConfig.DefaultFilePath);

            _isUpdatingUi = true;
            try
            {
                string targetBaud = cfg.BaudRate.ToString();
                for (int i = 0; i < cboBaud.Items.Count; i++)
                {
                    if (string.Equals(cboBaud.Items[i].ToString(), targetBaud, StringComparison.Ordinal))
                    {
                        cboBaud.SelectedIndex = i;
                        break;
                    }
                }

                int protIdx = SimulatorDefaultsConfig.IndexFromProtocolName(cfg.Protocol);
                if (protIdx >= 0 && protIdx < cboProtocol.Items.Count)
                {
                    cboProtocol.SelectedIndex = protIdx;
                    ApplyProtocolHandlerByIndex(protIdx);
                }

                _model.ApplyProfile(cfg.Profile);
                lock (_model.SyncRoot)
                {
                    _model.CellCount = cfg.CellCount;
                }

                chkAutoAdapt.Checked = cfg.AutoAdapt;
                _comm.AutoAdaptEnabled = cfg.AutoAdapt;

                chkReplyAllIds.Checked = cfg.ReplyAllIds;
                _comm.ReplyAllIds = cfg.ReplyAllIds;

                int clampedId = Math.Max(0, Math.Min(15, cfg.SpecificId));
                numSpecificId.Value = clampedId;
                numSpecificId.Enabled = !cfg.ReplyAllIds;
                _comm.SpecificId = clampedId;
                _comm.TargetSlaveId = (byte)clampedId;
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void SavePersistentConfig()
        {
            if (!_isConfigInitialized) return;
            SimulatorDefaultsConfig cfg = new SimulatorDefaultsConfig();
            int protIdx = (cboProtocol != null && cboProtocol.SelectedIndex >= 0) ? cboProtocol.SelectedIndex : 0;
            cfg.Protocol = SimulatorDefaultsConfig.ProtocolNameFromIndex(protIdx);

            int baud = 9600;
            if (cboBaud != null && cboBaud.SelectedItem != null)
            {
                int parsedBaud;
                if (int.TryParse(cboBaud.SelectedItem.ToString(), out parsedBaud)) baud = parsedBaud;
            }
            cfg.BaudRate = baud;
            cfg.AutoAdapt = (chkAutoAdapt != null && chkAutoAdapt.Checked);
            cfg.ReplyAllIds = (chkReplyAllIds == null || chkReplyAllIds.Checked);
            cfg.SpecificId = (numSpecificId != null) ? (int)numSpecificId.Value : 1;

            lock (_model.SyncRoot)
            {
                cfg.Profile = string.Equals(_model.ActiveProfileName, "3KU", StringComparison.OrdinalIgnoreCase) ? "3KU" : "6KU";
                cfg.CellCount = _model.GetEffectiveCellCount();
            }

            SimulatorDefaultsConfig.Save(SimulatorDefaultsConfig.DefaultFilePath, cfg);
        }

        private void CboProtocol_SelectedIndexChanged(object sender, EventArgs e)
        {
            int idx = cboProtocol.SelectedIndex;
            if (!_isUpdatingUi)
            {
                ApplyProtocolHandlerByIndex(idx);
                if (_comm.CurrentWorkMode == WorkMode.MasterPoller)
                {
                    _hasMasterTelemetryReceived = false;
                    ClearMasterTelemetryDisplay();
                }
                _comm.NotifyContextReset();
            }

            UpdateProtocolControlStates();
            UpdateProtocolBadge();

            if (!_isUpdatingUi)
            {
                if (_comm.CurrentWorkMode == WorkMode.SlaveSimulator)
                {
                    if (idx == 5)
                    {
                        lblStatusText.Text = "JBD 模式：0x03/0x04/0x05 不传输 CV/截止电压/限流（已置灰），软件锁MOS 对应 Bit12";
                    }
                    else if (idx == 4)
                    {
                        bool outOfRange = false;
                        lock (_model.SyncRoot)
                        {
                            outOfRange = (_model.Current < -327.68 || _model.Current > 327.67 ||
                                          _model.MaxChargeCurrent < 0 || _model.MaxChargeCurrent > 327.67 ||
                                          _model.RemainingCapacity > 655.35 || _model.FullCapacity > 655.35 ||
                                          _model.Voltage > 655.35 || _model.CVVoltage > 655.35);
                        }
                        if (outOfRange)
                        {
                            lblStatusText.Text = "警告: 当前模型参数超出圣阳协议范围 (电流±327.67A / 容量≤655.35Ah / 电压≤655.35V)，将按边界限幅编码";
                        }
                    }
                }
                SavePersistentConfig();
            }
        }

        private void SetConnectionUiState(bool connected, string statusMsg)
        {
            btnConnect.Text = connected ? "关闭串口" : "打开串口";
            lblPortStatus.Text = connected ? "● 运行中" : "未连接";
            lblPortStatus.ForeColor = connected ? Color.Green : Color.Gray;
            cboPort.Enabled = !connected;
            cboBaud.Enabled = !connected;
            if (btnRefresh != null) btnRefresh.Enabled = !connected;
            if (!string.IsNullOrEmpty(statusMsg))
            {
                lblStatusText.Text = statusMsg;
            }
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            if (_comm.IsRunning || btnConnect.Text == "关闭串口")
            {
                _comm.Close();
                RefreshPorts(true);
                SetConnectionUiState(false, _comm.CurrentWorkMode == WorkMode.MasterPoller
                    ? "串口已关闭，主机轮询已停止"
                    : "串口已关闭，从机模拟已停止");
            }
            else
            {
                if (cboPort.SelectedItem == null || cboPort.SelectedItem.ToString().Contains("无") || cboPort.SelectedItem.ToString().Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("请先插入 USB-485 转换器并选择有效串口！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string portName = cboPort.SelectedItem.ToString();
                int baud = 9600;
                if (cboBaud.SelectedItem == null || !int.TryParse(cboBaud.SelectedItem.ToString(), out baud))
                {
                    baud = 9600;
                }

                if (_comm.Open(portName, baud))
                {
                    string runningMsg = _comm.CurrentWorkMode == WorkMode.MasterPoller
                        ? string.Format("✓ 串口已打开 [{0}: {1}]，主机轮询模式运行中，正在采集从机 ID:{2} 遥测数据...", portName, baud, _comm.TargetSlaveId)
                        : string.Format("✓ 串口已打开 [{0}: {1}]，正在监听总线，等待外部逆变器查询...", portName, baud);
                    SetConnectionUiState(true, runningMsg);
                }
                else
                {
                    RefreshPorts(true);
                    SetConnectionUiState(false, null);
                }
            }
        }

        private void Comm_OnPortDisconnected(object sender, PortDisconnectedEventArgs e)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(new Action(delegate()
            {
                if (this.IsDisposed || _comm.IsRunning) return;
                RefreshPorts(true);
                SetConnectionUiState(false, "⚠️ " + e.Reason + "（已自动关闭串口）");
                AppendSystemNoticeToLog("⚠️ [链路断开] " + e.Reason + "，已自动关闭串口并恢复就绪状态。");
            }));
        }

        private void AppendSystemNoticeToLog(string notice)
        {
            if (rtbLog == null || rtbLog.IsDisposed || !rtbLog.IsHandleCreated) return;
            string line = string.Format("[{0:HH:mm:ss}] SYS: {1}{2}", DateTime.Now, notice, Environment.NewLine);
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = Color.FromArgb(248, 113, 113);
            rtbLog.AppendText(line);
            if (chkAutoScroll != null && chkAutoScroll.Checked)
            {
                rtbLog.ScrollToCaret();
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_DEVICECHANGE && _deviceChangeTimer != null)
            {
                int wp = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (wp == DBT_DEVNODES_CHANGED || wp == DBT_DEVICEARRIVAL || wp == DBT_DEVICEREMOVECOMPLETE)
                {
                    _deviceChangeTimer.Stop();
                    _deviceChangeTimer.Start();
                }
            }
        }

        private void DeviceChangeTimer_Tick(object sender, EventArgs e)
        {
            if (_deviceChangeTimer != null) _deviceChangeTimer.Stop();
            if (this.IsDisposed) return;

            if (_comm != null && _comm.IsRunning)
            {
                string activePort = _comm.CurrentPortName;
                bool stillPresent = false;
                if (!string.IsNullOrEmpty(activePort))
                {
                    string[] currentPorts = SerialPort.GetPortNames();
                    for (int i = 0; i < currentPorts.Length; i++)
                    {
                        if (string.Equals(currentPorts[i], activePort, StringComparison.OrdinalIgnoreCase))
                        {
                            stillPresent = true;
                            break;
                        }
                    }
                }
                if (!stillPresent || !_comm.IsPhysicalPortOpen)
                {
                    _comm.NotifyExternalPortLost(string.Format("串口 {0} 已拔出或发生变化", activePort ?? ""));
                }
            }
            else
            {
                RefreshPorts(true);
            }
        }

        private void Comm_OnAutoAdapted(object sender, AutoAdaptEventArgs e)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(new Action(delegate()
            {
                if (this.IsDisposed || _comm.CurrentWorkMode != WorkMode.SlaveSimulator) return;

                string targetProtName = e.Protocol == DetectedProtocolType.LibModbus ? "SHENGYANG" : (e.Protocol == DetectedProtocolType.Jbd ? "JBD" : e.Protocol.ToString().ToUpper());
                for (int i = 0; i < cboProtocol.Items.Count; i++)
                {
                    if (cboProtocol.Items[i].ToString().Equals(targetProtName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (cboProtocol.SelectedIndex != i)
                        {
                            _isUpdatingUi = true;
                            cboProtocol.SelectedIndex = i;
                            _isUpdatingUi = false;
                        }
                        break;
                    }
                }

                if (e.Model != InverterModel.Unknown)
                {
                    SyncModelToUI();
                    CommitAllControls();
                }

                UpdateProtocolControlStates();
                UpdateProtocolBadge();
                lblStatusText.Text = string.Format("自适应生效：已匹配为 [{0}] {1}", e.Model != InverterModel.Unknown ? (e.Model == InverterModel.INV_3KU ? "24V (3KU)" : "48V (6KU)") : "保持当前配置", targetProtName);
                SavePersistentConfig();
            }));
        }

        private void SyncModelToUI()
        {
            _isUpdatingUi = true;
            lock (_model.SyncRoot)
            {
                if (_model.MaxChargeCurrent > 0) _userConfiguredMaxChgI = _model.MaxChargeCurrent;
                if (_model.MaxDischargeCurrent > 0) _userConfiguredMaxDisI = _model.MaxDischargeCurrent;

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
                UpdateNumericCommitted(numCutoffVolt, (decimal)_model.CutoffVoltage);
                UpdateNumericCommitted(numCycleCount, (decimal)_model.CycleCount);

                chkChgEn.Checked = _model.StatusChargeEnable;
                chkDisEn.Checked = _model.StatusDischargeEnable;
                chkForceChg.Checked = _model.StatusForceCharge;
                chkFullChg.Checked = _model.StatusFullCharge;
                chkBalance.Checked = _model.StatusBalancing;
                chkSleep.Checked = _model.StatusSleep;

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

                if (cboCellCount != null)
                {
                    int cells = _model.GetEffectiveCellCount();
                    cboCellCount.SelectedIndex = (cells == 8) ? 0 : ((cells == 15) ? 1 : 2);
                }
            }
            UpdateAlarmProtectionVisualStyles();
            _isUpdatingUi = false;
        }

        private void UpdateNumericCommitted(NumericUpDown num, decimal val)
        {
            if (num == null) return;
            decimal clampedVal = Math.Max(num.Minimum, Math.Min(num.Maximum, val));
            num.Value = clampedVal;
            TextBox tb = GetInnerTextBox(num);
            if (tb != null)
            {
                if (_comm != null && _comm.CurrentWorkMode == WorkMode.MasterPoller && !_hasMasterTelemetryReceived)
                {
                    tb.Text = "";
                }
                else
                {
                    tb.Text = (num.DecimalPlaces > 0) ? clampedVal.ToString("F" + num.DecimalPlaces) : clampedVal.ToString("F0");
                }
            }
            _committedValues[num] = clampedVal;
            _isDirty[num] = false;
        }

        private void Comm_OnLogEvent(object sender, LogEventArgs e)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            this.BeginInvoke(new Action(delegate()
            {
                if (this.IsDisposed || rtbLog.IsDisposed || !rtbLog.IsHandleCreated) return;

                UpdateStatsLabels();
                if (e.LatencyMs > 0)
                {
                    lblLatency.Text = string.Format("时延: {0} ms", e.LatencyMs);
                }

                string timeStr = e.Timestamp.ToString("HH:mm:ss");
                string dirStr = e.IsTx ? "TX" : "RX";

                // 默认颜色: TX天蓝, RX浅绿
                Color textColor = e.IsTx ? Color.FromArgb(120, 180, 250) : Color.FromArgb(100, 220, 140);
                bool isAlarmOrFault = false;

                // 告警/保护报文摘要智能高亮 (仅在真正触发告警或保护跳闸时高亮):
                if (!string.IsNullOrEmpty(e.Summary))
                {
                    if (e.Summary.Contains("保护跳闸") || e.Summary.Contains("系统故障") || e.Summary.Contains("保护触发") || e.Summary.Contains("错误码") || e.Summary.Contains("故障注入"))
                    {
                        textColor = Color.FromArgb(248, 113, 113); // 亮红 (保护跳闸)
                        isAlarmOrFault = true;
                    }
                    else if (e.Summary.Contains("告警提示") || e.Summary.Contains("告警触发") || e.Summary.Contains("警告提示"))
                    {
                        textColor = Color.FromArgb(251, 191, 36);  // 亮橙黄 (告警提示)
                        isAlarmOrFault = true;
                    }
                }

                if (chkFilterAlarmOnly != null && chkFilterAlarmOnly.Checked && !isAlarmOrFault)
                {
                    return;
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

                if (chkShowHex.Checked && e.RawBytes != null && e.RawBytes.Length > 0)
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

                if (txtLogFilter != null && !string.IsNullOrEmpty(txtLogFilter.Text))
                {
                    string kw = txtLogFilter.Text.Trim();
                    if (kw.Length > 0 && logText.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return;
                    }
                }

                bool autoScroll = chkAutoScroll.Checked;

                if (autoScroll)
                {
                    // 5000 行上限截断保护 (Win32 O(1) 免 GC 查询，防止长期自动化测试 OOM)
                    int lineCount = (int)SendMessage(rtbLog.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero);
                    if (lineCount > 5000)
                    {
                        int cutCharIndex = rtbLog.GetFirstCharIndexFromLine(1000);
                        if (cutCharIndex > 0)
                        {
                            rtbLog.Select(0, cutCharIndex);
                            rtbLog.SelectedText = string.Empty;
                        }
                    }

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
                        // 完整记录当前的水平 (X) 与垂直 (Y) 像素滚动位置与选中范围
                        POINT scrollPos = new POINT();
                        SendMessage(rtbLog.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scrollPos);
                        int selStart = rtbLog.SelectionStart;
                        int selLen = rtbLog.SelectionLength;

                        int lineCount = (int)SendMessage(rtbLog.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero);
                        if (lineCount > 5000)
                        {
                            int cutCharIndex = rtbLog.GetFirstCharIndexFromLine(1000);
                            if (cutCharIndex > 0)
                            {
                                rtbLog.Select(0, cutCharIndex);
                                rtbLog.SelectedText = string.Empty;
                                selStart = Math.Max(0, selStart - cutCharIndex);
                            }
                        }

                        rtbLog.SelectionStart = rtbLog.TextLength;
                        rtbLog.SelectionLength = 0;
                        rtbLog.SelectionColor = textColor;
                        rtbLog.AppendText(logText);

                        rtbLog.Select(selStart, selLen);
                        // 无缝恢复 X 与 Y 双向滚动位置，彻底杜绝向右查看时向左跳动的 bug
                        SendMessage(rtbLog.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scrollPos);
                    }
                    finally
                    {
                        SendMessage(rtbLog.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                        rtbLog.Invalidate();
                    }
                }
            }));
        }

        private void RtbLog_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (rtbLog == null || rtbLog.TextLength == 0) return;
            int charIdx = rtbLog.GetCharIndexFromPosition(e.Location);
            int lineIdx = rtbLog.GetLineFromCharIndex(charIdx);
            string[] lines = rtbLog.Lines;
            if (lineIdx < 0 || lineIdx >= lines.Length) return;

            string lineText = lines[lineIdx];
            if (string.IsNullOrEmpty(lineText) || lineText.Trim().Length == 0) return;

            string protName = (_comm != null && _comm.ProtocolHandler != null) ? _comm.ProtocolHandler.ProtocolName : "CVTE";
            string report = FrameAnatomyInspector.DissectLogLine(lineText, protName);
            using (FrameInspectorDialog dlg = new FrameInspectorDialog(report))
            {
                dlg.ShowDialog(this);
            }
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
                if (this.IsDisposed) return;
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
                    try
                    {
                        File.WriteAllText(sfd.FileName, rtbLog.Text, new UTF8Encoding(true));
                        MessageBox.Show("日志已成功保存！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("保存日志失败:\n" + ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                if (splitMain != null && splitMain.Width > (splitMain.Panel1MinSize + splitMain.Panel2MinSize + 20))
                {
                    int targetDist = (int)(splitMain.Width * 0.52);
                    targetDist = Math.Max(splitMain.Panel1MinSize, Math.Min(targetDist, splitMain.Width - splitMain.Panel2MinSize));
                    splitMain.SplitterDistance = targetDist;
                }
            }
            catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (this.WindowState == FormWindowState.Minimized) return;
            try
            {
                if (splitMain != null && splitMain.Width > (splitMain.Panel1MinSize + splitMain.Panel2MinSize + 20))
                {
                    int targetDist = (int)(splitMain.Width * 0.52);
                    targetDist = Math.Max(splitMain.Panel1MinSize, Math.Min(targetDist, splitMain.Width - splitMain.Panel2MinSize));
                    splitMain.SplitterDistance = targetDist;
                }
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_deviceChangeTimer != null)
            {
                try
                {
                    _deviceChangeTimer.Stop();
                    _deviceChangeTimer.Dispose();
                }
                catch { }
                _deviceChangeTimer = null;
            }

            if (_flashTimers != null)
            {
                foreach (Timer tmr in _flashTimers.Values)
                {
                    if (tmr != null)
                    {
                        try
                        {
                            tmr.Stop();
                            tmr.Dispose();
                        }
                        catch { }
                    }
                }
                _flashTimers.Clear();
            }

            SavePersistentConfig();

            if (_comm != null)
            {
                _comm.OnLogEvent -= Comm_OnLogEvent;
                _comm.OnStatusChanged -= Comm_OnStatusChanged;
                _comm.OnMasterTelemetryUpdated -= Comm_OnMasterTelemetryUpdated;
                _comm.OnScanPacksCompleted -= Comm_OnScanPacksCompleted;
                _comm.OnAutoAdapted -= Comm_OnAutoAdapted;
                _comm.OnPortDisconnected -= Comm_OnPortDisconnected;
                _comm.Close();
            }

            if (_flagFontRegular != null)
            {
                _flagFontRegular.Dispose();
                _flagFontRegular = null;
            }
            if (_flagFontBold != null)
            {
                _flagFontBold.Dispose();
                _flagFontBold = null;
            }

            base.OnFormClosing(e);
        }
    }
}
