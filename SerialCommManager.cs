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

    public class PortDisconnectedEventArgs : EventArgs
    {
        public int SessionId { get; private set; }
        public string PortName { get; private set; }
        public string Reason { get; private set; }

        public PortDisconnectedEventArgs(int sessionId, string portName, string reason)
        {
            SessionId = sessionId;
            PortName = portName;
            Reason = reason;
        }
    }

    public class SerialCommManager
    {
        private sealed class WorkerSession
        {
            public readonly int SessionId;
            public readonly string PortName;
            public readonly SerialPort Port;

            public WorkerSession(int sessionId, string portName, SerialPort port)
            {
                SessionId = sessionId;
                PortName = portName;
                Port = port;
            }
        }

        private volatile SerialPort _port;
        private Thread _workerThread;
        private volatile bool _isRunning = false;
        private int _sessionId = 0;
        private readonly object _lifecycleLock = new object();

        private volatile bool _singlePollRequested = false;
        private volatile bool _scanRequested = false;
        private volatile bool _cancelScanRequested = false;
        private volatile bool _contextResetRequested = false;
        public bool WasScanCancelled { get; private set; }
        public bool WasScanAbortedByDisconnect { get; private set; }
        public string CurrentPortName { get; private set; }

        public BmsDataModel Model { get; private set; }
        public IBmsProtocolHandler ProtocolHandler { get; set; }
        public WorkMode CurrentWorkMode { get; set; }
        public bool ReplyAllIds { get; set; }
        public int SpecificId { get; set; }

        // ── 智能协议识别与自适应 ──
        public InverterDetector Detector { get; private set; }
        public bool AutoAdaptEnabled { get; set; }

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
        public event EventHandler<AutoAdaptEventArgs> OnAutoAdapted;
        public event EventHandler<PortDisconnectedEventArgs> OnPortDisconnected;

        public bool IsRunning
        {
            get { return _isRunning; }
        }

        public bool IsOpen
        {
            get
            {
                SerialPort p = _port;
                return _isRunning && p != null && p.IsOpen;
            }
        }

        public bool IsPhysicalPortOpen
        {
            get
            {
                SerialPort p = _port;
                return p != null && p.IsOpen;
            }
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

        private static void SafeClosePortBounded(SerialPort portToClose, int maxWaitMs)
        {
            if (portToClose == null) return;
            object gate = new object();
            bool completed = false;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { portToClose.Close(); } catch { }
                try { portToClose.Dispose(); } catch { }
                lock (gate)
                {
                    completed = true;
                    Monitor.PulseAll(gate);
                }
            });
            if (maxWaitMs > 0)
            {
                lock (gate)
                {
                    if (!completed)
                    {
                        Monitor.Wait(gate, maxWaitMs);
                    }
                }
            }
        }

        public bool Open(string portName, int baudRate, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One)
        {
            CloseInternal(false);
            SerialPort newPort = null;
            try
            {
                newPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
                newPort.ReadTimeout = 50;
                newPort.WriteTimeout = 200;
                newPort.Open();

                WorkerSession session;
                lock (_lifecycleLock)
                {
                    int sid = ++_sessionId;
                    CurrentPortName = portName;
                    _port = newPort;
                    _isRunning = true;
                    _singlePollRequested = false;
                    _scanRequested = false;
                    _cancelScanRequested = false;
                    _contextResetRequested = false;
                    WasScanCancelled = false;
                    WasScanAbortedByDisconnect = false;
                    IsBmsLost = false;
                    if (Detector != null) Detector.Reset();

                    session = new WorkerSession(sid, portName, newPort);
                    _workerThread = new Thread(WorkerLoop);
                    _workerThread.IsBackground = true;
                    _workerThread.Priority = ThreadPriority.AboveNormal;
                    _workerThread.Name = "BMS_SerialWorker";
                    _workerThread.Start(session);
                }

                if (OnStatusChanged != null)
                    OnStatusChanged(this, string.Format("串口 {0} 已打开 ({1} 8-N-1)", portName, baudRate));

                return true;
            }
            catch (Exception ex)
            {
                SafeClosePortBounded(newPort, 50);
                if (OnStatusChanged != null)
                    OnStatusChanged(this, "打开串口失败: " + ex.Message);
                return false;
            }
        }

        public void Close()
        {
            CloseInternal(true);
        }

        private void CloseInternal(bool notifyStatus)
        {
            bool wasRunning;
            SerialPort portToClose;
            Thread worker;

            lock (_lifecycleLock)
            {
                wasRunning = _isRunning;
                _isRunning = false;
                _cancelScanRequested = true;
                _sessionId++;
                portToClose = _port;
                _port = null;
                CurrentPortName = null;
                worker = _workerThread;
                _workerThread = null;
            }

            SafeClosePortBounded(portToClose, 150);

            if (worker != null && worker != Thread.CurrentThread)
            {
                try { worker.Join(120); } catch { }
            }

            if (notifyStatus && wasRunning && OnStatusChanged != null)
                OnStatusChanged(this, "串口已关闭");
        }

        public void NotifyContextReset()
        {
            _contextResetRequested = true;
        }

        public void NotifyExternalPortLost(string reason)
        {
            int sid;
            string portName;
            SerialPort portToClose;

            lock (_lifecycleLock)
            {
                if (!_isRunning) return;
                sid = _sessionId;
                _sessionId++;
                _isRunning = false;
                _cancelScanRequested = true;
                portName = CurrentPortName;
                CurrentPortName = null;
                portToClose = _port;
                _port = null;
                _workerThread = null;
            }

            SafeClosePortBounded(portToClose, 50);

            string msg = string.IsNullOrEmpty(reason)
                ? string.Format("串口 {0} 已拔出或失去连接", portName ?? "")
                : reason;

            if (OnStatusChanged != null)
                OnStatusChanged(this, "⚠️ " + msg);
            if (OnPortDisconnected != null)
                OnPortDisconnected(this, new PortDisconnectedEventArgs(sid, portName, msg));
        }

        private void HandleUnexpectedDisconnect(WorkerSession session, string reason)
        {
            if (session == null) return;
            lock (_lifecycleLock)
            {
                if (!_isRunning || _sessionId != session.SessionId) return;
                _isRunning = false;
                _cancelScanRequested = true;
                _sessionId++;
                CurrentPortName = null;
                _port = null;
                _workerThread = null;
            }

            SafeClosePortBounded(session.Port, 0);

            if (OnStatusChanged != null)
                OnStatusChanged(this, "⚠️ " + reason);
            if (OnPortDisconnected != null)
                OnPortDisconnected(this, new PortDisconnectedEventArgs(session.SessionId, session.PortName, reason));
        }

        private bool IsSessionActive(WorkerSession session)
        {
            return session != null && _isRunning && _sessionId == session.SessionId;
        }

        private bool CheckPortAlive(WorkerSession session)
        {
            if (!IsSessionActive(session)) return false;
            if (session.Port == null || !session.Port.IsOpen)
            {
                HandleUnexpectedDisconnect(session, string.Format("串口 {0} 已断开或设备已移除", session.PortName));
                return false;
            }
            return true;
        }

        private bool TryGetBytesToRead(WorkerSession session, out int bytesToRead)
        {
            bytesToRead = 0;
            if (!CheckPortAlive(session)) return false;
            try
            {
                bytesToRead = session.Port.BytesToRead;
                return true;
            }
            catch (TimeoutException)
            {
                return true;
            }
            catch (Exception ex)
            {
                if (IsSessionActive(session))
                    HandleUnexpectedDisconnect(session, string.Format("串口 {0} 读取状态失败: {1}", session.PortName, ex.Message));
                return false;
            }
        }

        private bool TryReadPort(WorkerSession session, byte[] buffer, int offset, int count, out int read)
        {
            read = 0;
            if (!CheckPortAlive(session)) return false;
            if (count <= 0) return true;
            try
            {
                read = session.Port.Read(buffer, offset, count);
                return true;
            }
            catch (TimeoutException)
            {
                read = 0;
                return true;
            }
            catch (Exception ex)
            {
                if (IsSessionActive(session))
                    HandleUnexpectedDisconnect(session, string.Format("串口 {0} 接收中断: {1}", session.PortName, ex.Message));
                return false;
            }
        }

        private bool TryWritePort(WorkerSession session, byte[] buffer, int count)
        {
            if (!CheckPortAlive(session)) return false;
            if (buffer == null || count <= 0) return false;
            try
            {
                session.Port.Write(buffer, 0, count);
                return true;
            }
            catch (TimeoutException)
            {
                ErrorCount++;
                return false;
            }
            catch (Exception ex)
            {
                if (IsSessionActive(session))
                    HandleUnexpectedDisconnect(session, string.Format("串口 {0} 发送中断: {1}", session.PortName, ex.Message));
                return false;
            }
        }

        private bool TryDiscardInBuffer(WorkerSession session)
        {
            if (!CheckPortAlive(session)) return false;
            try
            {
                session.Port.DiscardInBuffer();
                return true;
            }
            catch (TimeoutException)
            {
                return true;
            }
            catch (Exception ex)
            {
                if (IsSessionActive(session))
                    HandleUnexpectedDisconnect(session, string.Format("串口 {0} 清空缓冲失败: {1}", session.PortName, ex.Message));
                return false;
            }
        }

        public void ResetStatistics()
        {
            RxCount = 0;
            TxCount = 0;
            ErrorCount = 0;
            IsBmsLost = false;
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
            WasScanAbortedByDisconnect = false;
            _cancelScanRequested = false;
            _scanRequested = true;
        }

        // ── 主机模式：取消在线电池包扫描 ──
        public void CancelScan()
        {
            _cancelScanRequested = true;
            WasScanCancelled = true;
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

        private static void ShiftBuffer(byte[] buf, ref int totalLen, int consumeCount)
        {
            if (consumeCount <= 0) return;
            if (consumeCount >= totalLen)
            {
                totalLen = 0;
                return;
            }
            Array.Copy(buf, consumeCount, buf, 0, totalLen - consumeCount);
            totalLen -= consumeCount;
        }

        internal static byte[] TryExtractMasterResponseFrame(byte[] rxBuf, ref int rxLen, IBmsProtocolHandler handler, long idleMs)
        {
            if (rxBuf == null || rxLen <= 0 || handler == null) return null;

            if (handler is PylontechAsciiHandler)
            {
                while (rxLen > 0)
                {
                    int tildeIdx = -1;
                    for (int i = 0; i < rxLen; i++)
                    {
                        if (rxBuf[i] == (byte)'~') { tildeIdx = i; break; }
                    }
                    if (tildeIdx < 0)
                    {
                        if (idleMs >= 120 || rxLen >= 256)
                            rxLen = 0;
                        return null;
                    }
                    if (tildeIdx > 0)
                    {
                        ShiftBuffer(rxBuf, ref rxLen, tildeIdx);
                    }

                    int eoiIdx = -1;
                    for (int i = 0; i < rxLen; i++)
                    {
                        if (rxBuf[i] == 0x0D) { eoiIdx = i; break; }
                    }

                    if (eoiIdx >= 0)
                    {
                        int frameLen = eoiIdx + 1;
                        if (frameLen >= 10)
                        {
                            byte[] frame = new byte[frameLen];
                            Array.Copy(rxBuf, 0, frame, 0, frameLen);
                            ShiftBuffer(rxBuf, ref rxLen, frameLen);
                            return frame;
                        }
                        ShiftBuffer(rxBuf, ref rxLen, frameLen);
                        continue;
                    }

                    if (idleMs >= 120)
                    {
                        if (rxLen >= 10)
                        {
                            byte[] frame = new byte[rxLen];
                            Array.Copy(rxBuf, 0, frame, 0, rxLen);
                            rxLen = 0;
                            return frame;
                        }
                        rxLen = 0;
                    }
                    return null;
                }
                return null;
            }

            if (handler is JbdBinaryHandler)
            {
                int ddIdx = -1;
                int jbdRespLen = 0;
                for (int i = 0; i <= rxLen - 7; i++)
                {
                    if (rxBuf[i] == 0xDD)
                    {
                        int coreLen;
                        if (JbdChecksum.VerifyResponse(rxBuf, i, rxLen - i, out coreLen))
                        {
                            ddIdx = i;
                            jbdRespLen = coreLen;
                            break;
                        }
                    }
                }

                if (ddIdx >= 0)
                {
                    if (ddIdx > 0) ShiftBuffer(rxBuf, ref rxLen, ddIdx);
                    byte[] frame = new byte[jbdRespLen];
                    Array.Copy(rxBuf, 0, frame, 0, jbdRespLen);
                    ShiftBuffer(rxBuf, ref rxLen, jbdRespLen);
                    return frame;
                }

                if (idleMs >= 40)
                {
                    if (rxLen >= 7)
                    {
                        byte[] frame = new byte[rxLen];
                        Array.Copy(rxBuf, 0, frame, 0, rxLen);
                        rxLen = 0;
                        return frame;
                    }
                    if (idleMs >= 120)
                    {
                        rxLen = 0;
                    }
                }
                return null;
            }

            // Modbus RTU 与 Voltronic 4 字节长度头精确切片提取
            int validMbOffset = -1;
            int validMbLen = 0;
            for (int i = 0; i <= rxLen - 5; i++)
            {
                byte fc = rxBuf[i + 1];
                if ((fc == 0x03 || fc == 0x04 || fc == 0x11) && i + 2 < rxLen && rxBuf[i + 2] > 0)
                {
                    int candidateLen = 3 + rxBuf[i + 2] + 2;
                    if (candidateLen >= 5 && i + candidateLen <= rxLen && ModbusCrc.Check(rxBuf, candidateLen, i))
                    {
                        validMbOffset = i;
                        validMbLen = candidateLen;
                        break;
                    }
                }
                else if (fc == 0x03 && i + 3 < rxLen && rxBuf[i + 2] == 0x00 && rxBuf[i + 3] >= 1 && rxBuf[i + 3] <= 125)
                {
                    int vCandidateLen = 4 + rxBuf[i + 3] * 2 + 2;
                    if (i + vCandidateLen <= rxLen && ModbusCrc.Check(rxBuf, vCandidateLen, i))
                    {
                        validMbOffset = i;
                        validMbLen = vCandidateLen;
                        break;
                    }
                }
                else if ((fc & 0x80) != 0 && i + 5 <= rxLen)
                {
                    if (ModbusCrc.Check(rxBuf, 5, i))
                    {
                        validMbOffset = i;
                        validMbLen = 5;
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
                if (validMbOffset > 0) ShiftBuffer(rxBuf, ref rxLen, validMbOffset);
                byte[] frame = new byte[validMbLen];
                Array.Copy(rxBuf, 0, frame, 0, validMbLen);
                ShiftBuffer(rxBuf, ref rxLen, validMbLen);
                return frame;
            }

            if (idleMs >= 25)
            {
                if (rxLen >= 5)
                {
                    byte[] frame = new byte[rxLen];
                    Array.Copy(rxBuf, 0, frame, 0, rxLen);
                    rxLen = 0;
                    return frame;
                }
                if (idleMs >= 100)
                {
                    rxLen = 0;
                }
            }
            return null;
        }

        private void WorkerLoop(object state)
        {
            WorkerSession session = state as WorkerSession;
            if (session == null) return;

            byte[] rxBuf = new byte[1024];
            int rxLen = 0;
            Stopwatch swIdle = Stopwatch.StartNew();
            Stopwatch swPoll = Stopwatch.StartNew();
            Stopwatch swLastMasterRx = Stopwatch.StartNew();
            int pollPhase = 0;

            WorkMode lastMode = CurrentWorkMode;
            IBmsProtocolHandler lastHandler = ProtocolHandler;
            byte lastTargetId = TargetSlaveId;
            bool wasPaused = IsPollingPaused;
            bool masterPollSentSinceReset = false;

            while (IsSessionActive(session))
            {
                try
                {
                    if (!CheckPortAlive(session))
                    {
                        break;
                    }

                    if (_contextResetRequested || CurrentWorkMode != lastMode ||
                        !object.ReferenceEquals(ProtocolHandler, lastHandler) ||
                        TargetSlaveId != lastTargetId || (wasPaused && !IsPollingPaused))
                    {
                        _contextResetRequested = false;
                        lastMode = CurrentWorkMode;
                        lastHandler = ProtocolHandler;
                        lastTargetId = TargetSlaveId;
                        rxLen = 0;
                        pollPhase = 0;
                        IsBmsLost = false;
                        masterPollSentSinceReset = false;
                        swLastMasterRx.Restart();
                        swPoll.Restart();
                        swIdle.Restart();
                        if (!TryDiscardInBuffer(session)) break;
                    }
                    wasPaused = IsPollingPaused;

                    if (CurrentWorkMode == WorkMode.SlaveSimulator)
                    {
                        // ══════════════════════════════════════════════════════════
                        // 1. 从机模式：高速监听、智能机型协议识别与秒级响应逆变器查询
                        // ══════════════════════════════════════════════════════════
                        int bytesToRead;
                        if (!TryGetBytesToRead(session, out bytesToRead)) break;

                        if (bytesToRead > 0)
                        {
                            int spaceLeft = rxBuf.Length - rxLen;
                            if (spaceLeft <= 0)
                            {
                                ShiftBuffer(rxBuf, ref rxLen, 256);
                                spaceLeft = rxBuf.Length - rxLen;
                            }
                            if (spaceLeft > 0)
                            {
                                int read;
                                if (!TryReadPort(session, rxBuf, rxLen, Math.Min(bytesToRead, spaceLeft), out read)) break;
                                if (read > 0)
                                {
                                    rxLen += read;
                                    swIdle.Restart();
                                }
                            }
                        }

                        // 分包解帧检测与超时处理 (外置于 bytesToRead，确保静默超时也能解出帧或清理残包)
                        while (rxLen > 0 && IsSessionActive(session))
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

                            // 2. 查找合法的 Modbus RTU 查询帧 (8 字节 0x03/0x04 或 4 字节 0x11 读设备信息)
                            int mbIdx = -1;
                            int mbFrameLen = 0;
                            for (int i = 0; i < rxLen; i++)
                            {
                                if (i <= rxLen - 8 && (rxBuf[i + 1] == 0x03 || rxBuf[i + 1] == 0x04) && ModbusCrc.Check(rxBuf, 8, i))
                                {
                                    mbIdx = i;
                                    mbFrameLen = 8;
                                    break;
                                }
                                if (i <= rxLen - 4 && rxBuf[i] <= 15 && rxBuf[i + 1] == 0x11 && ModbusCrc.Check(rxBuf, 4, i))
                                {
                                    mbIdx = i;
                                    mbFrameLen = 4;
                                    break;
                                }
                            }

                            // 3. 查找合法的 JBD V12 查询帧 (0xDD 起始，0x77 结束，可带 0~4 字节透传 CALLBACK_ID)
                            int jbdIdx = -1;
                            int jbdFrameLen = 0;
                            for (int i = 0; i < rxLen; i++)
                            {
                                if (rxBuf[i] == 0xDD)
                                {
                                    int coreLen, cbLen;
                                    if (JbdChecksum.VerifyRequest(rxBuf, i, rxLen - i, out coreLen, out cbLen))
                                    {
                                        if (rxLen - i == coreLen && swIdle.ElapsedMilliseconds < 15)
                                        {
                                            break;
                                        }
                                        jbdIdx = i;
                                        jbdFrameLen = coreLen + cbLen;
                                        break;
                                    }
                                }
                            }

                            // 比较哪个合法协议帧最先出现，优先提取最先到达的完整帧
                            int earliest = -1;
                            int earliestType = 0; // 1: Modbus, 2: Pylon, 3: JBD

                            if (mbIdx >= 0) { earliest = mbIdx; earliestType = 1; }
                            if (soiIndex >= 0 && (earliest < 0 || soiIndex < earliest)) { earliest = soiIndex; earliestType = 2; }
                            if (jbdIdx >= 0 && (earliest < 0 || jbdIdx < earliest)) { earliest = jbdIdx; earliestType = 3; }

                            if (earliestType == 1) // Modbus RTU
                            {
                                if (mbIdx > 0) ShiftBuffer(rxBuf, ref rxLen, mbIdx);
                                currentFrame = new byte[mbFrameLen];
                                Array.Copy(rxBuf, 0, currentFrame, 0, mbFrameLen);
                                ShiftBuffer(rxBuf, ref rxLen, mbFrameLen);
                            }
                            else if (earliestType == 3) // JBD V12
                            {
                                if (jbdIdx > 0) ShiftBuffer(rxBuf, ref rxLen, jbdIdx);
                                currentFrame = new byte[jbdFrameLen];
                                Array.Copy(rxBuf, 0, currentFrame, 0, jbdFrameLen);
                                ShiftBuffer(rxBuf, ref rxLen, jbdFrameLen);
                            }
                            else if (earliestType == 2 || soiIndex >= 0)
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
                                        ShiftBuffer(rxBuf, ref rxLen, soiIndex);
                                        eoiIndex -= soiIndex;
                                    }

                                    int frameLen = eoiIndex + 1;
                                    currentFrame = new byte[frameLen];
                                    Array.Copy(rxBuf, 0, currentFrame, 0, frameLen);
                                    ShiftBuffer(rxBuf, ref rxLen, frameLen);
                                }
                                else if (soiIndex > 0)
                                {
                                    ShiftBuffer(rxBuf, ref rxLen, soiIndex);
                                }
                                else if (swIdle.ElapsedMilliseconds >= 250)
                                {
                                    ShiftBuffer(rxBuf, ref rxLen, 1);
                                    swIdle.Restart();
                                }
                            }
                            else if (rxLen > 0 && rxBuf[0] == 0xDD && swIdle.ElapsedMilliseconds >= 250)
                            {
                                ShiftBuffer(rxBuf, ref rxLen, 1);
                                swIdle.Restart();
                            }
                            else if (rxLen > 0 && swIdle.ElapsedMilliseconds >= 35)
                            {
                                currentFrame = new byte[rxLen];
                                Array.Copy(rxBuf, 0, currentFrame, 0, rxLen);
                                rxLen = 0;
                            }

                            if (currentFrame == null)
                            {
                                if (rxLen >= 800)
                                {
                                    ShiftBuffer(rxBuf, ref rxLen, 400);
                                }
                                break;
                            }

                            RxCount++;
                            Stopwatch swResp = Stopwatch.StartNew();

                            // ── 智能机型与协议识别 ──
                            DetectionResult det = Detector.InspectFrame(currentFrame, 0, currentFrame.Length);

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
                                else if (det.Protocol == DetectedProtocolType.LibModbus && !(ProtocolHandler is LibModbusHandler))
                                {
                                    ProtocolHandler = new LibModbusHandler();
                                    needSwitch = true;
                                }
                                else if (det.Protocol == DetectedProtocolType.Jbd && !(ProtocolHandler is JbdBinaryHandler))
                                {
                                    ProtocolHandler = new JbdBinaryHandler();
                                    needSwitch = true;
                                }
                                if (needSwitch)
                                {
                                    lastHandler = ProtocolHandler;
                                    if (OnAutoAdapted != null)
                                    {
                                        OnAutoAdapted(this, new AutoAdaptEventArgs(det.Protocol, InverterModel.Unknown));
                                    }
                                }
                            }

                            IBmsProtocolHandler activeSlaveHandler = ProtocolHandler;
                            byte[] txResp;
                            string decodedInfo;
                            bool processed = activeSlaveHandler.TryProcessQuery(currentFrame, currentFrame.Length, Model, ReplyAllIds, SpecificId, out txResp, out decodedInfo);

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
                                    int slept = 0;
                                    while (slept < delayMs && IsSessionActive(session))
                                    {
                                        int step = Math.Min(20, delayMs - slept);
                                        Thread.Sleep(step);
                                        slept += step;
                                    }
                                    if (!IsSessionActive(session)) break;

                                    if (SimulateCorruptChecksum && txResp != null && txResp.Length >= 4)
                                    {
                                        if (txResp[0] == (byte)'~')
                                        {
                                            txResp[txResp.Length - 2] = (byte)(txResp[txResp.Length - 2] == (byte)'0' ? 'F' : '0');
                                        }
                                        else if (txResp[0] == 0xDD && txResp.Length >= 7)
                                        {
                                            // JBD 响应帧: 破坏 0x77 停止位前面的校验码字节，保持 0x77 停止位完好
                                            int chkOffset = 4 + txResp[3];
                                            if (chkOffset < txResp.Length)
                                            {
                                                txResp[chkOffset] ^= 0xFF;
                                            }
                                        }
                                        else
                                        {
                                            txResp[txResp.Length - 1] ^= 0xFF;
                                        }
                                        decodedInfo = (decodedInfo ?? "") + " [故障注入: 校验码已故意破坏]";
                                    }

                                    if (TryWritePort(session, txResp, txResp.Length))
                                    {
                                        swResp.Stop();
                                        TxCount++;
                                        if (OnLogEvent != null) OnLogEvent(this, new LogEventArgs(true, txResp, decodedInfo, swResp.ElapsedMilliseconds));
                                    }
                                    else if (!IsSessionActive(session))
                                    {
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                // 仅当非主动忽略（如其它从机地址）时才计入通信错误计数
                                bool isIgnored = decodedInfo != null && (decodedInfo.StartsWith("[忽略]") || decodedInfo.Contains("已按单包规则忽略"));
                                if (!isIgnored)
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
                        IBmsProtocolHandler masterHandler = ProtocolHandler;

                        // 2.1 优先处理在线设备扫描
                        if (_scanRequested)
                        {
                            _scanRequested = false;
                            _cancelScanRequested = false;
                            WasScanCancelled = false;
                            WasScanAbortedByDisconnect = false;
                            List<byte> foundPacks = new List<byte>();

                            try
                            {
                                if (OnStatusChanged != null) OnStatusChanged(this, "正在扫描总线在线电池包 (ID 1 ~ 15, 0)...");
                                BmsDataModel tempScanModel = new BmsDataModel();

                                // 优先扫描常见地址 1 ~ 15，最后扫描 0
                                byte[] scanList = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0 };
                                int totalSteps = scanList.Length;
                                for (int i = 0; i < totalSteps; i++)
                                {
                                    if (_cancelScanRequested || !IsSessionActive(session) || CurrentWorkMode != WorkMode.MasterPoller)
                                    {
                                        if (!IsSessionActive(session)) WasScanAbortedByDisconnect = true;
                                        WasScanCancelled = true;
                                        break;
                                    }

                                    byte scanId = scanList[i];
                                    if (OnScanProgress != null)
                                    {
                                        OnScanProgress(this, new ScanProgressEventArgs(i, totalSteps, scanId, new List<byte>(foundPacks)));
                                    }

                                    byte[] probe = masterHandler.BuildMasterPollFrame(scanId, 0);
                                    if (!TryDiscardInBuffer(session))
                                    {
                                        WasScanAbortedByDisconnect = true;
                                        WasScanCancelled = true;
                                        break;
                                    }
                                    if (!TryWritePort(session, probe, probe.Length))
                                    {
                                        if (!IsSessionActive(session))
                                        {
                                            WasScanAbortedByDisconnect = true;
                                            WasScanCancelled = true;
                                            break;
                                        }
                                        continue;
                                    }
                                    TxCount++;

                                    // 等待从机响应 (280ms 充分满足 9600 波特率长报文传输时延)
                                    Stopwatch swProbe = Stopwatch.StartNew();
                                    byte[] probeBuf = new byte[512];
                                    int probeLen = 0;

                                    while (swProbe.ElapsedMilliseconds < 280)
                                    {
                                        if (_cancelScanRequested || !IsSessionActive(session) || CurrentWorkMode != WorkMode.MasterPoller)
                                        {
                                            if (!IsSessionActive(session)) WasScanAbortedByDisconnect = true;
                                            WasScanCancelled = true;
                                            break;
                                        }

                                        int bRead;
                                        if (!TryGetBytesToRead(session, out bRead))
                                        {
                                            WasScanAbortedByDisconnect = true;
                                            WasScanCancelled = true;
                                            break;
                                        }
                                        if (bRead > 0)
                                        {
                                            int space = probeBuf.Length - probeLen;
                                            if (space <= 0)
                                            {
                                                ShiftBuffer(probeBuf, ref probeLen, 128);
                                                space = probeBuf.Length - probeLen;
                                            }
                                            int readBytes;
                                            if (!TryReadPort(session, probeBuf, probeLen, Math.Min(bRead, space), out readBytes))
                                            {
                                                WasScanAbortedByDisconnect = true;
                                                WasScanCancelled = true;
                                                break;
                                            }
                                            probeLen += readBytes;

                                            if (probeLen >= 10)
                                            {
                                                if (masterHandler is PylontechAsciiHandler)
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
                                                    if (masterHandler.TryDecodeMasterResponse(probeBuf, probeLen, tempScanModel, out dummy))
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

                                    if (WasScanAbortedByDisconnect || !IsSessionActive(session)) break;

                                    if (OnScanProgress != null)
                                    {
                                        OnScanProgress(this, new ScanProgressEventArgs(i + 1, totalSteps, scanId, new List<byte>(foundPacks)));
                                    }
                                }
                            }
                            finally
                            {
                                if (!IsSessionActive(session))
                                {
                                    WasScanAbortedByDisconnect = true;
                                    WasScanCancelled = true;
                                }
                                else if (OnStatusChanged != null)
                                {
                                    string scanSummary = foundPacks.Count > 0
                                        ? string.Format("✓ 扫描完成，发现 {0} 个在线电池包: ID {1}", foundPacks.Count, string.Join(", ", foundPacks.ConvertAll<string>(delegate(byte b) { return b.ToString(); }).ToArray()))
                                        : (WasScanCancelled ? "扫描已由用户手动取消" : "⚠️ 扫描完成，未探测到在线电池包");
                                    OnStatusChanged(this, scanSummary);
                                }
                                if (OnScanPacksCompleted != null) OnScanPacksCompleted(this, foundPacks);
                                rxLen = 0;
                                masterPollSentSinceReset = false;
                                swPoll.Restart();
                                swLastMasterRx.Restart();
                            }
                            if (!IsSessionActive(session)) break;
                        }

                        // 2.2 周期轮询或单步轮询下发
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
                            int phaseCount = Math.Max(1, masterHandler.MasterPollPhaseCount);
                            pollPhase = pollPhase % phaseCount;
                            int currentPhaseDisplay = pollPhase + 1;
                            byte[] query = masterHandler.BuildMasterPollFrame(TargetSlaveId, pollPhase);
                            pollPhase = (pollPhase + 1) % phaseCount;

                            if (TryWritePort(session, query, query.Length))
                            {
                                TxCount++;
                                masterPollSentSinceReset = true;
                                if (OnLogEvent != null)
                                    OnLogEvent(this, new LogEventArgs(true, query, string.Format("[主机轮询] 发送查询 ID:{0} Phase:{1}/{2}", TargetSlaveId, currentPhaseDisplay, phaseCount)));
                            }
                            else if (!IsSessionActive(session))
                            {
                                break;
                            }
                        }

                        // 2.4 接收外部从机应答
                        int bytesToRead;
                        if (!TryGetBytesToRead(session, out bytesToRead)) break;

                        if (bytesToRead > 0)
                        {
                            int spaceLeft = rxBuf.Length - rxLen;
                            if (spaceLeft <= 0)
                            {
                                ShiftBuffer(rxBuf, ref rxLen, 256);
                                spaceLeft = rxBuf.Length - rxLen;
                            }
                            if (spaceLeft > 0)
                            {
                                int read;
                                if (!TryReadPort(session, rxBuf, rxLen, Math.Min(bytesToRead, spaceLeft), out read)) break;
                                if (read > 0)
                                {
                                    rxLen += read;
                                    swIdle.Restart();
                                }
                            }
                        }

                        while (rxLen > 0 && IsSessionActive(session))
                        {
                            byte[] currentFrame = TryExtractMasterResponseFrame(rxBuf, ref rxLen, masterHandler, swIdle.ElapsedMilliseconds);
                            if (currentFrame == null)
                            {
                                if (rxLen >= 800)
                                {
                                    ShiftBuffer(rxBuf, ref rxLen, 400);
                                }
                                break;
                            }

                            RxCount++;
                            string decoded;
                            if (masterHandler.TryDecodeMasterResponse(currentFrame, currentFrame.Length, Model, out decoded))
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
                            swIdle.Restart();
                        }

                        // 2.5 逆变器链路健康度诊断与 BMS 失联保护超时判定
                        if (!IsPollingPaused && PollIntervalMs > 0 && masterPollSentSinceReset && swLastMasterRx.ElapsedMilliseconds > 3000)
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
                catch (Exception ex)
                {
                    if (!IsSessionActive(session)) break;
                    if (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ObjectDisposedException)
                    {
                        HandleUnexpectedDisconnect(session, string.Format("串口 {0} 通信异常断开: {1}", session.PortName, ex.Message));
                        break;
                    }
                    ErrorCount++;
                    Thread.Sleep(10);
                }
            }
        }
    }
}
