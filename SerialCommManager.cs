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

        public BmsDataModel Model { get; private set; }
        public IBmsProtocolHandler ProtocolHandler { get; set; }
        public WorkMode CurrentWorkMode { get; set; }
        public bool ReplyAllIds { get; set; }
        public int SpecificId { get; set; }

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
        }

        // ── 主机模式：触发单次读取 ──
        public void TriggerSinglePoll()
        {
            _singlePollRequested = true;
        }

        // ── 主机模式：开始扫描在线电池包 ──
        public void StartScanOnlinePacks()
        {
            _scanRequested = true;
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

        private void WorkerLoop()
        {
            byte[] rxBuf = new byte[1024];
            int rxLen = 0;
            Stopwatch swIdle = new Stopwatch();
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
                        // 1. 从机模式：高速监听并秒级响应逆变器查询
                        // ══════════════════════════════════════════════════════════
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
                            if (read > 0)
                            {
                                rxLen += read;
                                if (ProtocolHandler is PylontechAsciiHandler)
                                {
                                    while (rxLen > 0)
                                    {
                                        int soiIndex = -1;
                                        for (int i = 0; i < rxLen; i++)
                                        {
                                            if (rxBuf[i] == (byte)'~') { soiIndex = i; break; }
                                        }

                                        if (soiIndex < 0)
                                        {
                                            if (rxLen > 256 || swIdle.ElapsedMilliseconds >= 50) rxLen = 0;
                                            break;
                                        }

                                        if (soiIndex > 0)
                                        {
                                            Array.Copy(rxBuf, soiIndex, rxBuf, 0, rxLen - soiIndex);
                                            rxLen -= soiIndex;
                                        }

                                        int eoiIndex = -1;
                                        for (int i = 0; i < rxLen; i++)
                                        {
                                            if (rxBuf[i] == 0x0D) { eoiIndex = i; break; }
                                        }

                                        if (eoiIndex < 0) break;

                                        int frameLen = eoiIndex + 1;
                                        byte[] currentFrame = new byte[frameLen];
                                        Array.Copy(rxBuf, 0, currentFrame, 0, frameLen);
                                        Array.Copy(rxBuf, frameLen, rxBuf, 0, rxLen - frameLen);
                                        rxLen -= frameLen;

                                        RxCount++;
                                        Stopwatch swResp = Stopwatch.StartNew();
                                        if (OnLogEvent != null) OnLogEvent(this, new LogEventArgs(false, currentFrame, ""));

                                        byte[] txResp;
                                        string decodedInfo;
                                        if (ProtocolHandler.TryProcessQuery(currentFrame, currentFrame.Length, Model, ReplyAllIds, SpecificId, out txResp, out decodedInfo))
                                        {
                                            Thread.Sleep(5);
                                            lock (_portLock) { if (_port != null && _port.IsOpen) _port.Write(txResp, 0, txResp.Length); }
                                            swResp.Stop();
                                            TxCount++;
                                            if (OnLogEvent != null) OnLogEvent(this, new LogEventArgs(true, txResp, decodedInfo, swResp.ElapsedMilliseconds));
                                        }
                                        else { ErrorCount++; }
                                    }
                                }
                                else
                                {
                                    bool shouldProcess = false;
                                    if (rxLen >= 8 && ModbusCrc.Check(rxBuf, 8)) shouldProcess = true;
                                    else if (rxLen > 0 && swIdle.ElapsedMilliseconds >= 12) shouldProcess = true;

                                    if (shouldProcess)
                                    {
                                        int frameLen = (rxLen >= 8 && ModbusCrc.Check(rxBuf, 8)) ? 8 : rxLen;
                                        byte[] currentFrame = new byte[frameLen];
                                        Array.Copy(rxBuf, 0, currentFrame, 0, frameLen);
                                        Array.Copy(rxBuf, frameLen, rxBuf, 0, rxLen - frameLen);
                                        rxLen -= frameLen;

                                        RxCount++;
                                        Stopwatch swResp = Stopwatch.StartNew();
                                        if (OnLogEvent != null) OnLogEvent(this, new LogEventArgs(false, currentFrame, ""));

                                        byte[] txResp;
                                        string decodedInfo;
                                        if (ProtocolHandler.TryProcessQuery(currentFrame, currentFrame.Length, Model, ReplyAllIds, SpecificId, out txResp, out decodedInfo))
                                        {
                                            Thread.Sleep(6);
                                            lock (_portLock) { if (_port != null && _port.IsOpen) _port.Write(txResp, 0, txResp.Length); }
                                            swResp.Stop();
                                            TxCount++;
                                            if (OnLogEvent != null) OnLogEvent(this, new LogEventArgs(true, txResp, decodedInfo, swResp.ElapsedMilliseconds));
                                        }
                                        else { ErrorCount++; }
                                    }
                                }
                                swIdle.Restart();
                            }
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
                            if (OnStatusChanged != null) OnStatusChanged(this, "正在扫描总线在线电池包 (ID 0 ~ 15)...");
                            List<byte> foundPacks = new List<byte>();

                            for (byte scanId = 0; scanId <= 15; scanId++)
                            {
                                if (!_isRunning || CurrentWorkMode != WorkMode.MasterPoller) break;
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

                                // 等待从机响应 (80ms)
                                Stopwatch swProbe = Stopwatch.StartNew();
                                byte[] probeBuf = new byte[256];
                                int probeLen = 0;

                                while (swProbe.ElapsedMilliseconds < 80)
                                {
                                    int bRead = 0;
                                    lock (_portLock) { if (_port != null && _port.IsOpen) bRead = _port.BytesToRead; }
                                    if (bRead > 0)
                                    {
                                        lock (_portLock)
                                        {
                                            if (_port != null && _port.IsOpen)
                                                probeLen += _port.Read(probeBuf, probeLen, Math.Min(bRead, probeBuf.Length - probeLen));
                                        }
                                        string dummy;
                                        if (ProtocolHandler.TryDecodeMasterResponse(probeBuf, probeLen, Model, out dummy))
                                        {
                                            foundPacks.Add(scanId);
                                            RxCount++;
                                            break;
                                        }
                                    }
                                    Thread.Sleep(5);
                                }
                            }

                            string scanSummary = foundPacks.Count > 0
                                ? string.Format("✓ 扫描完成，发现 {0} 个在线电池包: ID {1}", foundPacks.Count, string.Join(", ", foundPacks.ConvertAll<string>(delegate(byte b) { return b.ToString(); }).ToArray()))
                                : "⚠️ 扫描完成，未探测到在线电池包";
                            if (OnStatusChanged != null) OnStatusChanged(this, scanSummary);
                            if (OnScanPacksCompleted != null) OnScanPacksCompleted(this, foundPacks);
                            swPoll.Restart();
                            swLastMasterRx.Restart();
                        }

                        // 2.2 优先发送队列中的主动控制帧 (如写参数、关机)
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
                            if (OnLogEvent != null)
                                OnLogEvent(this, new LogEventArgs(true, pendingCmd.Key, pendingCmd.Value));
                        }

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
                            byte[] query = ProtocolHandler.BuildMasterPollFrame(TargetSlaveId, pollPhase);
                            pollPhase = (pollPhase + 1) % phaseCount;

                            lock (_portLock)
                            {
                                if (_port != null && _port.IsOpen)
                                    _port.Write(query, 0, query.Length);
                            }
                            TxCount++;
                            if (OnLogEvent != null)
                                OnLogEvent(this, new LogEventArgs(true, query, string.Format("[主机轮询] 发送查询 ID:{0} Phase:{1}/{2}", TargetSlaveId, pollPhase, phaseCount)));
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
                                if (rxBuf[rxLen - 1] == 0x0D || swIdle.ElapsedMilliseconds >= 25) ready = true;
                            }
                            else
                            {
                                if (swIdle.ElapsedMilliseconds >= 20 || (rxLen >= 5 && ModbusCrc.Check(rxBuf, rxLen))) ready = true;
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
                                    if (OnLogEvent != null) OnLogEvent(this, new LogEventArgs(false, currentFrame, "[从机校验失败或未知响应]"));
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
