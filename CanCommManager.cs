using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BMS_Protocol_Simulator
{
    public interface ICanDriver : IDisposable
    {
        string DriverName { get; }
        bool IsOpen { get; }
        bool Open();
        void Close();
        bool SendFrame(CanFrame frame);

        event EventHandler<CanFrame> OnFrameReceived;
        event EventHandler<string> OnError;
        event EventHandler<string> OnStatusChanged;
    }

    /// <summary>
    /// 免外部 DLL 的 SLCAN (LAWICEL 协议) 驱动
    /// 适用于 CANable, CandleLight, 以及基于虚拟串口的开源/商用 USB-CAN 适配器
    /// </summary>
    public class SlcanDriver : ICanDriver
    {
        private SerialPort _port;
        private Thread _rxThread;
        private volatile bool _isRunning = false;
        private readonly object _txLock = new object();

        public string DriverName
        {
            get { return "SLCAN (CANable/虚拟串口)"; }
        }

        public string PortName { get; set; }
        public string CanBaudRate { get; set; }

        public bool IsOpen
        {
            get { return _port != null && _port.IsOpen; }
        }

        public event EventHandler<CanFrame> OnFrameReceived;
        public event EventHandler<string> OnError;
        public event EventHandler<string> OnStatusChanged;

        public SlcanDriver(string portName, string canBaudRate = "500k")
        {
            PortName = portName;
            CanBaudRate = canBaudRate;
        }

        public bool Open()
        {
            Close();
            try
            {
                // USB CDC-ACM 虚拟串口波特率无关，设置为 115200 兼容传统串口
                _port = new SerialPort(PortName, 115200, Parity.None, 8, StopBits.One);
                _port.ReadTimeout = 500;
                _port.WriteTimeout = 500;
                _port.NewLine = "\r";
                _port.Open();

                // SLCAN 初始化时序
                // 1. 发送关闭命令 C 清除先前状态
                WriteCommand("C");
                Thread.Sleep(50);

                // 2. 发送波特率指令 (S0=10k, S1=20k, S2=50k, S3=100k, S4=125k, S5=250k, S6=500k, S7=800k, S8=1M)
                string speedCmd = GetSpeedCommand(CanBaudRate);
                WriteCommand(speedCmd);
                Thread.Sleep(50);

                // 3. 发送 O 打开 CAN 通道
                WriteCommand("O");
                Thread.Sleep(50);

                _isRunning = true;
                _rxThread = new Thread(RxWorkerLoop);
                _rxThread.IsBackground = true;
                _rxThread.Priority = ThreadPriority.AboveNormal;
                _rxThread.Name = "SLCAN_RxWorker";
                _rxThread.Start();

                if (OnStatusChanged != null)
                {
                    OnStatusChanged(this, string.Format("SLCAN 适配器已在 {0} 打开 (CAN速率: {1})", PortName, CanBaudRate));
                }
                return true;
            }
            catch (Exception ex)
            {
                Close();
                if (OnError != null)
                {
                    OnError(this, string.Format("打开 SLCAN 失败: {0}", ex.Message));
                }
                throw;
            }
        }

        private string GetSpeedCommand(string baud)
        {
            if (string.IsNullOrEmpty(baud)) return "S6"; // 默认 500k
            string b = baud.ToLower().Replace("bps", "").Replace("b", "").Trim();
            if (b == "10k") return "S0";
            if (b == "20k") return "S1";
            if (b == "50k") return "S2";
            if (b == "100k") return "S3";
            if (b == "125k") return "S4";
            if (b == "250k") return "S5";
            if (b == "500k") return "S6";
            if (b == "800k") return "S7";
            if (b == "1000k" || b == "1m") return "S8";
            return "S6"; // 默认 500k
        }

        private void WriteCommand(string cmd)
        {
            if (_port != null && _port.IsOpen)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(cmd + "\r");
                _port.Write(bytes, 0, bytes.Length);
            }
        }

        public bool SendFrame(CanFrame frame)
        {
            if (!IsOpen || frame == null) return false;
            try
            {
                string asciiCmd;
                StringBuilder sb = new StringBuilder();
                if (frame.IsExtended)
                {
                    sb.Append("T");
                    sb.Append((frame.Id & 0x1FFFFFFF).ToString("X8"));
                }
                else
                {
                    sb.Append("t");
                    sb.Append((frame.Id & 0x7FF).ToString("X3"));
                }

                sb.Append(frame.Dlc.ToString("X1"));
                for (int i = 0; i < frame.Dlc; i++)
                {
                    sb.Append(frame.Data[i].ToString("X2"));
                }
                sb.Append("\r");
                asciiCmd = sb.ToString();

                byte[] bytes = Encoding.ASCII.GetBytes(asciiCmd);
                lock (_txLock)
                {
                    _port.Write(bytes, 0, bytes.Length);
                }
                return true;
            }
            catch (Exception ex)
            {
                if (OnError != null)
                {
                    OnError(this, string.Format("SLCAN 发送失败: {0}", ex.Message));
                }
                return false;
            }
        }

        private void RxWorkerLoop()
        {
            StringBuilder lineBuf = new StringBuilder();
            byte[] rawByte = new byte[1];

            while (_isRunning && IsOpen)
            {
                try
                {
                    int b = _port.ReadByte();
                    if (b < 0) continue;

                    if (b == '\r' || b == '\n')
                    {
                        if (lineBuf.Length > 0)
                        {
                            string line = lineBuf.ToString().Trim();
                            lineBuf.Length = 0;
                            ParseSlcanLine(line);
                        }
                    }
                    else if (b == 0x07) // BELL (error)
                    {
                        lineBuf.Length = 0;
                        if (OnError != null)
                        {
                            OnError(this, "SLCAN 收到总线错误响应 (BEL)");
                        }
                    }
                    else
                    {
                        lineBuf.Append((char)b);
                        if (lineBuf.Length > 100) lineBuf.Length = 0; // 防止溢出
                    }
                }
                catch (TimeoutException)
                {
                    // 正常读取超时
                }
                catch (Exception ex)
                {
                    if (_isRunning && OnError != null)
                    {
                        OnError(this, string.Format("SLCAN 接收异常: {0}", ex.Message));
                    }
                    Thread.Sleep(10);
                }
            }
        }

        private void ParseSlcanLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            try
            {
                char type = line[0];
                if (type == 't') // 标准数据帧: tIIILDD...
                {
                    if (line.Length < 5) return;
                    uint id = Convert.ToUInt32(line.Substring(1, 3), 16);
                    byte dlc = Convert.ToByte(line.Substring(4, 1), 16);
                    if (line.Length < 5 + dlc * 2) return;

                    byte[] data = new byte[dlc];
                    for (int i = 0; i < dlc; i++)
                    {
                        data[i] = Convert.ToByte(line.Substring(5 + i * 2, 2), 16);
                    }

                    CanFrame frame = new CanFrame(id, data, false, false);
                    if (OnFrameReceived != null)
                    {
                        OnFrameReceived(this, frame);
                    }
                }
                else if (type == 'T') // 扩展数据帧: TIIIIIIIILDD...
                {
                    if (line.Length < 10) return;
                    uint id = Convert.ToUInt32(line.Substring(1, 8), 16);
                    byte dlc = Convert.ToByte(line.Substring(9, 1), 16);
                    if (line.Length < 10 + dlc * 2) return;

                    byte[] data = new byte[dlc];
                    for (int i = 0; i < dlc; i++)
                    {
                        data[i] = Convert.ToByte(line.Substring(10 + i * 2, 2), 16);
                    }

                    CanFrame frame = new CanFrame(id, data, true, false);
                    if (OnFrameReceived != null)
                    {
                        OnFrameReceived(this, frame);
                    }
                }
            }
            catch { }
        }

        public void Close()
        {
            _isRunning = false;
            if (_rxThread != null)
            {
                try { _rxThread.Join(200); } catch { }
                _rxThread = null;
            }

            if (_port != null)
            {
                try
                {
                    if (_port.IsOpen)
                    {
                        WriteCommand("C");
                        _port.Close();
                    }
                }
                catch { }
                finally
                {
                    _port.Dispose();
                    _port = null;
                }
            }

            if (OnStatusChanged != null)
            {
                OnStatusChanged(this, "SLCAN 已关闭");
            }
        }

        public void Dispose()
        {
            Close();
        }
    }

    /// <summary>
    /// ControlCAN.dll 原生 API 封装
    /// 适用于创芯科技 CANalyst-II、周立功 USBCAN-I/II、USBCAN-2E-U 等工业级适配器
    /// </summary>
    public static class ControlCanNative
    {
        public const uint STATUS_OK = 1;

        public const uint VCI_USBCAN1 = 3;
        public const uint VCI_USBCAN2 = 4;
        public const uint VCI_USBCAN_2E_U = 21;

        [StructLayout(LayoutKind.Sequential)]
        public struct VCI_INIT_CONFIG
        {
            public uint AccCode;
            public uint AccMask;
            public uint Reserved;
            public byte Filter;
            public byte Timing0;
            public byte Timing1;
            public byte Mode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VCI_CAN_OBJ
        {
            public uint ID;
            public uint TimeStamp;
            public byte TimeFlag;
            public byte SendType;
            public byte RemoteFlag;
            public byte ExternFlag;
            public byte DataLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] Data;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public byte[] Reserved;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr LoadLibrary(string lpLibFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeLibrary(IntPtr hLibModule);

        [DllImport("ControlCAN.dll", EntryPoint = "VCI_OpenDevice", CallingConvention = CallingConvention.StdCall)]
        public static extern uint VCI_OpenDevice(uint devType, uint devIndex, uint reserved);

        [DllImport("ControlCAN.dll", EntryPoint = "VCI_CloseDevice", CallingConvention = CallingConvention.StdCall)]
        public static extern uint VCI_CloseDevice(uint devType, uint devIndex);

        [DllImport("ControlCAN.dll", EntryPoint = "VCI_InitCAN", CallingConvention = CallingConvention.StdCall)]
        public static extern uint VCI_InitCAN(uint devType, uint devIndex, uint canIndex, ref VCI_INIT_CONFIG config);

        [DllImport("ControlCAN.dll", EntryPoint = "VCI_StartCAN", CallingConvention = CallingConvention.StdCall)]
        public static extern uint VCI_StartCAN(uint devType, uint devIndex, uint canIndex);

        [DllImport("ControlCAN.dll", EntryPoint = "VCI_ResetCAN", CallingConvention = CallingConvention.StdCall)]
        public static extern uint VCI_ResetCAN(uint devType, uint devIndex, uint canIndex);

        [DllImport("ControlCAN.dll", EntryPoint = "VCI_Transmit", CallingConvention = CallingConvention.StdCall)]
        public static extern uint VCI_Transmit(uint devType, uint devIndex, uint canIndex, ref VCI_CAN_OBJ send, uint len);

        [DllImport("ControlCAN.dll", EntryPoint = "VCI_Receive", CallingConvention = CallingConvention.StdCall)]
        public static extern uint VCI_Receive(uint devType, uint devIndex, uint canIndex, [Out] VCI_CAN_OBJ[] receive, uint len, int waitTime);

        [DllImport("ControlCAN.dll", EntryPoint = "VCI_ClearBuffer", CallingConvention = CallingConvention.StdCall)]
        public static extern uint VCI_ClearBuffer(uint devType, uint devIndex, uint canIndex);
    }

    /// <summary>
    /// 通用 ControlCAN.dll 适配驱动 (创芯科技 CANalyst-II / 周立功 USBCAN)
    /// 内置安全依赖检查，缺少 DLL 时给出友好向导提示，杜绝程序崩溃
    /// </summary>
    public class ControlCanDriver : ICanDriver
    {
        private Thread _rxThread;
        private volatile bool _isRunning = false;
        private readonly object _txLock = new object();

        public string DriverName
        {
            get { return "ControlCAN (CANalyst-II/周立功)"; }
        }

        public uint DevType { get; set; }
        public uint DevIndex { get; set; }
        public uint CanIndex { get; set; }
        public string CanBaudRate { get; set; }

        public bool IsOpen { get; private set; }

        public event EventHandler<CanFrame> OnFrameReceived;
        public event EventHandler<string> OnError;
        public event EventHandler<string> OnStatusChanged;

        public ControlCanDriver(uint devType = ControlCanNative.VCI_USBCAN2, uint devIndex = 0, uint canIndex = 0, string canBaudRate = "500k")
        {
            DevType = devType;
            DevIndex = devIndex;
            CanIndex = canIndex;
            CanBaudRate = canBaudRate;
            IsOpen = false;
        }

        /// <summary>
        /// 检查当前运行环境是否具备 ControlCAN.dll
        /// </summary>
        public static bool CheckDllAvailability(out string errorReason)
        {
            errorReason = "";
            string dllName = "ControlCAN.dll";
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string fullPath = Path.Combine(baseDir, dllName);

            if (!File.Exists(fullPath))
            {
                errorReason = string.Format("未在程序运行目录检测到 {0}！\n\n请将适用于您 CAN 适配器(如创芯科技CANalyst-II/周立功)的 ControlCAN.dll (及配套驱动DLL) 复制到上位机同一目录下。\n或选择免外部 DLL 的 SLCAN (CANable) 模式运行。", dllName);
                return false;
            }

            try
            {
                IntPtr hModule = ControlCanNative.LoadLibrary(fullPath);
                if (hModule == IntPtr.Zero)
                {
                    int win32Err = Marshal.GetLastWin32Error();
                    errorReason = string.Format("ControlCAN.dll 加载失败 (Win32错误码: {0})。\n\n可能原因：DLL位数(32位/64位)与当前操作系统或进程不匹配，或者缺少配套的依赖项(如kerneldlls)。\n建议切换至免 DLL 的 SLCAN 串口模式使用。", win32Err);
                    return false;
                }
                ControlCanNative.FreeLibrary(hModule);
                return true;
            }
            catch (Exception ex)
            {
                errorReason = string.Format("加载 ControlCAN.dll 时发生异常: {0}", ex.Message);
                return false;
            }
        }

        public bool Open()
        {
            Close();

            string checkError;
            if (!CheckDllAvailability(out checkError))
            {
                throw new InvalidOperationException(checkError);
            }

            try
            {
                uint ret = ControlCanNative.VCI_OpenDevice(DevType, DevIndex, 0);
                if (ret != ControlCanNative.STATUS_OK)
                {
                    throw new Exception(string.Format("打开 CAN 设备失败 (类型:{0}, 索引:{1})，请检查硬件连接或是否被其它程序占用。", DevType, DevIndex));
                }

                ControlCanNative.VCI_INIT_CONFIG cfg = new ControlCanNative.VCI_INIT_CONFIG();
                cfg.AccCode = 0x00000000;
                cfg.AccMask = 0xFFFFFFFF; // 接收全部帧
                cfg.Filter = 1;           // 单滤波接收全部
                cfg.Mode = 0;             // 正常模式

                byte t0, t1;
                GetTimingBytes(CanBaudRate, out t0, out t1);
                cfg.Timing0 = t0;
                cfg.Timing1 = t1;

                ret = ControlCanNative.VCI_InitCAN(DevType, DevIndex, CanIndex, ref cfg);
                if (ret != ControlCanNative.STATUS_OK)
                {
                    ControlCanNative.VCI_CloseDevice(DevType, DevIndex);
                    throw new Exception(string.Format("初始化 CAN{0} 通道失败。", CanIndex));
                }

                ret = ControlCanNative.VCI_StartCAN(DevType, DevIndex, CanIndex);
                if (ret != ControlCanNative.STATUS_OK)
                {
                    ControlCanNative.VCI_CloseDevice(DevType, DevIndex);
                    throw new Exception(string.Format("启动 CAN{0} 接收失败。", CanIndex));
                }

                ControlCanNative.VCI_ClearBuffer(DevType, DevIndex, CanIndex);

                IsOpen = true;
                _isRunning = true;
                _rxThread = new Thread(RxWorkerLoop);
                _rxThread.IsBackground = true;
                _rxThread.Priority = ThreadPriority.AboveNormal;
                _rxThread.Name = "ControlCAN_RxWorker";
                _rxThread.Start();

                if (OnStatusChanged != null)
                {
                    OnStatusChanged(this, string.Format("ControlCAN 已打开 (类型:{0}, 索引:{1}, CAN{2}, 速率:{3})", DevType, DevIndex, CanIndex, CanBaudRate));
                }

                return true;
            }
            catch (Exception ex)
            {
                Close();
                if (OnError != null)
                {
                    OnError(this, ex.Message);
                }
                throw;
            }
        }

        private void GetTimingBytes(string baud, out byte timing0, out byte timing1)
        {
            // 针对 16MHz SJA1000 标准波特率参数
            string b = (baud ?? "").ToLower().Replace("bps", "").Replace("b", "").Trim();
            switch (b)
            {
                case "1000k":
                case "1m":
                    timing0 = 0x00; timing1 = 0x14; break;
                case "800k":
                    timing0 = 0x00; timing1 = 0x16; break;
                case "500k":
                    timing0 = 0x00; timing1 = 0x1C; break;
                case "250k":
                    timing0 = 0x01; timing1 = 0x1C; break;
                case "125k":
                    timing0 = 0x03; timing1 = 0x1C; break;
                case "100k":
                    timing0 = 0x04; timing1 = 0x1C; break;
                case "50k":
                    timing0 = 0x09; timing1 = 0x1C; break;
                default:
                    timing0 = 0x00; timing1 = 0x1C; break; // 默认 500k
            }
        }

        public bool SendFrame(CanFrame frame)
        {
            if (!IsOpen || frame == null) return false;

            ControlCanNative.VCI_CAN_OBJ obj = new ControlCanNative.VCI_CAN_OBJ();
            obj.ID = frame.Id;
            obj.SendType = 0; // 正常发送
            obj.RemoteFlag = (byte)(frame.IsRemote ? 1 : 0);
            obj.ExternFlag = (byte)(frame.IsExtended ? 1 : 0);
            obj.DataLen = frame.Dlc;
            obj.Data = new byte[8];
            if (frame.Data != null)
            {
                Array.Copy(frame.Data, obj.Data, Math.Min(frame.Data.Length, 8));
            }
            obj.Reserved = new byte[3];

            try
            {
                lock (_txLock)
                {
                    uint ret = ControlCanNative.VCI_Transmit(DevType, DevIndex, CanIndex, ref obj, 1);
                    return ret == 1;
                }
            }
            catch (Exception ex)
            {
                if (OnError != null)
                {
                    OnError(this, string.Format("ControlCAN 发送异常: {0}", ex.Message));
                }
                return false;
            }
        }

        private void RxWorkerLoop()
        {
            ControlCanNative.VCI_CAN_OBJ[] buffer = new ControlCanNative.VCI_CAN_OBJ[50];

            while (_isRunning && IsOpen)
            {
                try
                {
                    uint rxCount = ControlCanNative.VCI_Receive(DevType, DevIndex, CanIndex, buffer, (uint)buffer.Length, 100);
                    if (rxCount > 0 && rxCount <= buffer.Length)
                    {
                        for (int i = 0; i < rxCount; i++)
                        {
                            ControlCanNative.VCI_CAN_OBJ obj = buffer[i];
                            CanFrame frame = new CanFrame(obj.ID, obj.Data, obj.ExternFlag != 0, obj.RemoteFlag != 0);
                            frame.Dlc = obj.DataLen;

                            if (OnFrameReceived != null)
                            {
                                OnFrameReceived(this, frame);
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                }
                catch (Exception ex)
                {
                    if (_isRunning && OnError != null)
                    {
                        OnError(this, string.Format("ControlCAN 接收错误: {0}", ex.Message));
                    }
                    Thread.Sleep(20);
                }
            }
        }

        public void Close()
        {
            _isRunning = false;
            IsOpen = false;

            if (_rxThread != null)
            {
                try { _rxThread.Join(200); } catch { }
                _rxThread = null;
            }

            try
            {
                ControlCanNative.VCI_ResetCAN(DevType, DevIndex, CanIndex);
                ControlCanNative.VCI_CloseDevice(DevType, DevIndex);
            }
            catch { }

            if (OnStatusChanged != null)
            {
                OnStatusChanged(this, "ControlCAN 设备已关闭");
            }
        }

        public void Dispose()
        {
            Close();
        }
    }

    /// <summary>
    /// CAN 通信总调度引擎
    /// 统一管理从机广播循环与主机逆变器轮询，无缝对齐现有 UI 遥测与日志体系
    /// </summary>
    public class CanCommManager
    {
        private ICanDriver _driver;
        private Thread _workerThread;
        private volatile bool _isRunning = false;
        private Stopwatch _watch = new Stopwatch();

        public BmsDataModel Model { get; private set; }
        public WorkMode CurrentWorkMode { get; set; }
        public int BroadcastIntervalMs { get; set; }
        public bool IsPollingPaused { get; set; }
        public bool IsBmsLost { get; private set; }
        public long LastResponseTimeMs { get; private set; }

        public long RxCount { get; private set; }
        public long TxCount { get; private set; }
        public long ErrorCount { get; private set; }

        public event EventHandler<LogEventArgs> OnLogEvent;
        public event EventHandler<string> OnStatusChanged;
        public event EventHandler<BmsDataModel> OnMasterTelemetryUpdated;

        public bool IsOpen
        {
            get { return _driver != null && _driver.IsOpen; }
        }

        public ICanDriver Driver
        {
            get { return _driver; }
        }

        public CanCommManager(BmsDataModel model)
        {
            Model = model;
            CurrentWorkMode = WorkMode.SlaveSimulator;
            BroadcastIntervalMs = 1000; // 协议规范默认 1s 广播周期
            IsPollingPaused = false;
            IsBmsLost = false;
            LastResponseTimeMs = 0;
            RxCount = 0;
            TxCount = 0;
            ErrorCount = 0;
            _watch.Start();
        }

        public bool Open(ICanDriver driver)
        {
            Close();

            _driver = driver;
            _driver.OnFrameReceived += Driver_OnFrameReceived;
            _driver.OnError += Driver_OnError;
            _driver.OnStatusChanged += Driver_OnStatusChanged;

            bool opened = _driver.Open();
            if (opened)
            {
                _isRunning = true;
                _workerThread = new Thread(WorkerLoop);
                _workerThread.IsBackground = true;
                _workerThread.Priority = ThreadPriority.AboveNormal;
                _workerThread.Name = "CanComm_Scheduler";
                _workerThread.Start();
            }

            return opened;
        }

        private void Driver_OnStatusChanged(object sender, string e)
        {
            if (OnStatusChanged != null)
            {
                OnStatusChanged(this, e);
            }
        }

        private void Driver_OnError(object sender, string e)
        {
            ErrorCount++;
            if (OnStatusChanged != null)
            {
                OnStatusChanged(this, "[CAN错误] " + e);
            }
        }

        private void Driver_OnFrameReceived(object sender, CanFrame frame)
        {
            RxCount++;
            string summary;
            HanchuCanProtocol.TryDecodeFrame(frame, Model, out summary);

            if (OnLogEvent != null)
            {
                OnLogEvent(this, new LogEventArgs(false, frame.GetRawData(), summary, 0));
            }

            if (CurrentWorkMode == WorkMode.MasterPoller)
            {
                // 主机模式下收到电池包的遥测帧，更新遥测和保活时间
                LastResponseTimeMs = _watch.ElapsedMilliseconds;
                IsBmsLost = false;

                if (frame.Id == HanchuCanProtocol.ID_CHARGE_DISCHARGE_MGMT ||
                    frame.Id == HanchuCanProtocol.ID_SOC_SOH_CELL_VOLT ||
                    frame.Id == HanchuCanProtocol.ID_PACK_VOLT_CURR_TEMP ||
                    frame.Id == HanchuCanProtocol.ID_ALARM_PROTECTION ||
                    frame.Id == HanchuCanProtocol.ID_REQUEST_FLAGS_STATUS)
                {
                    if (OnMasterTelemetryUpdated != null)
                    {
                        OnMasterTelemetryUpdated(this, Model);
                    }
                }
            }
        }

        private void WorkerLoop()
        {
            long lastBroadcastTime = 0;

            while (_isRunning && IsOpen)
            {
                try
                {
                    long now = _watch.ElapsedMilliseconds;

                    if (CurrentWorkMode == WorkMode.SlaveSimulator)
                    {
                        // ── 从机模式：每隔 1s 广播 6 帧数据 ──
                        if (!IsPollingPaused && (now - lastBroadcastTime >= BroadcastIntervalMs))
                        {
                            lastBroadcastTime = now;
                            List<KeyValuePair<CanFrame, string>> frames = HanchuCanProtocol.BuildSlaveBroadcastFrames(Model);
                            foreach (KeyValuePair<CanFrame, string> kvp in frames)
                            {
                                if (!_isRunning || !IsOpen) break;

                                bool ok = _driver.SendFrame(kvp.Key);
                                if (ok)
                                {
                                    TxCount++;
                                    if (OnLogEvent != null)
                                    {
                                        OnLogEvent(this, new LogEventArgs(true, kvp.Key.GetRawData(), kvp.Value, 0));
                                    }
                                }
                                else
                                {
                                    ErrorCount++;
                                }
                                Thread.Sleep(10); // 帧间微延时，防控制器突发拥塞
                            }
                        }
                    }
                    else if (CurrentWorkMode == WorkMode.MasterPoller)
                    {
                        // ── 主机模式：模拟逆变器发送 0x305 握手帧 ──
                        if (!IsPollingPaused && (now - lastBroadcastTime >= BroadcastIntervalMs))
                        {
                            lastBroadcastTime = now;
                            CanFrame handshake = HanchuCanProtocol.BuildInverterHandshakeFrame();
                            bool ok = _driver.SendFrame(handshake);
                            if (ok)
                            {
                                TxCount++;
                                if (OnLogEvent != null)
                                {
                                    OnLogEvent(this, new LogEventArgs(true, handshake.GetRawData(), "[HANCHU 0x305] 逆变器握手保活帧", 0));
                                }
                            }
                            else
                            {
                                ErrorCount++;
                            }
                        }

                        // 主机模式心跳超时检查 (3.5秒未收到任何帧视作失联)
                        if (LastResponseTimeMs > 0 && (now - LastResponseTimeMs > 3500))
                        {
                            if (!IsBmsLost)
                            {
                                IsBmsLost = true;
                                if (OnStatusChanged != null)
                                {
                                    OnStatusChanged(this, "⚠ 警告: CAN 通信超时，未收到 BMS 周期广播报文！");
                                }
                            }
                        }
                    }

                    Thread.Sleep(20);
                }
                catch (Exception ex)
                {
                    ErrorCount++;
                    if (OnStatusChanged != null)
                    {
                        OnStatusChanged(this, string.Format("CAN 调度循环异常: {0}", ex.Message));
                    }
                    Thread.Sleep(100);
                }
            }
        }

        public void Close()
        {
            _isRunning = false;
            if (_workerThread != null)
            {
                try { _workerThread.Join(300); } catch { }
                _workerThread = null;
            }

            if (_driver != null)
            {
                try
                {
                    _driver.OnFrameReceived -= Driver_OnFrameReceived;
                    _driver.OnError -= Driver_OnError;
                    _driver.OnStatusChanged -= Driver_OnStatusChanged;
                    _driver.Close();
                    _driver.Dispose();
                }
                catch { }
                finally
                {
                    _driver = null;
                }
            }
        }
    }
}
