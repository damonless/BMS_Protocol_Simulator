using System;
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

        public BmsDataModel Model { get; private set; }
        public IBmsProtocolHandler ProtocolHandler { get; set; }
        public WorkMode CurrentWorkMode { get; set; }
        public bool ReplyAllIds { get; set; }
        public int SpecificId { get; set; }
        public int PollIntervalMs { get; set; }

        // 统计数据
        public long RxCount { get; private set; }
        public long TxCount { get; private set; }
        public long ErrorCount { get; private set; }

        public event EventHandler<LogEventArgs> OnLogEvent;
        public event EventHandler<string> OnStatusChanged;

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
            PollIntervalMs = 500;
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
        }

        private void WorkerLoop()
        {
            byte[] rxBuf = new byte[1024];
            int rxLen = 0;
            Stopwatch swIdle = new Stopwatch();
            Stopwatch swPoll = Stopwatch.StartNew();
            int pollPhase = 0;
            byte masterTargetId = 1;

            while (_isRunning)
            {
                try
                {
                    if (CurrentWorkMode == WorkMode.SlaveSimulator)
                    {
                        // ── 从机模式：高速监听并秒级响应 ──
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
                                swIdle.Restart();
                            }
                        }

                        // 空闲断帧机制 (ASCII 遇到 \r 触发，Modbus 收到 8 字节或 10ms 空闲触发)
                        bool shouldProcess = false;
                        if (rxLen > 0)
                        {
                            if (ProtocolHandler is PylontechAsciiHandler && rxBuf[rxLen - 1] == 0x0D)
                            {
                                shouldProcess = true;
                            }
                            else if (rxLen == 8)
                            {
                                shouldProcess = true;
                            }
                            else if (swIdle.ElapsedMilliseconds >= 10)
                            {
                                shouldProcess = true;
                            }
                        }

                        if (shouldProcess)
                        {
                            RxCount++;
                            byte[] currentFrame = new byte[rxLen];
                            Array.Copy(rxBuf, currentFrame, rxLen);
                            rxLen = 0;

                            Stopwatch swResp = Stopwatch.StartNew();

                            if (OnLogEvent != null)
                                OnLogEvent(this, new LogEventArgs(false, currentFrame, ""));

                            byte[] txResp;
                            string decodedInfo;
                            if (ProtocolHandler.TryProcessQuery(currentFrame, currentFrame.Length, Model, ReplyAllIds, SpecificId, out txResp, out decodedInfo))
                            {
                                // 模拟真实 BMS 处理时延 (5~8ms)
                                Thread.Sleep(6);

                                lock (_portLock)
                                {
                                    if (_port != null && _port.IsOpen)
                                    {
                                        _port.Write(txResp, 0, txResp.Length);
                                    }
                                }
                                swResp.Stop();
                                TxCount++;
                                if (OnLogEvent != null)
                                    OnLogEvent(this, new LogEventArgs(true, txResp, decodedInfo, swResp.ElapsedMilliseconds));
                            }
                            else
                            {
                                ErrorCount++;
                            }
                        }
                        else
                        {
                            Thread.Sleep(1);
                        }
                    }
                    else
                    {
                        // ── 主机轮询模式：定期向真实电池包发送查询 ──
                        if (swPoll.ElapsedMilliseconds >= PollIntervalMs)
                        {
                            swPoll.Restart();
                            byte[] query = ProtocolHandler.BuildMasterPollFrame(masterTargetId, pollPhase);
                            pollPhase = (pollPhase + 1) % 3;

                            lock (_portLock)
                            {
                                if (_port != null && _port.IsOpen)
                                {
                                    _port.Write(query, 0, query.Length);
                                }
                            }
                            TxCount++;
                            if (OnLogEvent != null)
                                OnLogEvent(this, new LogEventArgs(true, query, string.Format("[主机轮询] 发送查询 ID:{0} Phase:{1}", masterTargetId, pollPhase)));
                        }

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
                            rxLen += read;
                            swIdle.Restart();
                        }

                        if (rxLen > 0 && swIdle.ElapsedMilliseconds >= 25)
                        {
                            RxCount++;
                            byte[] currentFrame = new byte[rxLen];
                            Array.Copy(rxBuf, currentFrame, rxLen);
                            rxLen = 0;

                            string decoded;
                            if (ProtocolHandler.TryDecodeMasterResponse(currentFrame, currentFrame.Length, Model, out decoded))
                            {
                                if (OnLogEvent != null)
                                    OnLogEvent(this, new LogEventArgs(false, currentFrame, decoded));
                            }
                            else
                            {
                                ErrorCount++;
                                if (OnLogEvent != null)
                                    OnLogEvent(this, new LogEventArgs(false, currentFrame, "[未知从机响应或校验失败]"));
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
