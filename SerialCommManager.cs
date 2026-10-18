using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Diagnostics;
using System.Text;

namespace BMS_Protocol_Simulator
{
    public class LogEventArgs : EventArgs
    {
        public bool IsTx { get; private set; }
        public byte[] RawBytes { get; private set; }
        public string Summary { get; private set; }
        public long LatencyMs { get; private set; }
        public DateTime Timestamp { get; private set; }

        public LogEventArgs(bool isTx, byte[] rawBytes, string summary, long latencyMs = 0)
        {
            IsTx = isTx;
            RawBytes = rawBytes;
            Summary = summary;
            LatencyMs = latencyMs;
            Timestamp = DateTime.Now;
        }
    }

    public class DetectionEventArgs : EventArgs
    {
        public DetectionResult Result { get; private set; }
        public DetectionEventArgs(DetectionResult result)
        {
            Result = result;
        }
    }

    public class AutoAdaptEventArgs : EventArgs
    {
        public DetectedProtocolType Protocol { get; private set; }
        public InverterModel Model { get; private set; }
        public AutoAdaptEventArgs(DetectedProtocolType protocol, InverterModel model)
        {
            Protocol = protocol;
            Model = model;
        }
    }

    public class ScanProgressEventArgs : EventArgs
    {
        public int CurrentStep { get; private set; }
        public int TotalSteps { get; private set; }
        public byte CurrentId { get; private set; }
        public List<byte> FoundPacks { get; private set; }

        public ScanProgressEventArgs(int currentStep, int totalSteps, byte currentId, List<byte> foundPacks)
        {
            CurrentStep = currentStep;
            TotalSteps = totalSteps;
            CurrentId = currentId;
            FoundPacks = foundPacks;
        }
    }

    public class SerialCommManager
    {
        private SerialPort _port;
        private Thread _workerThread;
        private volatile bool _isRunning = false;
        private readonly object _portLock = new object();
        private readonly object _queueLock = new object();
        private Queue<KeyValuePair<byte[], string>> _pendingTxQueue = new Queue<KeyValuePair<byte[], string>>();

        private volatile bool _singlePollRequested = false;
        private volatile bool _scanRequested = false;
        private volatile bool _cancelScanRequested = false;
        public bool WasScanCancelled { get; private set; }

        public BmsDataModel Model { get; private set; }
        public IBmsProtocolHandler ProtocolHandler { get; set; }
        public WorkMode CurrentWorkMode { get; set; }
        public bool ReplyAllIds { get; set; }
        public int SpecificId { get; set; }

        // ── 智能机型/协议识别与自适应 ──
        public InverterDetector Detector { get; private set; }
        public bool AutoAdaptEnabled { get; set; }
        private InverterModel _lastAdaptedModel = InverterModel.Unknown;

        // ── 从机模式：通信层故障注入参数 ──
        public bool SimulateSilence { get; set; }
        public bool SimulateCorruptChecksum { get; set; }
        public int ExtraResponseDelayMs { get; set; }

        // ── 主机模式可控参数 ──
        public byte TargetSlaveId { get; set; }
        public int PollIntervalMs { get; set; }
        public bool IsPollingPaused { get; set; }
        public bool IsBmsLost { get; private set; }
        public long LastResponseTimeMs { get; private set; }

        // 统计数据
        public long RxCount { get; private set; }
        public long TxCount { get; private set; }
        public long ErrorCount { get; private set; }

        public event EventHandler<LogEventArgs> OnLogEvent;
        public event EventHandler<string> OnStatusChanged;
        public event EventHandler<BmsDataModel> OnMasterTelemetryUpdated;
        public event EventHandler<List<byte>> OnScanPacksCompleted;
        public event EventHandler<ScanProgressEventArgs> OnScanProgress;
        public event EventHandler<DetectionEventArgs> OnInverterDetected;
        public event EventHandler<AutoAdaptEventArgs> OnAutoAdapted;

        public bool IsOpen
        {
            get { return _port != null && _port.IsOpen; }
        }

        public SerialCommManager(BmsDataModel model)
        {
            Model = model;
            ProtocolHandler = new CvteModbusHandler();
            CurrentWorkMode = WorkMode.SlaveSimulator;
            ReplyAllIds = true;
            SpecificId = 1;
            TargetSlaveId = 1;
            PollIntervalMs = 500;
            IsPollingPaused = false;
            IsBmsLost = false;
            LastResponseTimeMs = 0;
            RxCount = 0;
            TxCount = 0;
            ErrorCount = 0;
            Detector = new InverterDetector();
            AutoAdaptEnabled = false;
            SimulateSilence = false;
            SimulateCorruptChecksum = false;
            ExtraResponseDelayMs = 0;
        }

        public bool Open(string portName, int baudRate, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One)
        {
            Close();
            try
            {
                _port = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
                _port.ReadTimeout = 50;
                _port.WriteTimeout = 200;
                _port.Open();

                _isRunning = true;
                _workerThread = new Thread(WorkerLoop);
                _workerThread.IsBackground = true;
                _workerThread.Priority = ThreadPriority.AboveNormal;
                _workerThread.Name = "BMS_SerialWorker";
                _workerThread.Start();

                if (OnStatusChanged != null)
                    OnStatusChanged(this, string.Format("串口 {0} 已打开 ({1} 8-N-1)", portName, baudRate));

                return true;
            }
            catch (Exception ex)
            {
                if (OnStatusChanged != null)
                    OnStatusChanged(this, "打开串口失败: " + ex.Message);
                return false;
            }
        }

        public void Close()
        {
            _isRunning = false;
            if (_workerThread != null)
            {
                _workerThread.Join(200);
                _workerThread = null;
            }

            lock (_portLock)
            {
                if (_port != null)
                {
                    try
                    {
                        if (_port.IsOpen) _port.Close();
                        _port.Dispose();
                    }
                    catch { }
                    _port = null;
                }
            }
            if (OnStatusChanged != null)
                OnStatusChanged(this, "串口已关闭");
        }

        public void ResetStatistics()
        {
            RxCount = 0;
            TxCount = 0;
            ErrorCount = 0;
            IsBmsLost = false;
            _lastAdaptedModel = InverterModel.Unknown;
            if (Detector != null) Detector.Reset();
        }

        // ── 主机模式：触发单次读取 ──
        public void TriggerSinglePoll()
        {
            _singlePollRequested = true;
        }

        // ── 主机模式：开始扫描在线电池包 ──
        public void StartScanOnlinePacks()
        {
            WasScanCancelled = false;
            _cancelScanRequested = false;
            _scanRequested = true;
        }

        // ── 主机模式：取消在线电池包扫描 ──
        public void CancelScan()
        {
            _cancelScanRequested = true;
            WasScanCancelled = true;
        }

        // ── 主机模式：发送控制指令 ──
        public void SendMasterControl(string commandType, object param = null)
        {
            string desc;
            byte[] frame = ProtocolHandler.BuildMasterControlFrame(TargetSlaveId, commandType, param, out desc);
            if (frame != null)
            {
                lock (_queueLock)
                {
                    _pendingTxQueue.Enqueue(new KeyValuePair<byte[], string>(frame, desc));
                }
            }
            else
            {
                if (OnStatusChanged != null)
                    OnStatusChanged(this, "下发控制指令失败: " + desc);
            }
        }

        private static bool IsAsciiHexByte(byte b)
        {
            return (b >= (byte)'0' && b <= (byte)'9') ||
                   (b >= (byte)'A' && b <= (byte)'F') ||
                   (b >= (byte)'a' && b <= (byte)'f');
        }

        // 判断 rxBuf[soiIndex] 处的 '~' 是否是合法的 Pylontech ASCII 帧头候选，避免二进制 Modbus 报文中的 0x7E 造成假阳性阻塞
        private static bool IsValidPylonCandidate(byte[] buf, int soiIndex, int totalLen)
        {
            if (soiIndex < 0 || soiIndex >= totalLen || buf[soiIndex] != (byte)'~') return false;
            int checkEnd = Math.Min(totalLen, soiIndex + 64);
            for (int i = soiIndex + 1; i < checkEnd; i++)
            {
                byte b = buf[i];
                if (b == 0x0D)
                {
                    return (i - soiIndex) >= 17;
                }
                if (!IsAsciiHexByte(b))
                {
                    return false;
                }
            }
            return true;
        }

        private void WorkerLoop()
        {
            byte[] rxBuf = new byte[1024];
            int rxLen = 0;
            Stopwatch swIdle = Stopwatch.StartNew();
            Stopwatch swPoll = Stopwatch.StartNew();
            Stopwatch swLastMasterRx = Stopwatch.StartNew();
            int pollPhase = 0;

            while (_isRunning)
            {
                try
                {
                    if (CurrentWorkMode == WorkMode.SlaveSimulator)
                    {
                        // ══════════════════════════════════════════════════════════
                        // 1. 从机模式：高速监听、智能机型协议识别与秒级响应逆变器查询
                        // ══════════════════════════════════════════════════════════
                        int bytesToRead = 0;
                        lock (_portLock)
                        {
                            if (_port != null && _port.IsOpen)
                                bytesToRead = _port.BytesToRead;
                        }

                        if (bytesToRead > 0)
                        {
                            int spaceLeft = rxBuf.Length - rxLen;
                            if (spaceLeft > 0)
                            {
                                int read = 0;
                                lock (_portLock)
                                {
                                    if (_port != null && _port.IsOpen)
                                        read = _port.Read(rxBuf, rxLen, Math.Min(bytesToRead, spaceLeft));
                                }
                                if (read > 0)
                                {
                                    rxLen += read;
                                    swIdle.Restart();
                                }
                            }
                            else
                            {
                                // 缓冲区满且长时间未能成功解帧，丢弃陈旧字节防止死锁挂起
                                Array.Copy(rxBuf, 256, rxBuf, 0, rxLen - 256);
                                rxLen -= 256;
                            }
                        }

                        // 分包解帧检测与超时处理 (外置于 bytesToRead，确保静默超时也能解出帧或清理残包)
                        while (rxLen > 0)
                        {
                            byte[] currentFrame = null;

                            // 1. 查找合法的 Pylontech ASCII 帧候选 ('~' 后跟纯 ASCII Hex 字符并以 '\r' 结尾)
                            int soiIndex = -1;
                            for (int i = 0; i < rxLen; i++)
                            {
                                if (rxBuf[i] == (byte)'~' && IsValidPylonCandidate(rxBuf, i, rxLen))
                                {
                                    soiIndex = i;
                                    break;
                                }
                            }

                            // 2. 查找合法的 8 字节 Modbus RTU 标准查询帧
                            int mbIdx = -1;
                            for (int i = 0; i <= rxLen - 8; i++)
                            {
                                if (rxBuf[i + 1] == 0x03 && ModbusCrc.Check(rxBuf, 8, i))
                                {
                                    mbIdx = i;
                                    break;
                                }
                            }

                            // 若先出现完整的 Modbus RTU 查询帧，优先提取 Modbus 帧（彻底杜绝 0x7E 字节饿死 Modbus）
                            if (mbIdx >= 0 && (soiIndex < 0 || mbIdx < soiIndex))
                            {
                                if (mbIdx > 0)
                                {
                                    Array.Copy(rxBuf, mbIdx, rxBuf, 0, rxLen - mbIdx);
                                    rxLen -= mbIdx;
                                }

                                int frameLen = 8;
                                currentFrame = new byte[frameLen];
                                Array.Copy(rxBuf, 0, currentFrame, 0, frameLen);
                                Array.Copy(rxBuf, frameLen, rxBuf, 0, rxLen - frameLen);
                                rxLen -= frameLen;
                            }
                            else if (soiIndex >= 0)
                            {
                                int eoiIndex = -1;
                                for (int i = soiIndex; i < rxLen; i++)
                                {
                                    if (rxBuf[i] == 0x0D) { eoiIndex = i; break; }
                                }

                                if (eoiIndex >= 0)
                                {
                                    if (soiIndex > 0)
                                    {
                                        Array.Copy(rxBuf, soiIndex, rxBuf, 0, rxLen - soiIndex);
                                        rxLen -= soiIndex;
                                        eoiIndex -= soiIndex;
                                    }

                                    int frameLen = eoiIndex + 1;
                                    currentFrame = new byte[frameLen];
                                    Array.Copy(rxBuf, 0, currentFrame, 0, frameLen);
                                    Array.Copy(rxBuf, frameLen, rxBuf, 0, rxLen - frameLen);
                                    rxLen -= frameLen;
                                }
                                else if (soiIndex > 0)
                                {
                                    // '~' 前面有噪声杂散字节，直接丢弃前导噪声
                                    Array.Copy(rxBuf, soiIndex, rxBuf, 0, rxLen - soiIndex);
                                    rxLen -= soiIndex;
                                }
                                else if (swIdle.ElapsedMilliseconds >= 250)
                                {
                                    // Pylontech ASCII 帧以 '~' 开头但长时间未收到 '\r'，丢弃无效首字节恢复解包
                                    Array.Copy(rxBuf, 1, rxBuf, 0, rxLen - 1);
                                    rxLen--;
                                    swIdle.Restart();
                                }
                            }
                            else if (rxLen > 0 && swIdle.ElapsedMilliseconds >= 35)
                            {
                                int frameLen = rxLen;
                                currentFrame = new byte[frameLen];
                                Array.Copy(rxBuf, 0, currentFrame, 0, frameLen);
                                rxLen = 0;
                            }

                            if (currentFrame == null)
                            {
                                if (rxLen >= 800)
                                {
                                    Array.Copy(rxBuf, 400, rxBuf, 0, rxLen - 400);
                                    rxLen -= 400;
                                }
                                break;
                            }

                            RxCount++;
                            Stopwatch swResp = Stopwatch.StartNew();

                            // ── 智能机型与协议识别 ──
                            DetectionResult det = Detector.InspectFrame(currentFrame, 0, currentFrame.Length);
                            if (det != null && OnInverterDetected != null)
                            {
                                OnInverterDetected(this, new DetectionEventArgs(det));
                            }

                            // ── 智能自适应切换 (若开启自适应且检测到已知协议，在首帧应答前同步完成切换) ──
                            if (AutoAdaptEnabled && det != null && det.Protocol != DetectedProtocolType.Unknown)
                            {
                                bool needSwitch = false;
                                if (det.Protocol == DetectedProtocolType.Growatt && !(ProtocolHandler is GrowattModbusHandler))
                                {
                                    ProtocolHandler = new GrowattModbusHandler();
                                    needSwitch = true;
                                }
                                else if (det.Protocol == DetectedProtocolType.Voltronic && !(ProtocolHandler is VoltronicModbusHandler))
                                {
                                    ProtocolHandler = new VoltronicModbusHandler();
                                    needSwitch = true;
                                }
                                else if (det.Protocol == DetectedProtocolType.Pylontech && !(ProtocolHandler is PylontechAsciiHandler))
                                {
                                    ProtocolHandler = new PylontechAsciiHandler();
                                    needSwitch = true;
                                }
                                else if (det.Protocol == DetectedProtocolType.Cvte && !(ProtocolHandler is CvteModbusHandler))
                                {
                                    ProtocolHandler = new CvteModbusHandler();
                                    needSwitch = true;
                                }

                                bool modelChanged = (det.Model != InverterModel.Unknown && det.Model != _lastAdaptedModel);
                                if (modelChanged)
                                {
                                    _lastAdaptedModel = det.Model;
                                    // 在首帧构建应答前同步更新 Model 套系参数，防止首帧返回错误的 51.2V/25.6V 触发逆变器故障
                                    BmsDefaultConfig cfg = BmsDefaultConfig.Load();
                                    string targetModelName = det.Model == InverterModel.INV_3KU ? "3KU" : "6KU";
                                    if (cfg.ActiveProfileName != targetModelName)
                                    {
                                        cfg.ActiveProfileName = targetModelName;
                                        cfg.Save();
                                        Model.ApplyDefaultConfig(targetModelName == "6KU" ? cfg.Profile6KU : cfg.Profile3KU);
                                    }
                                }

                                if (needSwitch || modelChanged)
                                {
                                    if (OnAutoAdapted != null)
                                    {
                                        OnAutoAdapted(this, new AutoAdaptEventArgs(det.Protocol, det.Model));
                                    }
                                }
                            }

                            byte[] txResp;
                            string decodedInfo;
                            bool processed = ProtocolHandler.TryProcessQuery(currentFrame, currentFrame.Length, Model, ReplyAllIds, SpecificId, out txResp, out decodedInfo);

                            string rxSummary = (det != null && !string.IsNullOrEmpty(det.RawSignature)) ? det.RawSignature : (!processed && !string.IsNullOrEmpty(decodedInfo) ? decodedInfo : "");
                            if (OnLogEvent != null) OnLogEvent(this, new LogEventArgs(false, currentFrame, rxSummary));

                            if (processed)
                            {
                                if (SimulateSilence)
                                {
                                    swResp.Stop();
                                    if (OnLogEvent != null)
                                        OnLogEvent(this, new LogEventArgs(true, new byte[0], "[故障注入: 模拟断线静默，已抑制应答以测试逆变器失联保护]"));
                                }
                                else
                                {
                                    int delayMs = ExtraResponseDelayMs > 0 ? ExtraResponseDelayMs : 5;
                                    Thread.Sleep(delayMs);

                                    if (SimulateCorruptChecksum && txResp != null && txResp.Length >= 4)
                                    {
                                        if (txResp[0] == (byte)'~')
                                        {
                                            txResp[txResp.Length - 2] = (byte)(txResp[txResp.Length - 2] == (byte)'0' ? 'F' : '0');
                                        }
                                        else
                                        {
                                            txResp[txResp.Length - 1] ^= 0xFF;
                                        }
                                        decodedInfo = (decodedInfo ?? "") + " [故障注入: 校验码已故意破坏]";
                                    }

                                    lock (_portLock) { if (_port != null && _port.IsOpen) _port.Write(txResp, 0, txResp.Length); }
                                    swResp.Stop();
                                    TxCount++;
                                    if (OnLogEvent != null) OnLogEvent(this, new LogEventArgs(true, txResp, decodedInfo, swResp.ElapsedMilliseconds));
                                }
                            }
                            else
                            {
                                // 仅当非主动忽略（如其它从机地址）时才计入通信错误计数
                                if (decodedInfo == null || !decodedInfo.StartsWith("[忽略]"))
                                {
                                    ErrorCount++;
                                }
                            }
                            swIdle.Restart();
                        }
                        Thread.Sleep(1);
                    }
                    else
                    {
                        // ══════════════════════════════════════════════════════════
                        // 2. 主机模式：模拟逆变器轮询电池包与下发控制
                        // ══════════════════════════════════════════════════════════

                        // 2.1 优先处理在线设备扫描
                        if (_scanRequested)
                        {
                            _scanRequested = false;
                            _cancelScanRequested = false;
                            WasScanCancelled = false;
                            if (OnStatusChanged != null) OnStatusChanged(this, "正在扫描总线在线电池包 (ID 1 ~ 15, 0)...");
                            List<byte> foundPacks = new List<byte>();
                            BmsDataModel tempScanModel = new BmsDataModel();

                            // 优先扫描常见地址 1 ~ 15，最后扫描 0
                            byte[] scanList = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0 };
                            int totalSteps = scanList.Length;
                            for (int i = 0; i < totalSteps; i++)
                            {
                                if (_cancelScanRequested || !_isRunning || CurrentWorkMode != WorkMode.MasterPoller)
                                {
                                    WasScanCancelled = true;
                                    break;
                                }

                                byte scanId = scanList[i];
                                if (OnScanProgress != null)
                                {
                                    OnScanProgress(this, new ScanProgressEventArgs(i, totalSteps, scanId, new List<byte>(foundPacks)));
                                }

                                byte[] probe = ProtocolHandler.BuildMasterPollFrame(scanId, 0);
                                lock (_portLock)
                                {
                                    if (_port != null && _port.IsOpen)
                                    {
                                        _port.DiscardInBuffer();
                                        _port.Write(probe, 0, probe.Length);
                                    }
                                }
                                TxCount++;

                                // 等待从机响应 (280ms 充分满足 9600 波特率长报文传输时延)
                                Stopwatch swProbe = Stopwatch.StartNew();
                                byte[] probeBuf = new byte[512];
                                int probeLen = 0;

                                while (swProbe.ElapsedMilliseconds < 280)
                                {
                                    if (_cancelScanRequested || !_isRunning || CurrentWorkMode != WorkMode.MasterPoller)
                                    {
                                        WasScanCancelled = true;
                                        break;
                                    }

                                    int bRead = 0;
                                    lock (_portLock) { if (_port != null && _port.IsOpen) bRead = _port.BytesToRead; }
                                    if (bRead > 0)
                                    {
                                        lock (_portLock)
                                        {
                                            if (_port != null && _port.IsOpen)
                                                probeLen += _port.Read(probeBuf, probeLen, Math.Min(bRead, probeBuf.Length - probeLen));
                                        }

                                        if (probeLen >= 10)
                                        {
                                            if (ProtocolHandler is PylontechAsciiHandler)
                                            {
                                                int tildePos = -1;
                                                int crPos = -1;
                                                for (int p = 0; p < probeLen; p++)
                                                {
                                                    if (tildePos < 0 && probeBuf[p] == (byte)'~') tildePos = p;
                                                    if (tildePos >= 0 && probeBuf[p] == 0x0D) { crPos = p; break; }
                                                }
                                                if (tildePos >= 0 && crPos > tildePos)
                                                {
                                                    int sliceLen = crPos - tildePos + 1;
                                                    byte[] slice = new byte[sliceLen];
                                                    Array.Copy(probeBuf, tildePos, slice, 0, sliceLen);
                                                    if (PylonChecksum.Verify(slice))
                                                    {
                                                        foundPacks.Add(scanId);
                                                        RxCount++;
                                                        break;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                string dummy;
                                                if (ProtocolHandler.TryDecodeMasterResponse(probeBuf, probeLen, tempScanModel, out dummy))
                                                {
                                                    foundPacks.Add(scanId);
                                                    RxCount++;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                    Thread.Sleep(5);
                                }

                                if (OnScanProgress != null)
                                {
                                    OnScanProgress(this, new ScanProgressEventArgs(i + 1, totalSteps, scanId, new List<byte>(foundPacks)));
                                }
                            }

                            string scanSummary = foundPacks.Count > 0
                                ? string.Format("✓ 扫描完成，发现 {0} 个在线电池包: ID {1}", foundPacks.Count, string.Join(", ", foundPacks.ConvertAll<string>(delegate(byte b) { return b.ToString(); }).ToArray()))
                                : (WasScanCancelled ? "扫描已由用户手动取消" : "⚠️ 扫描完成，未探测到在线电池包");
                            if (OnStatusChanged != null) OnStatusChanged(this, scanSummary);
                            if (OnScanPacksCompleted != null) OnScanPacksCompleted(this, foundPacks);
                            swPoll.Restart();
                            swLastMasterRx.Restart();
                        }

                        // 2.2 优先发送队列中的主动控制帧 (如写参数、关机)，并与周期轮询互斥防总线碰撞
                        KeyValuePair<byte[], string> pendingCmd = default(KeyValuePair<byte[], string>);
                        bool hasCmd = false;
                        lock (_queueLock)
                        {
                            if (_pendingTxQueue.Count > 0)
                            {
                                pendingCmd = _pendingTxQueue.Dequeue();
                                hasCmd = true;
                            }
                        }

                        if (hasCmd && pendingCmd.Key != null)
                        {
                            lock (_portLock)
                            {
                                if (_port != null && _port.IsOpen)
                                    _port.Write(pendingCmd.Key, 0, pendingCmd.Key.Length);
                            }
                            TxCount++;
                            swPoll.Restart(); // 重置轮询计时器，留出完整应答窗口防止控制帧与轮询帧粘包碰撞
                            if (OnLogEvent != null)
                                OnLogEvent(this, new LogEventArgs(true, pendingCmd.Key, pendingCmd.Value));
                        }
                        else
                        {
                            // 2.3 周期轮询或单步轮询下发
                            bool shouldPoll = false;
                            if (_singlePollRequested)
                            {
                                _singlePollRequested = false;
                                shouldPoll = true;
                            }
                            else if (!IsPollingPaused && PollIntervalMs > 0 && swPoll.ElapsedMilliseconds >= PollIntervalMs)
                            {
                                shouldPoll = true;
                            }

                            if (shouldPoll)
                            {
                                swPoll.Restart();
                                int phaseCount = Math.Max(1, ProtocolHandler.MasterPollPhaseCount);
                                int currentPhaseDisplay = pollPhase + 1;
                                byte[] query = ProtocolHandler.BuildMasterPollFrame(TargetSlaveId, pollPhase);
                                pollPhase = (pollPhase + 1) % phaseCount;

                                lock (_portLock)
                                {
                                    if (_port != null && _port.IsOpen)
                                        _port.Write(query, 0, query.Length);
                                }
                                TxCount++;
                                if (OnLogEvent != null)
                                    OnLogEvent(this, new LogEventArgs(true, query, string.Format("[主机轮询] 发送查询 ID:{0} Phase:{1}/{2}", TargetSlaveId, currentPhaseDisplay, phaseCount)));
                            }
                        }

                        // 2.4 接收外部从机应答
                        int bytesToRead = 0;
                        lock (_portLock)
                        {
                            if (_port != null && _port.IsOpen)
                                bytesToRead = _port.BytesToRead;
                        }

                        if (bytesToRead > 0)
                        {
                            int read = 0;
                            lock (_portLock)
                            {
                                if (_port != null && _port.IsOpen)
                                    read = _port.Read(rxBuf, rxLen, Math.Min(bytesToRead, rxBuf.Length - rxLen));
                            }
                            if (read > 0) { rxLen += read; swIdle.Restart(); }
                        }

                        if (rxLen > 0)
                        {
                            bool ready = false;
                            if (ProtocolHandler is PylontechAsciiHandler)
                            {
                                // 消除 RS485 杂散前导噪声：确保以 ~ (0x7E) 开头
                                int tildeIdx = -1;
                                for (int i = 0; i < rxLen; i++)
                                {
                                    if (rxBuf[i] == (byte)'~') { tildeIdx = i; break; }
                                }
                                if (tildeIdx > 0)
                                {
                                    Array.Copy(rxBuf, tildeIdx, rxBuf, 0, rxLen - tildeIdx);
                                    rxLen -= tildeIdx;
                                }

                                // 完整报文必须以 \r (0x0D) 结尾，或静默等待 120ms 防分包截断
                                if (rxLen >= 10 && rxBuf[rxLen - 1] == 0x0D)
                                {
                                    ready = true;
                                }
                                else if (swIdle.ElapsedMilliseconds >= 120 && rxLen >= 10)
                                {
                                    ready = true;
                                }
                            }
                            else
                            {
                                // 消除 Modbus 主机接收时的 RS485 前导杂散噪声：扫描合法 CRC 子帧
                                int validMbOffset = -1;
                                int validMbLen = 0;
                                for (int i = 0; i <= rxLen - 5; i++)
                                {
                                    byte fc = rxBuf[i + 1];
                                    if (fc == 0x03 && i + 2 < rxLen)
                                    {
                                        int candidateLen = 3 + rxBuf[i + 2] + 2;
                                        if (candidateLen >= 5 && i + candidateLen <= rxLen && ModbusCrc.Check(rxBuf, candidateLen, i))
                                        {
                                            validMbOffset = i;
                                            validMbLen = candidateLen;
                                            break;
                                        }
                                    }
                                    else if ((fc == 0x06 || fc == 0x10) && i + 8 <= rxLen)
                                    {
                                        if (ModbusCrc.Check(rxBuf, 8, i))
                                        {
                                            validMbOffset = i;
                                            validMbLen = 8;
                                            break;
                                        }
                                    }
                                }

                                if (validMbOffset >= 0)
                                {
                                    if (validMbOffset > 0)
                                    {
                                        Array.Copy(rxBuf, validMbOffset, rxBuf, 0, rxLen - validMbOffset);
                                        rxLen -= validMbOffset;
                                    }
                                    ready = true;
                                }
                                else if (swIdle.ElapsedMilliseconds >= 25)
                                {
                                    ready = true;
                                }
                            }

                            if (ready)
                            {
                                RxCount++;
                                byte[] currentFrame = new byte[rxLen];
                                Array.Copy(rxBuf, currentFrame, rxLen);
                                rxLen = 0;

                                string decoded;
                                if (ProtocolHandler.TryDecodeMasterResponse(currentFrame, currentFrame.Length, Model, out decoded))
                                {
                                    LastResponseTimeMs = swIdle.ElapsedMilliseconds;
                                    swLastMasterRx.Restart();
                                    IsBmsLost = false;

                                    if (OnLogEvent != null) OnLogEvent(this, new LogEventArgs(false, currentFrame, decoded));
                                    if (OnMasterTelemetryUpdated != null) OnMasterTelemetryUpdated(this, Model);
                                }
                                else
                                {
                                    ErrorCount++;
                                    if (OnLogEvent != null) OnLogEvent(this, new LogEventArgs(false, currentFrame, string.IsNullOrEmpty(decoded) ? "[从机校验失败或未知响应]" : decoded));
                                }
                            }
                        }

                        // 2.5 逆变器链路健康度诊断与 BMS 失联保护超时判定
                        if (!IsPollingPaused && PollIntervalMs > 0 && swLastMasterRx.ElapsedMilliseconds > 3000 && TxCount > 0)
                        {
                            if (!IsBmsLost)
                            {
                                IsBmsLost = true;
                                if (OnStatusChanged != null)
                                    OnStatusChanged(this, string.Format("⚠️ [主机模式] BMS 失联警告: 目标 ID:{0} 超过 {1:F1}s 无应答", TargetSlaveId, swLastMasterRx.ElapsedMilliseconds / 1000.0));
                            }
                        }

                        Thread.Sleep(5);
                    }
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch (Exception)
                {
                    ErrorCount++;
                    Thread.Sleep(10);
                }
            }
        }
    }
}
