using System;
using System.Text;

namespace BMS_Protocol_Simulator
{
    public static class ModbusCrc
    {
        private static readonly ushort[] Table = new ushort[]
        {
            0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401,
            0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400
        };

        public static ushort Calculate(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + length; i++)
            {
                byte b = data[i];
                crc = (ushort)(Table[(b ^ crc) & 0x0F] ^ (crc >> 4));
                crc = (ushort)(Table[((b >> 4) ^ crc) & 0x0F] ^ (crc >> 4));
            }
            return (ushort)(((crc & 0xFF) << 8) | ((crc >> 8) & 0xFF));
        }

        public static ushort Calculate(byte[] data, int length)
        {
            return Calculate(data, 0, length);
        }

        public static bool Check(byte[] data, int length, int offset = 0)
        {
            if (length < 4 || data == null || offset + length > data.Length) return false;
            ushort expectedCrc = (ushort)((data[offset + length - 2] << 8) | data[offset + length - 1]);
            ushort calcCrc = Calculate(data, offset, length - 2);
            return expectedCrc == calcCrc;
        }
    }

    public static class PylonChecksum
    {
        public static ushort Calculate(byte[] asciiBytes, int start, int length)
        {
            int sum = 0;
            for (int i = start; i < start + length; i++)
            {
                sum += asciiBytes[i];
            }
            sum = sum & 0xFFFF;
            ushort chk = (ushort)(~sum + 1);
            return chk;
        }

        public static bool Verify(byte[] fullFrame)
        {
            if (fullFrame == null || fullFrame.Length < 18) return false;
            if (fullFrame[0] != (byte)'~' || fullFrame[fullFrame.Length - 1] != 0x0D) return false;

            string recvHex = Encoding.ASCII.GetString(fullFrame, fullFrame.Length - 5, 4);
            ushort recvChk;
            if (!ushort.TryParse(recvHex, System.Globalization.NumberStyles.HexNumber, null, out recvChk))
                return false;

            ushort calcChk = Calculate(fullFrame, 1, fullFrame.Length - 6);
            return recvChk == calcChk;
        }
    }

    public static class JbdChecksum
    {
        public static ushort Calculate(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0 || offset < 0 || offset + count > buffer.Length) return 0;
            int sum = 0;
            for (int i = offset; i < offset + count; i++)
            {
                sum += buffer[i];
            }
            return (ushort)((~sum + 1) & 0xFFFF);
        }

        public static bool VerifyRequest(byte[] buffer, int offset, int totalLength, out int frameCoreLen, out int callbackLen)
        {
            frameCoreLen = 0;
            callbackLen = 0;
            if (buffer == null || totalLength < 7 || offset < 0 || offset + totalLength > buffer.Length)
                return false;

            if (buffer[offset] != 0xDD)
                return false;

            byte rw = buffer[offset + 1];
            if (rw != 0xA5 && rw != 0x5A)
                return false;

            byte dataLen = buffer[offset + 3];
            frameCoreLen = 4 + dataLen + 3;

            if (totalLength < frameCoreLen)
                return false;

            if (buffer[offset + frameCoreLen - 1] != 0x77)
                return false;

            ushort expectedChk = (ushort)((buffer[offset + 4 + dataLen] << 8) | buffer[offset + 4 + dataLen + 1]);
            ushort calcChk = Calculate(buffer, offset + 2, 2 + dataLen);
            if (expectedChk != calcChk)
                return false;

            int extra = totalLength - frameCoreLen;
            if (extra > 0)
            {
                int maxCb = Math.Min(4, extra);
                for (int k = 0; k < maxCb; k++)
                {
                    if (buffer[offset + frameCoreLen + k] == 0xDD) break;
                    callbackLen++;
                }
            }
            return true;
        }

        public static bool VerifyResponse(byte[] buffer, int offset, int totalLength, out int frameCoreLen)
        {
            frameCoreLen = 0;
            if (buffer == null || totalLength < 7 || offset < 0 || offset + totalLength > buffer.Length)
                return false;

            if (buffer[offset] != 0xDD)
                return false;

            byte status = buffer[offset + 2];
            if (status != 0x00 && status != 0x80 && status != 0x81 && status != 0x82 && status != 0x83 && status != 0x84)
                return false;

            byte dataLen = buffer[offset + 3];
            frameCoreLen = 4 + dataLen + 3;

            if (totalLength < frameCoreLen)
                return false;

            if (buffer[offset + frameCoreLen - 1] != 0x77)
                return false;

            ushort expectedChk = (ushort)((buffer[offset + 4 + dataLen] << 8) | buffer[offset + 4 + dataLen + 1]);
            ushort calcChk = Calculate(buffer, offset + 2, 2 + dataLen);
            if (expectedChk != calcChk)
                return false;

            return true;
        }

        public static byte[] BuildResponse(byte cmd, byte status, byte[] data, byte[] callbackId = null)
        {
            int dataLen = data != null ? data.Length : 0;
            int cbLen = callbackId != null ? Math.Min(4, callbackId.Length) : 0;
            int totalLen = 7 + dataLen + cbLen;
            byte[] resp = new byte[totalLen];
            resp[0] = 0xDD;
            resp[1] = cmd;
            resp[2] = status;
            resp[3] = (byte)dataLen;
            if (dataLen > 0)
            {
                Array.Copy(data, 0, resp, 4, dataLen);
            }
            ushort chk = Calculate(resp, 2, 2 + dataLen);
            resp[4 + dataLen] = (byte)(chk >> 8);
            resp[4 + dataLen + 1] = (byte)(chk & 0xFF);
            resp[4 + dataLen + 2] = 0x77;
            if (cbLen > 0)
            {
                Array.Copy(callbackId, 0, resp, 7 + dataLen, cbLen);
            }
            return resp;
        }

        public static byte[] BuildRequest(byte rw, byte cmd, byte[] data, byte[] callbackId = null)
        {
            int dataLen = data != null ? data.Length : 0;
            int cbLen = callbackId != null ? Math.Min(4, callbackId.Length) : 0;
            int totalLen = 7 + dataLen + cbLen;
            byte[] req = new byte[totalLen];
            req[0] = 0xDD;
            req[1] = rw;
            req[2] = cmd;
            req[3] = (byte)dataLen;
            if (dataLen > 0)
            {
                Array.Copy(data, 0, req, 4, dataLen);
            }
            ushort chk = Calculate(req, 2, 2 + dataLen);
            req[4 + dataLen] = (byte)(chk >> 8);
            req[4 + dataLen + 1] = (byte)(chk & 0xFF);
            req[4 + dataLen + 2] = 0x77;
            if (cbLen > 0)
            {
                Array.Copy(callbackId, 0, req, 7 + dataLen, cbLen);
            }
            return req;
        }
    }

    public interface IBmsProtocolHandler
    {
        string ProtocolName { get; }
        int MasterPollPhaseCount { get; }
        // 尝试解析逆变器发来的查询，并生成从机应答包
        bool TryProcessQuery(byte[] rxBuffer, int rxLength, BmsDataModel model, bool replyAllIds, int specificId, out byte[] txResponse, out string decodedInfo);
        // 主机轮询模式：构造发送给真实电池包的查询帧
        byte[] BuildMasterPollFrame(byte targetId, int pollPhase);
        // 主机轮询模式：解析真实电池包回复的报文并更新至 model
        bool TryDecodeMasterResponse(byte[] rxBuffer, int rxLength, BmsDataModel model, out string decodedInfo);
    }

    // ─────────────────────────────────────────────────────────────
    // 1. CVTE (Modbus RTU) 协议处理器 (严格按 Servotech Inverter Modbus Protocol PDF V1.3/V1.4 规范实现)
    // ─────────────────────────────────────────────────────────────
    public class CvteModbusHandler : IBmsProtocolHandler
    {
        public string ProtocolName
        {
            get { return "CVTE (Modbus RTU)"; }
        }

        public bool TryProcessQuery(byte[] rxBuffer, int rxLength, BmsDataModel model, bool replyAllIds, int specificId, out byte[] txResponse, out string decodedInfo)
        {
            txResponse = null;
            decodedInfo = "";

            if (rxLength != 8) return false;
            if (!ModbusCrc.Check(rxBuffer, 8)) return false;

            byte id = rxBuffer[0];
            byte cmd = rxBuffer[1];
            ushort regStart = (ushort)((rxBuffer[2] << 8) | rxBuffer[3]);
            ushort regCount = (ushort)((rxBuffer[4] << 8) | rxBuffer[5]);

            if (cmd != 0x03 || regCount == 0 || regCount > 100)
                return false;

            // CVTE 协议合法寄存器起始地址范围过滤 (防止误响应 Voltronic 0x0030/0x0060/0x0070 或 Growatt 0x0001 len 35 / 0x0014 len 15，使逆变器产生 56 通信丢失告警):
            bool isValidCvteAddr = (regStart >= 0x0011 && regStart <= 0x0013 && regCount <= 3) ||
                                   (regStart >= 0x001C && regStart <= 0x001F && regCount <= 4) ||
                                   (regStart >= 0x0020 && regStart <= 0x0036 && regStart != 0x0030 && regCount <= 23) ||
                                   (regStart >= 0x0040 && regStart <= 0x005A && regCount <= 27) ||
                                   (regStart >= 0x0060 && regStart <= 0x006E && regCount <= 15 && !(regStart == 0x0060 && regCount == 10));
            if (!isValidCvteAddr)
                return false;

            if (!replyAllIds && id != specificId)
            {
                decodedInfo = string.Format("收到对 ID:{0:X2} 的查询 (已按单包规则忽略)", id);
                return false;
            }

            int byteCount = regCount * 2;
            byte[] resp = new byte[3 + byteCount + 2];
            resp[0] = id;
            resp[1] = 0x03;
            resp[2] = (byte)byteCount;

            lock (model.SyncRoot)
            {
                for (int i = 0; i < regCount; i++)
                {
                    ushort addr = (ushort)(regStart + i);
                    ushort val = GetCvteRegisterValue(addr, model);
                    resp[3 + i * 2] = (byte)(val >> 8);
                    resp[3 + i * 2 + 1] = (byte)(val & 0xFF);
                }

                string statusTag = "";
                if (model.HasProtection) statusTag += string.Format(" [保护跳闸: {0}]", model.GetActiveProtectionsSummary());
                if (model.HasWarning) statusTag += string.Format(" [告警提示: {0}]", model.GetActiveWarningsSummary());

                decodedInfo = string.Format("[CVTE] 应答 ID:{0:X2} 读 0x{1:X4} ({2}寄存器) -> 电压:{3:F2}V, 电流:{4:F2}A, SOC:{5:F0}%, 容量:{6:F1}/{7:F1}Ah, CV:{8:F2}V, 限流:{9:F1}A{10}",
                    id, regStart, regCount, model.Voltage, model.Current, model.SOC, model.RemainingCapacity, model.FullCapacity, model.CVVoltage, model.CanCharge ? model.MaxChargeCurrent : 0.0, statusTag);
            }


            ushort crc = ModbusCrc.Calculate(resp, 3 + byteCount);
            resp[3 + byteCount] = (byte)(crc >> 8);
            resp[3 + byteCount + 1] = (byte)(crc & 0xFF);

            txResponse = resp;
            return true;
        }

        private ushort GetCvteRegisterValue(ushort regAddress, BmsDataModel model)
        {
            switch (regAddress)
            {
                case 0x0011: // MCU 软件版本 (V1.3, PDF Page 6)
                    return 0x0103;

                case 0x0012: // BMS 公司信息与版本 (CVTE / Gen 1, PDF Page 6)
                    return 0x0100;

                case 0x0013: // 电池类型 (0: LFP, PDF Page 6)
                    return 0x0000;

                case 0x001C: // 协议识别码 'CV' (0x4356, PDF Page 6)
                    return 0x4356;

                case 0x001D: // 协议识别码 'TE' (0x5445, PDF Page 6)
                    return 0x5445;

                case 0x001E: // 协议识别码 '_C' (0x5F43, PDF Page 6)
                    return 0x5F43;

                case 0x001F: // 协议识别码 'OM' (0x4F4D, PDF Page 6)
                    return 0x4F4D;

                case 0x0020: // 状态信息 (PDF Page 8)
                    ushort mode = 0;
                    // Bit 0~1: 状态 (00:软启动, 01:待机, 10:充电, 11:放电)
                    if (model.Current > 0.05) mode |= 0x0002;      // 10: 充电
                    else if (model.Current < -0.05) mode |= 0x0003; // 11: 放电
                    else mode |= 0x0001;                            // 01: 待机

                    if (model.HasProtection)
                    {
                        mode |= (1 << 2); // Bit 2: 错误位有效标志
                    }
                    if (model.StatusBalancing) mode |= (1 << 3);      // Bit 3: 电芯均衡中
                    if (model.StatusSleep) mode |= (1 << 4);          // Bit 4: 休眠状态
                    if (model.CanDischarge) mode |= (1 << 5);         // Bit 5: 放电输出开启
                    if (model.CanCharge) mode |= (1 << 6);            // Bit 6: 充电输入开启
                    // Bit 7: 0为端子连接
                    // Bit 8~9: 00为单机
                    if (model.StatusForceCharge) mode |= (1 << 12);   // Bit 12: 强制充电请求
                    return mode;

                case 0x0021: // 电池总压 (10mV, PDF Page 7)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.Voltage * 100)));

                case 0x0022: // 电池电流 (10mA, 有符号: 0x0000~0x7FFF充电, 0x8000~0xFFFF放电, PDF Page 8-9)
                    short sCurr = (short)Math.Round(model.Current * 100);
                    return (ushort)sCurr;

                case 0x0023: // 电芯最高温度 (1°C, 有符号, PDF Page 7)
                    short sTemp = (short)Math.Round(model.Temperature);
                    return (ushort)sTemp;

                case 0x0024: // SOC (1%, PDF Page 7)
                    return (ushort)Math.Max(0, Math.Min(100, (int)Math.Round(model.SOC)));

                case 0x0025: // 剩余容量 (10mAH, PDF Page 7)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.RemainingCapacity * 100)));

                case 0x0026: // 满充容量 (10mAH, PDF Page 7)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.FullCapacity * 100)));

                case 0x0027: // 故障信息 (Fault / Protection, PDF Page 9)
                    ushort err = 0;
                    if (model.ProtOverVolt) err |= 0x0001;     // Bit 0: 过压保护
                    if (model.ProtUnderVolt) err |= 0x0002;    // Bit 1: 欠压保护
                    if (model.ProtOverCurrent) err |= 0x0004;  // Bit 2: 过流保护
                    if (model.ProtShortCircuit) err |= 0x0008;// Bit 3: 短路保护
                    if (model.ProtHighTemp) err |= 0x0010;     // Bit 4: 高温保护
                    if (model.ProtUnderTemp) err |= 0x0020;    // Bit 5: 低温保护
                    if (model.ProtSystemFault && model.WarnVoltDiff) err |= 0x0040; // Bit 6: 压差保护
                    if (model.ProtSystemFault) err |= 0x0080;  // Bit 7: 系统故障保护
                    if (model.ProtSoftStart) err |= 0x0100;    // Bit 8: 软启动保护
                    return err;

                case 0x0028: // 警告信息 (Warning, PDF Page 9-10)
                    ushort warn = 0;
                    if (model.WarnSingleOverVolt || model.WarnGlobalOverVolt) warn |= 0x0001; // Bit 0: 过压警告
                    if (model.WarnSingleUnderVolt || model.WarnGlobalUnderVolt) warn |= 0x0002; // Bit 1: 欠压警告
                    if (model.WarnOverCurrent) warn |= 0x0004; // Bit 2: 过流警告
                    if (model.WarnHighTemp) warn |= 0x0008;    // Bit 3: 高温警告
                    if (model.WarnLowTemp) warn |= 0x0010;     // Bit 4: 低温警告
                    if (model.WarnVoltDiff) warn |= 0x0020;    // Bit 5: 压差警告
                    if (model.WarnLowCapacity) warn |= 0x0040; // Bit 6: 电池低电量告警
                    return warn;

                case 0x0029: // 最大充电电流 (10mA, PDF Page 7)
                    if (!model.CanCharge)
                        return 0;
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.MaxChargeCurrent * 100)));

                case 0x002A: // CV 恒压充电点 (10mV, PDF Page 7)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.CVVoltage * 100)));

                case 0x002B: // SOH (1%, PDF Page 7)
                    return (ushort)Math.Max(0, Math.Min(100, (int)Math.Round(model.SOH)));

                case 0x002C: // 循环次数 (PDF Page 7)
                    return (ushort)Math.Max(0, Math.Min(65535, model.CycleCount));

                case 0x002D: // 充电剩余时间 (min, PDF Page 7)
                    return (model.Current > 0.1 && model.MaxChargeCurrent > 0) ? (ushort)60 : (ushort)0;

                case 0x002E: // 单体最高电压 (mV, 按显式串数适配, PDF Page 7)
                    {
                        int cellCountMax = model.GetEffectiveCellCount();
                        ushort maxMv, minMv;
                        model.GetCellMillivolts(0, cellCountMax, out maxMv, out minMv);
                        return maxMv;
                    }

                case 0x002F: // 单体最高电压位号
                    return 1;

                case 0x0030: // 单体最低电压 (mV, 按显式串数适配, PDF Page 7)
                    {
                        int cellCountMin = model.GetEffectiveCellCount();
                        ushort maxMv, minMv;
                        model.GetCellMillivolts(1, cellCountMin, out maxMv, out minMv);
                        return minMv;
                    }

                case 0x0031: // 单体最低电压位号
                    return 2;

                case 0x0032: // 电芯压差 (mV, PDF Page 8)
                    {
                        int cellCountDiff = model.GetEffectiveCellCount();
                        ushort maxMv, minMv;
                        model.GetCellMillivolts(0, cellCountDiff, out maxMv, out minMv);
                        return (ushort)Math.Max(0, maxMv - minMv);
                    }

                case 0x0033: // 单芯最高温度 (℃, PDF Page 8)
                    return (ushort)((short)Math.Round(model.Temperature));

                case 0x0034: // 单芯最高温度位号
                    return 1;

                case 0x0035: // 单芯最低温度 (℃, PDF Page 8)
                    return (ushort)((short)Math.Round(model.Temperature - 1));

                case 0x0036: // 单芯最低温度位号
                    return 2;

                case 0x0040: // 电池串数 (按显式串数适配, PDF Page 10)
                    return (ushort)model.GetEffectiveCellCount();

                case 0x0060: // 环境温度 (℃, PDF Page 11)
                case 0x0061: // MOS 1 温度 (℃, PDF Page 11)
                case 0x0062: // MOS 2 温度 (℃, PDF Page 11)
                case 0x0063: // MOS 3 温度 (℃, PDF Page 11)
                    return (ushort)((short)Math.Round(model.Temperature));

                case 0x0064: // 电芯 NTC 个数 (个, PDF Page 11)
                    return 4;

                default:
                    // 按显式串数填充单体电芯电压 0x0041~0x005A
                    if (regAddress >= 0x0041 && regAddress <= 0x005A)
                    {
                        int nCells = model.GetEffectiveCellCount();
                        int cellIdx = regAddress - 0x0040;
                        if (cellIdx <= nCells)
                        {
                            ushort maxMv, minMv;
                            return model.GetCellMillivolts(cellIdx - 1, nCells, out maxMv, out minMv);
                        }
                    }
                    // 默认多 NTC 温度填充 0x0065~0x006E
                    if (regAddress >= 0x0065 && regAddress <= 0x006E)
                    {
                        return (ushort)((short)Math.Round(model.Temperature));
                    }
                    return 0x0000;
            }
        }

        public int MasterPollPhaseCount
        {
            get { return 1; }
        }

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            byte[] frame = new byte[8];
            frame[0] = targetId;
            frame[1] = 0x03;
            frame[2] = 0x00; frame[3] = 0x20; // 0x0020
            frame[4] = 0x00; frame[5] = 0x0D; // 13 寄存器 (0x0020~0x002C 包含循环次数)
            ushort crc = ModbusCrc.Calculate(frame, 6);
            frame[6] = (byte)(crc >> 8);
            frame[7] = (byte)(crc & 0xFF);
            return frame;
        }

        public bool TryDecodeMasterResponse(byte[] rxBuffer, int rxLength, BmsDataModel model, out string decodedInfo)
        {
            decodedInfo = "";
            if (rxLength < 7) return false;
            if (!ModbusCrc.Check(rxBuffer, rxLength)) return false;
            if (rxBuffer[1] != 0x03 && rxBuffer[1] != 0x06) return false;

            if (rxBuffer[1] == 0x06)
            {
                ushort addr = (ushort)((rxBuffer[2] << 8) | rxBuffer[3]);
                ushort val = (ushort)((rxBuffer[4] << 8) | rxBuffer[5]);
                decodedInfo = string.Format("[CVTE] 从机确认写入: 寄存器 0x{0:X4} = 0x{1:X4}", addr, val);
                return true;
            }

            byte byteCount = rxBuffer[2];
            if (rxLength != 3 + byteCount + 2) return false;

            lock (model.SyncRoot)
            {
                if (byteCount >= 24)
                {
                    // 0x0020: 状态信息
                    ushort status = (ushort)((rxBuffer[3] << 8) | rxBuffer[4]);
                    model.StatusBalancing = (status & (1 << 3)) != 0;
                    model.StatusSleep = (status & (1 << 4)) != 0;
                    model.StatusDischargeEnable = (status & (1 << 5)) != 0;
                    model.StatusChargeEnable = (status & (1 << 6)) != 0;
                    model.StatusForceCharge = (status & (1 << 12)) != 0;

                    // 0x0021: 电池总压 (10mV)
                    model.Voltage = ((rxBuffer[5] << 8) | rxBuffer[6]) / 100.0;
                    // 0x0022: 充放电流 (10mA)
                    model.Current = (short)((rxBuffer[7] << 8) | rxBuffer[8]) / 100.0;
                    // 0x0023: 最高温度 (1°C)
                    model.Temperature = (short)((rxBuffer[9] << 8) | rxBuffer[10]);
                    // 0x0024: SOC (1%)
                    model.SOC = ((rxBuffer[11] << 8) | rxBuffer[12]);
                    // 0x0025: 剩余容量 (10mAh)
                    model.RemainingCapacity = ((rxBuffer[13] << 8) | rxBuffer[14]) / 100.0;
                    // 0x0026: 满充容量 (10mAh)
                    model.FullCapacity = ((rxBuffer[15] << 8) | rxBuffer[16]) / 100.0;

                    // 0x0027: 故障保护信息
                    ushort fault = (ushort)((rxBuffer[17] << 8) | rxBuffer[18]);
                    model.ProtOverVolt = (fault & 0x0001) != 0;
                    model.ProtUnderVolt = (fault & 0x0002) != 0;
                    model.ProtOverCurrent = (fault & 0x0004) != 0;
                    model.ProtShortCircuit = (fault & 0x0008) != 0;
                    model.ProtHighTemp = (fault & 0x0010) != 0;
                    model.ProtUnderTemp = (fault & 0x0020) != 0;
                    model.ProtSystemFault = (fault & 0x00C0) != 0;
                    model.ProtSoftStart = (fault & 0x0100) != 0;

                    // 0x0028: 警告信息
                    ushort warn = (ushort)((rxBuffer[19] << 8) | rxBuffer[20]);
                    model.WarnSingleOverVolt = (warn & 0x0001) != 0;
                    model.WarnSingleUnderVolt = (warn & 0x0002) != 0;
                    model.WarnGlobalOverVolt = (warn & 0x0001) != 0;
                    model.WarnGlobalUnderVolt = (warn & 0x0002) != 0;
                    model.WarnOverCurrent = (warn & 0x0004) != 0;
                    model.WarnHighTemp = (warn & 0x0008) != 0;
                    model.WarnLowTemp = (warn & 0x0010) != 0;
                    model.WarnVoltDiff = (warn & 0x0020) != 0;
                    model.WarnLowCapacity = (warn & 0x0040) != 0;

                    // 0x0029: 最大充电电流
                    model.MaxChargeCurrent = ((rxBuffer[21] << 8) | rxBuffer[22]) / 100.0;
                    // 0x002A: CV 恒压点
                    model.CVVoltage = ((rxBuffer[23] << 8) | rxBuffer[24]) / 100.0;
                    // 0x002B: SOH
                    model.SOH = ((rxBuffer[25] << 8) | rxBuffer[26]);

                    // 0x002C: 循环次数 (若读取 >= 26 字节)
                    if (byteCount >= 26)
                    {
                        model.CycleCount = (rxBuffer[27] << 8) | rxBuffer[28];
                    }
                }
            }

            decodedInfo = string.Format("[CVTE] 解析成功: 电压={0:F2}V, 电流={1:F2}A, SOC={2:F0}%, 容量={3:F1}/{4:F1}Ah, CV={5:F2}V, 限流={6:F1}A",
                model.Voltage, model.Current, model.SOC, model.RemainingCapacity, model.FullCapacity, model.CVVoltage, model.MaxChargeCurrent);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. GROWATT (Modbus RTU) 协议处理器
    // ─────────────────────────────────────────────────────────────
    // ─────────────────────────────────────────────────────────────
    // 2. GROWATT (Modbus RTU) 协议处理器 (严格按 Growatt xxSxxP ESS Protocol PDF Rev 2.01 规范实现)
    // ─────────────────────────────────────────────────────────────
    public class GrowattModbusHandler : IBmsProtocolHandler
    {
        public string ProtocolName
        {
            get { return "GROWATT (Modbus RTU)"; }
        }

        public bool TryProcessQuery(byte[] rxBuffer, int rxLength, BmsDataModel model, bool replyAllIds, int specificId, out byte[] txResponse, out string decodedInfo)
        {
            txResponse = null;
            decodedInfo = "";

            if (rxLength != 8) return false;
            if (!ModbusCrc.Check(rxBuffer, 8)) return false;

            byte id = rxBuffer[0];
            byte cmd = rxBuffer[1];
            ushort regStart = (ushort)((rxBuffer[2] << 8) | rxBuffer[3]);
            ushort regCount = (ushort)((rxBuffer[4] << 8) | rxBuffer[5]);

            if (cmd != 0x03 || regCount == 0 || regCount > 120)
                return false;

            // Growatt 协议合法寄存器起始地址范围过滤 (严格限定古瑞瓦特协议定义的轮询分段，防止误响应 Voltronic 0x0030/0x0060/0x0070 或 CVTE 0x0020 len 12，使逆变器产生 56 通信丢失告警):
            bool isValidGrowattAddr = (regStart == 0x0001 && regCount <= 45) ||
                                      (regStart >= 0x0010 && regStart <= 0x0014 && regCount <= 26) ||
                                      (regStart >= 0x0015 && regStart <= 0x0029 && regCount <= 2) ||
                                      (regStart == 0x0071 && regCount <= 16);
            if (!isValidGrowattAddr)
                return false;


            if (!replyAllIds && id != specificId)
            {
                decodedInfo = string.Format("收到对 ID:{0:X2} 的查询 (已按单包规则忽略)", id);
                return false;
            }

            int byteCount = regCount * 2;
            byte[] resp = new byte[3 + byteCount + 2];
            resp[0] = id;
            resp[1] = 0x03;
            resp[2] = (byte)byteCount;

            lock (model.SyncRoot)
            {
                for (int i = 0; i < regCount; i++)
                {
                    ushort regAddr = (ushort)(regStart + i);
                    ushort val = GetGrowattRegisterValue(regAddr, model);
                    resp[3 + i * 2] = (byte)(val >> 8);
                    resp[3 + i * 2 + 1] = (byte)(val & 0xFF);
                }

                string statusTag = "";
                if (model.HasProtection) statusTag += string.Format(" [保护跳闸: {0}]", model.GetActiveProtectionsSummary());
                if (model.HasWarning) statusTag += string.Format(" [告警提示: {0}]", model.GetActiveWarningsSummary());

                decodedInfo = string.Format("[GROWATT] 应答 ID:{0:X2} 读 0x{1:X4} ({2}寄存器) -> 电压:{3:F2}V, SOC:{4:F0}%, 电流:{5:F2}A, 温度:{6:F1}°C, CV:{7:F2}V, 限流:{8:F1}A{9}",
                    id, regStart, regCount, model.Voltage, model.SOC, model.Current, model.Temperature, model.CVVoltage, model.CanCharge ? model.MaxChargeCurrent : 0.0, statusTag);
            }

            ushort crc = ModbusCrc.Calculate(resp, 3 + byteCount);
            resp[3 + byteCount] = (byte)(crc >> 8);
            resp[3 + byteCount + 1] = (byte)(crc & 0xFF);

            txResponse = resp;
            return true;
        }

        private ushort GetGrowattRegisterValue(ushort regAddress, BmsDataModel model)
        {
            switch (regAddress)
            {
                // ── Spec Query 区域 (0x0001 ~ 0x000F, PDF Page 6-7) ──
                case 0x0001: return 0x0201; // MCU SW Version V2.1
                case 0x0002: return 0x0100; // Gauge Version V1.0
                case 0x0003: return 0x0000; // Gauge FR Version Lo
                case 0x0004: return 0x0000; // Gauge FR Version Hi
                case 0x0005: return 0x0000; // Date & Time
                case 0x0006: return 0x0000;
                case 0x0007: return 0x0000;
                case 0x0008: return 0x0000;
                case 0x0009: return 0x4752; // Bar Code: 'G','R'
                case 0x000A: return 0x4F57; // 'O','W'
                case 0x000B: return 0x4154; // 'A','T'
                case 0x000C: return 0x5431; // 'T','1'
                case 0x000D: return 0x0100; // BMS Company: 0x00, BMS Ver: Gen 1 (0x01) (PDF Page 7)
                case 0x000E: return 0x0101; // PACK Company: 0x01 (EVE), PACK Ver: Gen 1 (0x01) (PDF Page 8)
                case 0x000F: return 5000;   // Using Capacity (5000WH / 5K) (PDF Page 7)

                // ── Status Query 区域 (0x0010 ~ 0x0030, PDF Page 8-15) ──
                case 0x0010: // Gauge IC Current (10mA, PDF Page 8)
                    return (ushort)((short)Math.Round(model.Current * 100));

                case 0x0011: return 0x0000; // Date & Time Lo
                case 0x0012: return 0x0000; // Date & Time Hi

                case 0x0013: // Status Word (PDF Page 8, 10)
                    ushort status = 0;
                    if (model.Current > 0.05) status |= 0x0002;      // 10: charging
                    else if (model.Current < -0.05) status |= 0x0003; // 11: discharging
                    else status |= 0x0001;                            // 01: stand by

                    if (model.HasProtection)
                    {
                        status |= (1 << 2); // Bit 2: Error bit flag
                    }
                    if (model.StatusBalancing) status |= (1 << 3);      // Bit 3: Cell balance PF status
                    if (model.StatusSleep) status |= (1 << 4);          // Bit 4: Sleep status

                    if (model.CanDischarge) status |= (1 << 5);// Bit 5: Output Discharge status
                    if (model.CanCharge) status |= (1 << 6);// Bit 6: Output Charge status

                    if (model.StatusForceCharge) status |= (1 << 12);   // Bit 12: Request force charge
                    return status;

                case 0x0014: // Error Code (PDF Page 8, 10-11)
                    ushort err = 0;
                    if (model.ProtOverCurrent) { err |= (1 << 0); err |= (1 << 11); }  // Bit 0: OCD (放电过流), Bit 11: OCC (充电过流)
                    if (model.ProtShortCircuit) err |= (1 << 1);                       // Bit 1: SCD (放电短路，移除误置位欠压Bit 3)
                    if (model.ProtOverVolt) err |= (1 << 2);                          // Bit 2: OV (总压/单体过压)
                    if (model.ProtUnderVolt) err |= (1 << 3);                         // Bit 3: UV (总压/单体欠压)
                    if (model.ProtHighTemp) { err |= (1 << 4); err |= (1 << 5); err |= (1 << 12); err |= (1 << 13); } // Bit 4: OTD, Bit 5: OTC, Bit 12: MOS OT, Bit 13: 环境 OT (3KU检测0x0030, 6KU全检测)
                    if (model.ProtUnderTemp) { err |= (1 << 6); err |= (1 << 7); err |= (1 << 14); } // Bit 6: UTD, Bit 7: UTC, Bit 14: 环境 UT (3KU检测0x00C0, 6KU全检测)
                    if (model.ProtSoftStart) err |= (1 << 8);                         // Bit 8: Soft start fail (软启动失败)
                    if (model.ProtSystemFault) err |= (1 << 9);                       // Bit 9: Permanent Fault (永久故障)
                    if (model.WarnVoltDiff) err |= (1 << 10);                         // Bit 10: Delta V Fail (压差故障)
                    return err;

                case 0x0015: // SOC (1%, PDF Page 8)
                    return (ushort)Math.Max(0, Math.Min(100, (int)Math.Round(model.SOC)));

                case 0x0016: // Total Voltage (10mV, PDF Page 8)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.Voltage * 100)));

                case 0x0017: // Current (10mA, 有符号: 0x0000~0x7FFF充电正值, 0x8000~0xFFFF放电负值, PDF Page 8, 11)
                    short sCurr = (short)Math.Round(model.Current * 100);
                    return (ushort)sCurr;

                case 0x0018: // Temperature (-127~127°C, 有符号, PDF Page 8)
                    short sTemp = (short)Math.Round(model.Temperature);
                    return (ushort)sTemp;

                case 0x0019: // Max Charge Current (10mA, PDF Page 8)
                    if (!model.CanCharge)
                        return 0;
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.MaxChargeCurrent * 100)));

                case 0x001A: // Gauge RM 剩余容量 (10mAh, PDF Page 9)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.RemainingCapacity * 100)));

                case 0x001B: // Gauge FCC 满充容量 (10mAh, PDF Page 9)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.FullCapacity * 100)));

                case 0x001C: // YW / FW (硬件/软件版本 1~9, PDF Page 9, 11)
                    return 0x0101;

                case 0x001D: // Delta Cell Voltage (mV -> V, PDF Page 9)
                    {
                        int nCells = model.GetEffectiveCellCount();
                        ushort maxMv, minMv;
                        model.GetCellMillivolts(0, nCells, out maxMv, out minMv);
                        return (ushort)Math.Max(0, maxMv - minMv);
                    }

                case 0x001E: // Cycle Count (PDF Page 9)
                    return (ushort)Math.Max(0, Math.Min(65535, model.CycleCount));

                case 0x001F: // Box Number / Battery ID (PDF Page 9, 12)
                    return 0x0000;

                case 0x0020: // SOH (Bit 0~6: SOH Counter, Bit 7: SOH Flag=1, PDF Page 9)
                    byte bSoh = (byte)Math.Max(0, Math.Min(100, (int)Math.Round(model.SOH)));
                    return (ushort)(bSoh | 0x0080);

                case 0x0021: // CV Voltage (10mV, PDF Page 9, 12)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.CVVoltage * 100)));

                case 0x0022: // Warning Code (PDF Page 9, 12-13)
                    ushort warn = 0;
                    if (model.WarnSingleOverVolt) warn |= (1 << 0);  // Bit 0: 单体过压告警
                    if (model.WarnSingleUnderVolt) warn |= (1 << 1); // Bit 1: 单体欠压告警
                    if (model.WarnGlobalOverVolt) warn |= (1 << 2);  // Bit 2: 总压过压告警
                    if (model.WarnGlobalUnderVolt) warn |= (1 << 3); // Bit 3: 总压欠压告警
                    if (model.WarnOverCurrent) { warn |= (1 << 4); warn |= (1 << 5); } // Bit 4: 放电过流告警, Bit 5: 充电过流告警 (3KU检测0x0030)
                    if (model.WarnHighTemp) { warn |= (1 << 6); warn |= (1 << 8); warn |= (1 << 10); warn |= (1 << 11); } // Bit 6,8,10,11: 充放电/MOS/环境高温告警 (3KU检测0x0D40)
                    if (model.WarnLowTemp) { warn |= (1 << 7); warn |= (1 << 9); warn |= (1 << 12); } // Bit 7,9,12: 充放电/环境低温告警 (3KU检测0x0128)
                    if (model.WarnLowCapacity) warn |= (1 << 13);                     // Bit 13: 系统低压关机前告警
                    // Bit 14~15: 电池类型 00: 磷酸铁锂电池
                    return warn;


                case 0x0023: // Max Discharge Current (10mA, 正值, PDF Page 9)
                    if (!model.CanDischarge)
                        return 0;
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.MaxDischargeCurrent * 100)));

                case 0x0024: // Extended Error (PDF Page 9, 13)
                    return 0x0000;

                case 0x0025: // Maximum Cell Voltage (1mV, PDF Page 9)
                    {
                        int cellCountMax = model.GetEffectiveCellCount();
                        ushort maxMv, minMv;
                        model.GetCellMillivolts(0, cellCountMax, out maxMv, out minMv);
                        return maxMv;
                    }

                case 0x0026: // Minimum Cell Voltage (1mV, PDF Page 9)
                    {
                        int cellCountMin = model.GetEffectiveCellCount();
                        ushort maxMv, minMv;
                        model.GetCellMillivolts(1, cellCountMin, out maxMv, out minMv);
                        return minMv;
                    }

                case 0x0027: // Maximum Cell Voltage Number (PDF Page 9)
                    return 1;

                case 0x0028: // Minimum Cell Voltage Number (PDF Page 9)
                    return 2;

                case 0x0029: // Cell Series (PDF Page 9)
                    return (ushort)model.GetEffectiveCellCount();

                default:
                    // 单体电压上报 0x0071 ~ 0x0080 (1mV, PDF Page 14-15)
                    if (regAddress >= 0x0071 && regAddress <= 0x0080)
                    {
                        int nCells = model.GetEffectiveCellCount();
                        int cellIdx = regAddress - 0x0070;
                        if (cellIdx <= nCells)
                        {
                            ushort maxMv, minMv;
                            return model.GetCellMillivolts(cellIdx - 1, nCells, out maxMv, out minMv);
                        }
                    }
                    return 0x0000;
            }
        }

        public int MasterPollPhaseCount
        {
            get { return 1; }
        }

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            byte[] frame = new byte[8];
            frame[0] = targetId;
            frame[1] = 0x03;
            frame[2] = 0x00; frame[3] = 0x13; // 0x0013 (从状态字开始读)
            frame[4] = 0x00; frame[5] = 0x11; // 17 寄存器 (0x0013~0x0023 覆盖状态、告警、模拟量与充放电限流)
            ushort crc = ModbusCrc.Calculate(frame, 6);
            frame[6] = (byte)(crc >> 8);
            frame[7] = (byte)(crc & 0xFF);
            return frame;
        }

        public bool TryDecodeMasterResponse(byte[] rxBuffer, int rxLength, BmsDataModel model, out string decodedInfo)
        {
            decodedInfo = "";
            if (rxLength < 7) return false;
            if (!ModbusCrc.Check(rxBuffer, rxLength)) return false;
            if (rxBuffer[1] != 0x03 && rxBuffer[1] != 0x06) return false;

            if (rxBuffer[1] == 0x06)
            {
                ushort addr = (ushort)((rxBuffer[2] << 8) | rxBuffer[3]);
                ushort val = (ushort)((rxBuffer[4] << 8) | rxBuffer[5]);
                decodedInfo = string.Format("[GROWATT] 从机确认写入: 寄存器 0x{0:X4} = 0x{1:X4}", addr, val);
                return true;
            }

            byte byteCount = rxBuffer[2];
            if (rxLength != 3 + byteCount + 2) return false;

            lock (model.SyncRoot)
            {
                if (byteCount >= 34) // 17 寄存器 (0x0013~0x0023)
                {
                    // 0x0013: 状态信息
                    ushort status = (ushort)((rxBuffer[3] << 8) | rxBuffer[4]);
                    model.StatusBalancing = (status & (1 << 3)) != 0;
                    model.StatusSleep = (status & (1 << 4)) != 0;
                    model.StatusDischargeEnable = (status & (1 << 5)) != 0;
                    model.StatusChargeEnable = (status & (1 << 6)) != 0;
                    model.StatusForceCharge = (status & (1 << 12)) != 0;

                    // 0x0014: 保护与故障信息 (Error Code)
                    ushort err = (ushort)((rxBuffer[5] << 8) | rxBuffer[6]);
                    model.ProtOverCurrent = (err & ((1 << 0) | (1 << 11))) != 0;
                    model.ProtShortCircuit = (err & (1 << 1)) != 0;
                    model.ProtOverVolt = (err & (1 << 2)) != 0;
                    model.ProtUnderVolt = (err & (1 << 3)) != 0;
                    model.ProtHighTemp = (err & ((1 << 4) | (1 << 5) | (1 << 12) | (1 << 13))) != 0;
                    model.ProtUnderTemp = (err & ((1 << 6) | (1 << 7) | (1 << 14))) != 0;
                    model.ProtSoftStart = (err & (1 << 8)) != 0;
                    model.ProtSystemFault = (err & (1 << 9)) != 0;
                    model.WarnVoltDiff = (err & (1 << 10)) != 0;

                    // 0x0015: SOC (1%)
                    model.SOC = (rxBuffer[7] << 8) | rxBuffer[8];
                    // 0x0016: 电池总压 (10mV)
                    model.Voltage = ((rxBuffer[9] << 8) | rxBuffer[10]) / 100.0;
                    // 0x0017: 充放电流 (10mA)
                    model.Current = (short)((rxBuffer[11] << 8) | rxBuffer[12]) / 100.0;
                    // 0x0018: 最高温度 (1°C)
                    model.Temperature = (short)((rxBuffer[13] << 8) | rxBuffer[14]);
                    // 0x0019: 最大充电电流 (10mA)
                    model.MaxChargeCurrent = ((rxBuffer[15] << 8) | rxBuffer[16]) / 100.0;
                    // 0x001A: 剩余容量 (10mAh)
                    model.RemainingCapacity = ((rxBuffer[17] << 8) | rxBuffer[18]) / 100.0;
                    // 0x001B: 满充容量 (10mAh)
                    model.FullCapacity = ((rxBuffer[19] << 8) | rxBuffer[20]) / 100.0;

                    // 0x001E: 循环次数
                    model.CycleCount = (rxBuffer[25] << 8) | rxBuffer[26];
                    // 0x0020: SOH (Bit 0~6: SOH Counter, Bit 7: SOH Flag)
                    ushort sohRaw = (ushort)((rxBuffer[29] << 8) | rxBuffer[30]);
                    byte soh = (byte)(sohRaw & 0x7F);
                    if (soh > 0 && soh <= 100) model.SOH = soh;

                    // 0x0021: CV 恒压点 (10mV)
                    model.CVVoltage = ((rxBuffer[31] << 8) | rxBuffer[32]) / 100.0;

                    // 0x0022: 告警信息 (Warning Code)
                    ushort warn = (ushort)((rxBuffer[33] << 8) | rxBuffer[34]);
                    model.WarnSingleOverVolt = (warn & (1 << 0)) != 0;
                    model.WarnSingleUnderVolt = (warn & (1 << 1)) != 0;
                    model.WarnGlobalOverVolt = (warn & (1 << 2)) != 0;
                    model.WarnGlobalUnderVolt = (warn & (1 << 3)) != 0;
                    model.WarnOverCurrent = (warn & ((1 << 4) | (1 << 5))) != 0;
                    model.WarnHighTemp = (warn & ((1 << 6) | (1 << 8) | (1 << 10) | (1 << 11))) != 0;
                    model.WarnLowTemp = (warn & ((1 << 7) | (1 << 9) | (1 << 12))) != 0;
                    model.WarnLowCapacity = (warn & (1 << 13)) != 0;

                    // 0x0023: 最大放电电流 (10mA)
                    model.MaxDischargeCurrent = ((rxBuffer[35] << 8) | rxBuffer[36]) / 100.0;
                }
                else if (byteCount >= 30) // 15 寄存器 (0x0014~0x0022 兼容格式)
                {
                    // 0x0014: 保护与故障信息
                    ushort err = (ushort)((rxBuffer[3] << 8) | rxBuffer[4]);
                    model.ProtOverCurrent = (err & ((1 << 0) | (1 << 11))) != 0;
                    model.ProtShortCircuit = (err & (1 << 1)) != 0;
                    model.ProtOverVolt = (err & (1 << 2)) != 0;
                    model.ProtUnderVolt = (err & (1 << 3)) != 0;
                    model.ProtHighTemp = (err & ((1 << 4) | (1 << 5) | (1 << 12) | (1 << 13))) != 0;
                    model.ProtUnderTemp = (err & ((1 << 6) | (1 << 7) | (1 << 14))) != 0;
                    model.ProtSoftStart = (err & (1 << 8)) != 0;
                    model.ProtSystemFault = (err & (1 << 9)) != 0;
                    model.WarnVoltDiff = (err & (1 << 10)) != 0;

                    model.SOC = (rxBuffer[5] << 8) | rxBuffer[6];
                    model.Voltage = ((rxBuffer[7] << 8) | rxBuffer[8]) / 100.0;
                    model.Current = (short)((rxBuffer[9] << 8) | rxBuffer[10]) / 100.0;
                    model.Temperature = (short)((rxBuffer[11] << 8) | rxBuffer[12]);
                    model.MaxChargeCurrent = ((rxBuffer[13] << 8) | rxBuffer[14]) / 100.0;
                    model.RemainingCapacity = ((rxBuffer[15] << 8) | rxBuffer[16]) / 100.0;
                    model.FullCapacity = ((rxBuffer[17] << 8) | rxBuffer[18]) / 100.0;

                    model.CycleCount = (rxBuffer[23] << 8) | rxBuffer[24];
                    ushort sohRaw = (ushort)((rxBuffer[27] << 8) | rxBuffer[28]);
                    byte soh = (byte)(sohRaw & 0x7F);
                    if (soh > 0 && soh <= 100) model.SOH = soh;

                    model.CVVoltage = ((rxBuffer[29] << 8) | rxBuffer[30]) / 100.0;

                    ushort warn = (ushort)((rxBuffer[31] << 8) | rxBuffer[32]);
                    model.WarnSingleOverVolt = (warn & (1 << 0)) != 0;
                    model.WarnSingleUnderVolt = (warn & (1 << 1)) != 0;
                    model.WarnGlobalOverVolt = (warn & (1 << 2)) != 0;
                    model.WarnGlobalUnderVolt = (warn & (1 << 3)) != 0;
                    model.WarnOverCurrent = (warn & ((1 << 4) | (1 << 5))) != 0;
                    model.WarnHighTemp = (warn & ((1 << 6) | (1 << 8) | (1 << 10) | (1 << 11))) != 0;
                    model.WarnLowTemp = (warn & ((1 << 7) | (1 << 9) | (1 << 12))) != 0;
                    model.WarnLowCapacity = (warn & (1 << 13)) != 0;
                }
            }

            decodedInfo = string.Format("[GROWATT] 解析成功: 电压={0:F2}V, SOC={1:F0}%, 电流={2:F2}A, 容量={3:F1}/{4:F1}Ah, CV={5:F2}V, 充放限流={6:F1}/{7:F1}A",
                model.Voltage, model.SOC, model.Current, model.RemainingCapacity, model.FullCapacity, model.CVVoltage, model.MaxChargeCurrent, model.MaxDischargeCurrent);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. VOLTRONIC (Modbus RTU) 协议处理器 (严格按 Voltronic Inverter & BMS 485 Protocol 规范实现)
    // ─────────────────────────────────────────────────────────────
    // ─────────────────────────────────────────────────────────────
    // 3. VOLTRONIC (Modbus RTU) 协议处理器 (严格按 Voltronic Inverter & BMS 485 Protocol 规范实现)
    // ─────────────────────────────────────────────────────────────
    public class VoltronicModbusHandler : IBmsProtocolHandler
    {
        public string ProtocolName
        {
            get { return "VOLTRONIC (Modbus RTU)"; }
        }

        public bool TryProcessQuery(byte[] rxBuffer, int rxLength, BmsDataModel model, bool replyAllIds, int specificId, out byte[] txResponse, out string decodedInfo)
        {
            txResponse = null;
            decodedInfo = "";

            if (rxLength != 8) return false;
            if (!ModbusCrc.Check(rxBuffer, 8)) return false;

            byte id = rxBuffer[0];
            byte cmd = rxBuffer[1];
            ushort regStart = (ushort)((rxBuffer[2] << 8) | rxBuffer[3]);
            ushort regCount = (ushort)((rxBuffer[4] << 8) | rxBuffer[5]);

            if (cmd != 0x03 || regCount == 0 || regCount > 100) return false;

            // Voltronic 协议合法寄存器起始地址范围过滤 (严格限定日月元协议定义的轮询分段，防止误响应 Growatt 0x0001 len 35 / 0x0014 len 15 或 CVTE 0x0020 len 12，使逆变器产生 56 通信丢失告警):
            bool isValidVoltronicAddr = (regStart == 0x0030 && regCount <= 6) ||
                                        (regStart == 0x0060 && regCount <= 10) ||
                                        (regStart == 0x0070 && regCount <= 5) ||
                                        (regStart == 0x0025 && regCount <= 11) ||
                                        (regStart == 0x0026 && regCount <= 10) ||
                                        (regStart == 0x0040 && regCount <= 11) ||
                                        (regStart == 0x0050 && regCount <= 6) ||
                                        (regStart == 0x0001 && regCount <= 2) ||
                                        (regStart == 0x0010 && regCount == 1) ||
                                        (regStart == 0x0011 && regCount <= 16);
            if (!isValidVoltronicAddr)
                return false;


            if (!replyAllIds && id != specificId)
            {
                decodedInfo = string.Format("收到对 ID:{0:X2} 的查询 (已按单包规则忽略)", id);
                return false;
            }

            // 日月元 (Voltronic) 485 协议响应帧规范:
            // [ID (1B)][CMD 0x03 (1B)][Data Length Hi (1B)][Data Length Lo (1B)][Data 0 Hi][Data 0 Lo]...[CRC Hi][CRC Lo]
            // 其中 Data Length 为 16 位寄存器数量 (如 0x0006 表示 6 个寄存器即 12 字节数据)
            int byteCount = regCount * 2;
            byte[] resp = new byte[4 + byteCount + 2];
            resp[0] = id;
            resp[1] = 0x03;
            resp[2] = (byte)(regCount >> 8);
            resp[3] = (byte)(regCount & 0xFF);

            lock (model.SyncRoot)
            {
                for (int i = 0; i < regCount; i++)
                {
                    ushort regAddr = (ushort)(regStart + i);
                    ushort val = GetVoltronicRegisterValue(regAddr, model);
                    resp[4 + i * 2] = (byte)(val >> 8);
                    resp[4 + i * 2 + 1] = (byte)(val & 0xFF);
                }

                string statusTag = "";
                if (model.HasProtection) statusTag += string.Format(" [保护跳闸: {0}]", model.GetActiveProtectionsSummary());
                if (model.HasWarning) statusTag += string.Format(" [告警提示: {0}]", model.GetActiveWarningsSummary());

                decodedInfo = string.Format("[VOLTRONIC] 应答 ID:{0:X2} 读 0x{1:X4} ({2}寄存器) -> 电压:{3:F2}V, SOC:{4:F0}%, 电流:{5:F2}A, CV:{6:F2}V, 截止:{7:F2}V, 充限:{8:F1}A, 放限:{9:F1}A{10}",
                    id, regStart, regCount, model.Voltage, model.SOC, model.Current, model.CVVoltage, model.CutoffVoltage, model.CanCharge ? model.MaxChargeCurrent : 0.0, model.CanDischarge ? model.MaxDischargeCurrent : 0.0, statusTag);
            }

            ushort crc = ModbusCrc.Calculate(resp, 4 + byteCount);
            resp[4 + byteCount] = (byte)(crc >> 8);
            resp[4 + byteCount + 1] = (byte)(crc & 0xFF);

            txResponse = resp;
            return true;
        }

        private ushort GetVoltronicRegisterValue(ushort regAddress, BmsDataModel model)
        {
            switch (regAddress)
            {
                // ── 1. 版本与电芯/温度总数 (0x0001~0x0025, PDF Page 3-4) ──
                case 0x0001: return 0x0001; // Protocol Type
                case 0x0002: return 0x0001; // Protocol Version
                case 0x0010: return (ushort)model.GetEffectiveCellCount(); // Number of cell: L pcs
                case 0x0025: return 4;      // Number of temperature sensor: M pcs (PDF Page 4)
                case 0x0040: return (ushort)model.GetEffectiveCellCount(); // Number of cell: L (PDF Page 5)
                case 0x0050: return 4;      // Number of temperature sensor: M (PDF Page 5)

                // ── 2. 模拟量状态查询组 (0x0030~0x0035, PDF Page 4) ──
                case 0x0030: // Module charge current (0.1A)
                    return (ushort)(model.Current > 0.05 ? Math.Round(model.Current * 10) : 0);

                case 0x0031: // Module discharge current (0.1A, 正值)
                    return (ushort)(model.Current < -0.05 ? Math.Round(-model.Current * 10) : 0);

                case 0x0032: // Module voltage (0.1V, e.g. 530 = 53.0V, 280 = 28.0V)
                    return (ushort)Math.Round(model.Voltage * 10);

                case 0x0033: // SOC (1%, 0~100)
                    return (ushort)Math.Max(0, Math.Min(100, (int)Math.Round(model.SOC)));

                case 0x0034: // Module total capacity Hi word (mAH, 32-bit: FullCapacity * 1000)
                    uint capMah = (uint)Math.Max(0, (int)Math.Round(model.FullCapacity * 1000));
                    return (ushort)(capMah >> 16);

                case 0x0035: // Module total capacity Lo word (mAH)
                    uint capMahLo = (uint)Math.Max(0, (int)Math.Round(model.FullCapacity * 1000));
                    return (ushort)(capMahLo & 0xFFFF);

                // ── 3. 告警与保护状态字组 (0x0060~0x0069, 00H正常/01H偏低/02H偏高/F0H错误, PDF Page 5) ──
                case 0x0060: // Module charge voltage state (02H: overvoltage)
                    return (ushort)((model.WarnGlobalOverVolt || model.ProtOverVolt) ? 0x0002 : 0x0000);

                case 0x0061: // Module discharge voltage state (01H: undervoltage)
                    return (ushort)((model.WarnGlobalUnderVolt || model.ProtUnderVolt) ? 0x0001 : 0x0000);

                case 0x0062: // Cell charge voltage state (02H: single overvoltage)
                    return (ushort)((model.WarnSingleOverVolt || model.ProtOverVolt) ? 0x0002 : 0x0000);

                case 0x0063: // Cell discharge voltage state (01H: single undervoltage)
                    return (ushort)((model.WarnSingleUnderVolt || model.ProtUnderVolt) ? 0x0001 : 0x0000);

                case 0x0064: // Module charge current state (02H: charge overcurrent)
                    return (ushort)((model.WarnOverCurrent || model.ProtOverCurrent) ? 0x0002 : 0x0000);

                case 0x0065: // Module discharge current state (02H: discharge overcurrent)
                    return (ushort)((model.WarnOverCurrent || model.ProtOverCurrent || model.ProtShortCircuit) ? 0x0002 : 0x0000);

                case 0x0066: // Module charge temperature state (01H: low temp, 02H: high temp)
                    if (model.WarnLowTemp || model.ProtUnderTemp) return 0x0001;
                    if (model.WarnHighTemp || model.ProtHighTemp) return 0x0002;
                    return 0x0000;

                case 0x0067: // Module discharge temperature state (01H: low temp, 02H: high temp)
                    if (model.WarnLowTemp || model.ProtUnderTemp) return 0x0001;
                    if (model.WarnHighTemp || model.ProtHighTemp) return 0x0002;
                    return 0x0000;

                case 0x0068: // Cell charge temperature state (01H: low temp, 02H: high temp)
                    if (model.WarnLowTemp || model.ProtUnderTemp) return 0x0001;
                    return (ushort)((model.WarnHighTemp || model.ProtHighTemp) ? 0x0002 : 0x0000);

                case 0x0069: // Cell discharge temperature state (01H: low temp, 02H: high temp)
                    if (model.WarnLowTemp || model.ProtUnderTemp) return 0x0001;
                    return (ushort)((model.WarnHighTemp || model.ProtHighTemp) ? 0x0002 : 0x0000);

                // ── 4. 充放电控制参数组 (0x0070~0x0074, PDF Page 5-6) ──
                case 0x0070: // Charge voltage limit / CV Voltage (0.1V, PDF Page 5)
                    return (ushort)Math.Max(0, (int)Math.Round(model.CVVoltage * 10));

                case 0x0071: // Discharge voltage limit / Cutoff Voltage (0.1V, PDF Page 5)
                    return (ushort)Math.Max(0, (int)Math.Round(model.CutoffVoltage * 10));

                case 0x0072: // Charge current limit (0.1A, PDF Page 5)
                    if (!model.CanCharge)
                        return 0;
                    return (ushort)Math.Max(0, (int)Math.Round(model.MaxChargeCurrent * 10));

                case 0x0073: // Discharge current limit (0.1A, PDF Page 5)
                    if (!model.CanDischarge)
                        return 0;
                    return (ushort)Math.Max(0, (int)Math.Round(model.MaxDischargeCurrent * 10));

                case 0x0074: // Charge, discharge status (PDF Page 5-6)
                    ushort vStatus = 0;
                    if (model.CanCharge) vStatus |= (1 << 7);        // Bit 7: Charge enable (1: yes, 0: request stop charge)
                    if (model.CanDischarge) vStatus |= (1 << 6);     // Bit 6: Discharge enable (1: yes, 0: request stop discharge)
                    if (model.StatusForceCharge) vStatus |= (1 << 5);// Bit 5: Charge immediately (强制充电请求)
                    if (model.StatusFullCharge) vStatus |= (1 << 3); // Bit 3: Full charge request (满充请求)
                    return vStatus;


                // ── 5. 温度传感器组 (0x0026~0x002F, 0.1K 开尔文温度, PDF Page 4) 与电芯状态 ──
                default:
                    if (regAddress >= 0x0026 && regAddress <= 0x002F)
                    {
                        // 0.1K = (273.15 + Temperature) * 10
                        return (ushort)Math.Round((273.15 + model.Temperature) * 10);
                    }
                    if (regAddress >= 0x0011 && regAddress <= 0x0024)
                    {
                        int nCells = model.GetEffectiveCellCount();
                        int cellIdx = regAddress - 0x0010;
                        if (cellIdx <= nCells)
                        {
                            ushort maxMv, minMv;
                            ushort cMv = model.GetCellMillivolts(cellIdx - 1, nCells, out maxMv, out minMv);
                            return (ushort)Math.Round(cMv / 100.0); // 0.1V
                        }
                    }
                    if (regAddress >= 0x0041 && regAddress <= 0x004A)
                    {
                        byte cState = 0x00;
                        if (model.WarnSingleOverVolt || model.ProtOverVolt) cState = 0x02;
                        else if (model.WarnSingleUnderVolt || model.ProtUnderVolt) cState = 0x01;
                        return (ushort)((cState << 8) | cState);
                    }
                    if (regAddress >= 0x0051 && regAddress <= 0x0055)
                    {
                        byte tState = 0x00;
                        if (model.WarnHighTemp || model.ProtHighTemp) tState = 0x02;
                        else if (model.WarnLowTemp || model.ProtUnderTemp) tState = 0x01;
                        return (ushort)((tState << 8) | tState);
                    }
                    return 0x0000;
            }
        }

        public int MasterPollPhaseCount
        {
            get { return 3; }
        }

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            byte[] frame = new byte[8];
            frame[0] = targetId;
            frame[1] = 0x03;
            if (pollPhase == 0)
            {
                frame[2] = 0x00; frame[3] = 0x30; frame[4] = 0x00; frame[5] = 0x06; // 0x0030~0x0035 实时充放电流/总压/SOC/总容量
            }
            else if (pollPhase == 1)
            {
                frame[2] = 0x00; frame[3] = 0x60; frame[4] = 0x00; frame[5] = 0x0A; // 0x0060~0x0069 组端与单体告警/保护矩阵
            }
            else
            {
                frame[2] = 0x00; frame[3] = 0x70; frame[4] = 0x00; frame[5] = 0x05; // 0x0070~0x0074 CV/截止点/最大充放限流/状态字
            }
            ushort crc = ModbusCrc.Calculate(frame, 6);
            frame[6] = (byte)(crc >> 8);
            frame[7] = (byte)(crc & 0xFF);
            return frame;
        }

        public bool TryDecodeMasterResponse(byte[] rxBuffer, int rxLength, BmsDataModel model, out string decodedInfo)
        {
            decodedInfo = "";
            if (rxLength < 7) return false;
            if (!ModbusCrc.Check(rxBuffer, rxLength)) return false;
            if (rxBuffer[1] != 0x03 && rxBuffer[1] != 0x06) return false;

            if (rxBuffer[1] == 0x06)
            {
                ushort addr = (ushort)((rxBuffer[2] << 8) | rxBuffer[3]);
                ushort val = (ushort)((rxBuffer[4] << 8) | rxBuffer[5]);
                decodedInfo = string.Format("[VOLTRONIC] 从机确认写入: 寄存器 0x{0:X4} = 0x{1:X4}", addr, val);
                return true;
            }

            int dataOffset = 4;
            int regCount = 0;

            if (rxBuffer[2] == 0x00)
            {
                regCount = (rxBuffer[2] << 8) | rxBuffer[3];
                dataOffset = 4;
            }
            else
            {
                regCount = rxBuffer[2] / 2;
                dataOffset = 3;
            }

            if (rxLength < dataOffset + regCount * 2 + 2) return false;

            lock (model.SyncRoot)
            {
                if (regCount == 6) // Phase 0: 0x0030~0x0035 模拟量
                {
                    double chgI = ((rxBuffer[dataOffset] << 8) | rxBuffer[dataOffset + 1]) / 10.0;
                    double disI = ((rxBuffer[dataOffset + 2] << 8) | rxBuffer[dataOffset + 3]) / 10.0;
                    model.Current = chgI > 0 ? chgI : -disI;
                    model.Voltage = ((rxBuffer[dataOffset + 4] << 8) | rxBuffer[dataOffset + 5]) / 10.0;
                    model.SOC = (rxBuffer[dataOffset + 6] << 8) | rxBuffer[dataOffset + 7];
                    uint capMah = ((uint)rxBuffer[dataOffset + 8] << 24) | ((uint)rxBuffer[dataOffset + 9] << 16) |
                                  ((uint)rxBuffer[dataOffset + 10] << 8) | (uint)rxBuffer[dataOffset + 11];
                    model.FullCapacity = capMah / 1000.0;
                    decodedInfo = string.Format("[VOLTRONIC] 模拟量遥测: 电压={0:F1}V, 电流={1:F1}A, SOC={2:F0}%, 容量={3:F1}Ah", model.Voltage, model.Current, model.SOC, model.FullCapacity);
                    return true;
                }
                else if (regCount == 10) // Phase 1: 0x0060~0x0069 告警与保护状态
                {
                    ushort s0 = (ushort)((rxBuffer[dataOffset] << 8) | rxBuffer[dataOffset + 1]);     // 0x0060 组端过压
                    ushort s1 = (ushort)((rxBuffer[dataOffset + 2] << 8) | rxBuffer[dataOffset + 3]); // 0x0061 组端欠压
                    ushort s2 = (ushort)((rxBuffer[dataOffset + 4] << 8) | rxBuffer[dataOffset + 5]); // 0x0062 单体过压
                    ushort s3 = (ushort)((rxBuffer[dataOffset + 6] << 8) | rxBuffer[dataOffset + 7]); // 0x0063 单体欠压
                    ushort s4 = (ushort)((rxBuffer[dataOffset + 8] << 8) | rxBuffer[dataOffset + 9]); // 0x0064 充电过流
                    ushort s5 = (ushort)((rxBuffer[dataOffset + 10] << 8) | rxBuffer[dataOffset + 11]); // 0x0065 放电过流
                    ushort s6 = (ushort)((rxBuffer[dataOffset + 12] << 8) | rxBuffer[dataOffset + 13]); // 0x0066 充电温度
                    ushort s7 = (ushort)((rxBuffer[dataOffset + 14] << 8) | rxBuffer[dataOffset + 15]); // 0x0067 放电温度
                    ushort s8 = (ushort)((rxBuffer[dataOffset + 16] << 8) | rxBuffer[dataOffset + 17]); // 0x0068 单芯充电温度
                    ushort s9 = (ushort)((rxBuffer[dataOffset + 18] << 8) | rxBuffer[dataOffset + 19]); // 0x0069 单芯放电温度

                    model.WarnGlobalOverVolt = (s0 == 0x02 || s0 == 0xF0);
                    model.WarnGlobalUnderVolt = (s1 == 0x01 || s1 == 0xF0);
                    model.WarnSingleOverVolt = (s2 == 0x02 || s2 == 0xF0);
                    model.WarnSingleUnderVolt = (s3 == 0x01 || s3 == 0xF0);
                    model.WarnOverCurrent = (s4 == 0x02 || s5 == 0x02 || s4 == 0xF0 || s5 == 0xF0);
                    model.WarnHighTemp = (s6 == 0x02 || s7 == 0x02 || s8 == 0x02 || s9 == 0x02);
                    model.WarnLowTemp = (s6 == 0x01 || s7 == 0x01 || s8 == 0x01 || s9 == 0x01);

                    model.ProtOverVolt = (s0 == 0x02 || s2 == 0x02 || s0 == 0xF0 || s2 == 0xF0);
                    model.ProtUnderVolt = (s1 == 0x01 || s3 == 0x01 || s1 == 0xF0 || s3 == 0xF0);
                    model.ProtOverCurrent = (s4 == 0x02 || s5 == 0x02 || s4 == 0xF0 || s5 == 0xF0);
                    model.ProtHighTemp = (s6 == 0x02 || s7 == 0x02 || s8 == 0x02 || s9 == 0x02);
                    model.ProtUnderTemp = (s6 == 0x01 || s7 == 0x01 || s8 == 0x01 || s9 == 0x01);

                    decodedInfo = string.Format("[VOLTRONIC] 告警矩阵遥测更新完成 (过压={0}, 欠压={1}, 过流={2}, 温态={3})",
                        s0, s1, s4, s6);
                    return true;
                }
                else if (regCount >= 3 && regCount <= 5) // Phase 2: 0x0070~0x0074 充放电控制参数
                {
                    model.CVVoltage = ((rxBuffer[dataOffset] << 8) | rxBuffer[dataOffset + 1]) / 10.0;
                    if (regCount >= 4)
                    {
                        model.CutoffVoltage = ((rxBuffer[dataOffset + 2] << 8) | rxBuffer[dataOffset + 3]) / 10.0;
                        model.MaxChargeCurrent = ((rxBuffer[dataOffset + 4] << 8) | rxBuffer[dataOffset + 5]) / 10.0;
                        model.MaxDischargeCurrent = ((rxBuffer[dataOffset + 6] << 8) | rxBuffer[dataOffset + 7]) / 10.0;
                    }
                    if (regCount >= 5)
                    {
                        ushort st74 = (ushort)((rxBuffer[dataOffset + 8] << 8) | rxBuffer[dataOffset + 9]);
                        model.StatusChargeEnable = (st74 & (1 << 7)) != 0;
                        model.StatusDischargeEnable = (st74 & (1 << 6)) != 0;
                        model.StatusForceCharge = (st74 & (1 << 5)) != 0;
                        model.StatusFullCharge = (st74 & (1 << 3)) != 0;
                    }
                    decodedInfo = string.Format("[VOLTRONIC] 控制参数遥测: CV={0:F1}V, 截止={1:F1}V, 充电限流={2:F1}A, 放电限流={3:F1}A",
                        model.CVVoltage, model.CutoffVoltage, model.MaxChargeCurrent, model.MaxDischargeCurrent);
                    return true;
                }
            }

            decodedInfo = string.Format("[VOLTRONIC] 接收并校验通过 {0} 寄存器报文", regCount);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. PYLONTECH (Pylon RS485 ASCII) 协议处理器 (严格按 PYLON low voltage Protocol RS485 规范实现)
    // ─────────────────────────────────────────────────────────────
    public class PylontechAsciiHandler : IBmsProtocolHandler
    {
        public string ProtocolName
        {
            get { return "PYLONTECH (RS485 ASCII)"; }
        }

        public bool TryProcessQuery(byte[] rxBuffer, int rxLength, BmsDataModel model, bool replyAllIds, int specificId, out byte[] txResponse, out string decodedInfo)
        {
            txResponse = null;
            decodedInfo = "";

            if (rxLength < 18) return false;
            if (rxBuffer[0] != (byte)'~' || rxBuffer[rxLength - 1] != 0x0D) return false;

            byte[] frameSlice = rxBuffer;
            if (rxBuffer.Length != rxLength)
            {
                frameSlice = new byte[rxLength];
                Array.Copy(rxBuffer, 0, frameSlice, 0, rxLength);
            }
            if (!PylonChecksum.Verify(frameSlice)) return false;

            string asciiStr = Encoding.ASCII.GetString(rxBuffer, 0, rxLength);
            if (asciiStr.Length < 18) return false;

            // 提取协议字段: ~ VER(2) ADR(2) CID1(2) CID2(2) LENGTH(4) [INFO] CHKSUM(4) \r
            string ver = asciiStr.Substring(1, 2);
            string adrStr = asciiStr.Substring(3, 2);
            string cid1 = asciiStr.Substring(5, 2).ToUpper();
            string cid2 = asciiStr.Substring(7, 2).ToUpper();

            if (cid1 != "46") return false;

            byte adrVal = 0;
            byte.TryParse(adrStr, System.Globalization.NumberStyles.HexNumber, null, out adrVal);

            // 地址过滤 (如果未开启全ID应答)
            if (!replyAllIds)
            {
                // specificId 匹配直接数值(如1或2)、单组(0x02+ID-1)或多组高低位
                if (adrVal != specificId && (adrVal & 0x0F) != specificId && adrVal != (0x10 + specificId))
                {
                    decodedInfo = string.Format("收到对 ADR:{0} 的查询 (已按单包规则忽略)", adrStr);
                    return false;
                }
            }

            string infoData = "";
            string rtnCode = "00"; // 00H = Normal

            lock (model.SyncRoot)
            {
                // ─────────────────────────────────────────────────────────────
                // 1. 系统级通信指令 (Chapter 2: 60H, 61H, 62H, 63H, 64H)
                // ─────────────────────────────────────────────────────────────
                if (cid2 == "60") // 2.1 获取电池组系统基本信息 (Page 12-13)
                {
                    StringBuilder sb = new StringBuilder();
                    // 1. 主机设备名称 (10 字节 ASCII -> 20 HEX字符)
                    string batName = "Force_L   "; // 10 chars
                    foreach (char c in batName) sb.Append(((byte)c).ToString("X2"));

                    // 2. 主机厂商名称 (20 字节 ASCII -> 40 HEX字符)
                    string mfgName = "Pylon               "; // 20 chars
                    foreach (char c in mfgName) sb.Append(((byte)c).ToString("X2"));

                    // 3. 主机软件版本 (2 字节 -> 4 HEX字符)
                    sb.Append("0100"); // V1.0

                    // 4. 电池数量 (1 字节 -> 2 HEX字符)
                    sb.Append("01"); // 1 台电池包

                    // 5. 电池 1 的条形码 (16 字节 ASCII -> 32 HEX字符)
                    string barcode = "PYLON20260817001"; // 16 chars
                    foreach (char c in barcode) sb.Append(((byte)c).ToString("X2"));

                    infoData = sb.ToString();
                    decodedInfo = string.Format("[PYLON-60H System Basic] 响应系统基本信息: 1台电池, Force_L / Pylon, V1.0");
                }
                else if (cid2 == "61") // 2.2 获取电池组系统运行模拟量信息 (Page 14-16, 26项, 49字节 / 98 HEX字符)
                {
                    int uVoltMv = (int)Math.Max(0, Math.Min(65535, Math.Round(model.Voltage * 1000)));
                    // 电流: 2 字节有符号整数, 精度 0.01A (10mA) / 0.1A (PDF page 14: Accuracy 2, Page 15: 0x61A8 = 25000 -> 25.00A)
                    short sCurr10Ma = (short)Math.Max(-32768, Math.Min(32767, Math.Round(model.Current * 100)));
                    byte uSoc = (byte)Math.Max(0, Math.Min(100, Math.Round(model.SOC)));
                    byte uSoh = (byte)Math.Max(0, Math.Min(100, Math.Round(model.SOH)));

                    int cellCount = model.GetEffectiveCellCount();
                    ushort cellVoltMv = (ushort)Math.Max(0, Math.Min(65535, Math.Round((model.Voltage / cellCount) * 1000)));
                    ushort cellVoltMax, cellVoltMin;
                    model.GetCellMillivolts(0, cellCount, out cellVoltMax, out cellVoltMin);

                    // 温度: 绝对开尔文温度 * 10 (Kelvin: 0.1K, e.g. 29.0°C = 290 + 2731 = 3021; 常态无偏置，仅告警时附加温差)
                    ushort uKelvinAvg = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.Temperature * 10 + 2731)));
                    ushort uKelvinMax = (ushort)Math.Min(65535, uKelvinAvg + (model.WarnHighTemp ? 40 : 0));
                    ushort uKelvinMin = (ushort)Math.Max(0, uKelvinAvg - (model.WarnLowTemp ? 40 : 0));

                    StringBuilder sb = new StringBuilder();
                    sb.Append(((ushort)uVoltMv).ToString("X4"));          // 1. 电池组系统总平均电压 (mV)
                    sb.Append(((ushort)sCurr10Ma).ToString("X4"));         // 2. 电池组系统总电流 (10mA, 有符号)
                    sb.Append(uSoc.ToString("X2"));                       // 3. 电池组系统 SOC (%)
                    sb.Append(((ushort)Math.Max(0, Math.Min(65535, model.CycleCount))).ToString("X4")); // 4. 平均循环次数
                    sb.Append(((ushort)Math.Max(0, Math.Min(65535, model.CycleCount))).ToString("X4")); // 5. 最大循环次数
                    sb.Append(uSoh.ToString("X2"));                       // 6. 平均 SOH (%)
                    sb.Append(uSoh.ToString("X2"));                       // 7. 最小 SOH (%)
                    sb.Append(cellVoltMax.ToString("X4"));                 // 8. 单芯最高电压 (mV)
                    sb.Append("0001");                                    // 9. 单芯最高电压所在模块 (组0 模块1)
                    sb.Append(cellVoltMin.ToString("X4"));                 // 10. 单芯最低电压 (mV)
                    sb.Append("0001");                                    // 11. 单芯最低电压所在模块
                    sb.Append(uKelvinAvg.ToString("X4"));                  // 12. 单芯平均温度 (0.1K)
                    sb.Append(uKelvinMax.ToString("X4"));                  // 13. 单芯最高温度
                    sb.Append("0001");                                    // 14. 单芯最高温度所在模块
                    sb.Append(uKelvinMin.ToString("X4"));                  // 15. 单芯最低温度
                    sb.Append("0001");                                    // 16. 单芯最低温度所在模块
                    sb.Append(uKelvinAvg.ToString("X4"));                  // 17. MOSFET 平均温度
                    sb.Append(uKelvinMax.ToString("X4"));                  // 18. MOSFET 最高温度
                    sb.Append("0001");                                    // 19. MOSFET 最高温度所在模块
                    sb.Append(uKelvinMin.ToString("X4"));                  // 20. MOSFET 最低温度
                    sb.Append("0001");                                    // 21. MOSFET 最低温度所在模块
                    sb.Append(uKelvinAvg.ToString("X4"));                  // 22. BMS 平均温度
                    sb.Append(uKelvinMax.ToString("X4"));                  // 23. BMS 最高温度
                    sb.Append("0001");                                    // 24. BMS 最高温度所在模块
                    sb.Append(uKelvinMin.ToString("X4"));                  // 25. BMS 最低温度
                    sb.Append("0001");                                    // 26. BMS 最低温度所在模块

                    infoData = sb.ToString();
                    string statusTag61 = "";
                    if (model.HasProtection) statusTag61 += string.Format(" [保护跳闸: {0}]", model.GetActiveProtectionsSummary());
                    if (model.HasWarning) statusTag61 += string.Format(" [告警提示: {0}]", model.GetActiveWarningsSummary());

                    decodedInfo = string.Format("[PYLON-61H System Analog] 应答系统模拟量: 总压={0:F2}V, 电流={1:F2}A, SOC={2:F0}%, SOH={3:F0}%, 温度={4:F1}°C, 单芯={5}串({6}~{7}mV){8}",
                        model.Voltage, model.Current, model.SOC, model.SOH, model.Temperature, cellCount, cellVoltMin, cellVoltMax, statusTag61);
                }
                else if (cid2 == "62") // 2.3 获取电池组系统状态告警量信息 (Page 17-18, 4字节 / 8 HEX字符)
                {
                    byte warn1 = 0;
                    if (model.WarnGlobalOverVolt) warn1 |= (0x80 | 0x20);  // Bit 7: 模块总压高压, Bit 5: 单芯电压高压 (兼容3KU/6KU)
                    if (model.WarnGlobalUnderVolt) warn1 |= (0x40 | 0x10); // Bit 6: 模块总压低压, Bit 4: 单芯电压低压
                    if (model.WarnSingleOverVolt) warn1 |= (0x80 | 0x20);  // 修正为过压位: Bit 7 模块总压高压, Bit 5 单芯高压 (兼容3KU/6KU)
                    if (model.WarnSingleUnderVolt) warn1 |= (0x10 | 0x40); // 兼容3KU 0x1000 及 6KU 0x1000 校验
                    if (model.WarnHighTemp) warn1 |= (0x08 | 0x02);        // Bit 3: 单芯高温, Bit 1: MOSFET 高温 (3KU检测0x0900)
                    if (model.WarnLowTemp) warn1 |= 0x04;                  // Bit 2: 单芯低温 (3KU检测0x0400)
                    if (model.WarnVoltDiff) warn1 |= 0x01;                 // Bit 0: 单芯电压一致性告警

                    byte warn2 = 0;
                    if (model.WarnVoltDiff) warn2 |= 0x80;                 // Bit 7: 单芯温度一致性告警
                    if (model.WarnOverCurrent) warn2 |= (0x40 | 0x20);     // Bit 6: 充电过流, Bit 5: 放电过流 (3KU检测0x0060)
                    if (model.ProtSystemFault) warn2 |= 0x10;              // Bit 4: 内部通信错误

                    byte prot1 = 0;
                    if (model.ProtOverVolt) prot1 |= (0x80 | 0x20);        // 0xA0: 模块总压/单芯过压保护 (3KU检测0xA000)
                    if (model.ProtUnderVolt) prot1 |= (0x40 | 0x10);       // 0x50: 模块总压/单芯欠压保护 (3KU检测0x5000)
                    if (model.ProtHighTemp) prot1 |= (0x08 | 0x02);        // 0x0A: 单芯/MOSFET过温保护 (3KU检测0x0A00)
                    if (model.ProtUnderTemp) prot1 |= 0x04;                // 0x04: 单芯欠温保护 (3KU检测0x0400)

                    byte prot2 = 0;
                    if (model.ProtOverCurrent) prot2 |= (0x40 | 0x20);     // 0x60: 充放电过流保护 (3KU检测0x0060)
                    if (model.ProtSystemFault || model.ProtShortCircuit || model.ProtSoftStart) prot2 |= 0x08; // 0x08: 系统故障保护 (3KU检测0x0008)

                    infoData = string.Format("{0:X2}{1:X2}{2:X2}{3:X2}", warn1, warn2, prot1, prot2);
                    if (model.HasProtection)
                    {
                        decodedInfo = string.Format("[PYLON-62H 保护跳闸: {0}] Prot1=0x{1:X2}, Prot2=0x{2:X2}, Warn1=0x{3:X2}, Warn2=0x{4:X2}",
                            model.GetActiveProtectionsSummary(), prot1, prot2, warn1, warn2);
                    }
                    else if (model.HasWarning)
                    {
                        decodedInfo = string.Format("[PYLON-62H 告警提示: {0}] Warn1=0x{1:X2}, Warn2=0x{2:X2}",
                            model.GetActiveWarningsSummary(), warn1, warn2);
                    }
                    else
                    {
                        decodedInfo = "[PYLON-62H 状态正常] 系统正常 (无告警/无保护)";
                    }
                }
                else if (cid2 == "63" || cid2 == "92") // 2.4 获取电池组系统充放电管理交互信息 (Page 19-20, 5项, 9字节 / 18 HEX字符)
                {
                    // 1. 充电电压建议上限 (mV, Accuracy 3)
                    ushort uCvMv = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.CVVoltage * 1000)));
                    // 2. 放电电压建议下限 (mV, Accuracy 3, 支持 6KU/3KU 标准对齐)
                    ushort uCutoffMv = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.CutoffVoltage * 1000)));
                    // 3. 最大充电电流 (10mA / 0.01A: 当禁止充电或处于任何保护时强制输出 0A)
                    bool canCharge = model.CanCharge;
                    ushort uMaxChg = (ushort)(canCharge ? Math.Max(0, Math.Min(65535, Math.Round(model.MaxChargeCurrent * 100))) : 0);
                    // 4. 最大放电电流 (10mA: 当禁止放电或处于任何保护时强制输出 0A)
                    bool canDischarge = model.CanDischarge;
                    ushort uMaxDis = (ushort)(canDischarge ? Math.Max(0, Math.Min(65535, Math.Round(model.MaxDischargeCurrent * 100))) : 0);

                    // 5. 充放电状态字 (1 字节):
                    // Bit 7: Charge enable (1: yes; 0: request stop charge)
                    // Bit 6: Discharge enable (1: yes; 0: request stop discharge)
                    // Bit 5: 强充，立即充电/charge immediately (1: yes; 0: normal)
                    // Bit 4: 满充请求/full charge request (1: yes; 0: normal)
                    byte statusByte = 0;
                    if (canCharge)
                        statusByte |= 0x80; // 允许充电 (Bit 7 = 1)
                    if (canDischarge)
                        statusByte |= 0x40; // 允许放电 (Bit 6 = 1)
                    if (model.StatusForceCharge)
                        statusByte |= 0x20; // 强制充电 (Bit 5 = 1)
                    if (model.StatusFullCharge)
                        statusByte |= 0x10; // 满充请求 (Bit 4 = 1)

                    StringBuilder sb = new StringBuilder();
                    sb.Append(uCvMv.ToString("X4"));
                    sb.Append(uCutoffMv.ToString("X4"));
                    sb.Append(uMaxChg.ToString("X4"));
                    sb.Append(uMaxDis.ToString("X4"));
                    sb.Append(statusByte.ToString("X2"));

                    infoData = sb.ToString();
                    string statusTag63 = "";
                    if (model.HasProtection) statusTag63 += string.Format(" [保护跳闸: {0}]", model.GetActiveProtectionsSummary());
                    if (model.HasWarning) statusTag63 += string.Format(" [告警提示: {0}]", model.GetActiveWarningsSummary());

                    decodedInfo = string.Format("[PYLON-63H System Config] 响应充放电控制: CV={0:F2}V, 截止={1:F2}V, 充限={2:F1}A, 放限={3:F1}A, 状态=0x{4:X2}(充使能:{5}, 放使能:{6}){7}",
                        model.CVVoltage, model.CutoffVoltage, canCharge ? model.MaxChargeCurrent : 0, canDischarge ? model.MaxDischargeCurrent : 0, statusByte,
                        (statusByte & 0x80) != 0, (statusByte & 0x40) != 0, statusTag63);
                }

                else if (cid2 == "64" || cid2 == "95") // 2.5 控制电池组系统关机指令
                {
                    infoData = "00";
                    decodedInfo = "[PYLON-64H Shutdown] 响应电池组系统关机指令";
                }
                // ─────────────────────────────────────────────────────────────
                // 2. 单包/模块级通信指令 (42H, 44H, 47H, 4FH, 51H, 93H, 94H, 96H)
                // ─────────────────────────────────────────────────────────────
                else if (cid2 == "42") // 获取模拟量量化后数据 (定点数)
                {
                    int cellCount = model.GetEffectiveCellCount();
                    ushort uVoltMv = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.Voltage * 1000)));
                    short sCurr100Ma = (short)Math.Max(-32768, Math.Min(32767, Math.Round(model.Current * 10)));
                    // 容量单位: 10mAh (0.01Ah), 如 100.0Ah -> 10000 = 0x2710
                    ushort uRemCap10Mah = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.RemainingCapacity * 100)));
                    ushort uFullCap10Mah = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.FullCapacity * 100)));
                    ushort uKelvin = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.Temperature * 10 + 2731)));
                    byte uSoc = (byte)Math.Max(0, Math.Min(100, Math.Round(model.SOC)));

                    StringBuilder sb = new StringBuilder();
                    sb.Append("01"); // Command Value / Pack 1
                    sb.Append(cellCount.ToString("X2")); // 电池串数
                    for (int i = 0; i < cellCount; i++)
                    {
                        ushort maxMv, minMv;
                        sb.Append(model.GetCellMillivolts(i, cellCount, out maxMv, out minMv).ToString("X4")); // 各串电芯电压 (mV)
                    }
                    sb.Append("04"); // 4 个温度传感器
                    for (int i = 0; i < 4; i++) sb.Append(uKelvin.ToString("X4")); // 温度 (0.1K)
                    sb.Append(((ushort)sCurr100Ma).ToString("X4")); // 电流 (0.1A, 有符号)
                    sb.Append(uVoltMv.ToString("X4"));              // 总压 (mV)
                    sb.Append(uRemCap10Mah.ToString("X4"));         // 剩余容量 (10mAh)
                    sb.Append("02");                                // P - 预留/已定义
                    sb.Append(uFullCap10Mah.ToString("X4"));        // 满充总容量 (10mAh)
                    sb.Append(((ushort)Math.Max(0, Math.Min(65535, model.CycleCount))).ToString("X4")); // 循环次数
                    sb.Append(uSoc.ToString("X2"));                 // 电池 SOC (%)

                    infoData = sb.ToString();
                    decodedInfo = string.Format("[PYLON-42H Pack Info] 应答单包数据: 电压={0:F2}V, 电流={1:F1}A, SOC={2:F0}%, 容量={3:F1}/{4:F1}Ah, 温度={5:F1}°C ({6}串)",
                        model.Voltage, model.Current, model.SOC, model.RemainingCapacity, model.FullCapacity, model.Temperature, cellCount);
                }
                else if (cid2 == "44") // 获取单包告警信息
                {
                    int cellCount = model.GetEffectiveCellCount();
                    byte cellWarnByte = 0;
                    if (model.WarnSingleOverVolt) cellWarnByte = 0x02; // 上限告警
                    else if (model.WarnSingleUnderVolt) cellWarnByte = 0x01; // 下限告警

                    byte tempWarnByte = 0;
                    if (model.WarnHighTemp) tempWarnByte = 0x02;
                    else if (model.WarnLowTemp) tempWarnByte = 0x01;

                    byte currWarnByte = 0;
                    if (model.WarnOverCurrent) currWarnByte = (byte)(model.Current >= 0 ? 0x01 : 0x02);

                    byte voltWarnByte = 0;
                    if (model.WarnGlobalOverVolt) voltWarnByte = 0x02;
                    else if (model.WarnGlobalUnderVolt) voltWarnByte = 0x01;

                    byte prot1 = 0;
                    if (model.ProtOverVolt) prot1 |= 0x80;
                    if (model.ProtUnderVolt) prot1 |= 0x40;
                    if (model.ProtOverCurrent) prot1 |= 0x20;
                    if (model.ProtHighTemp) prot1 |= 0x08;
                    if (model.ProtUnderTemp) prot1 |= 0x04;

                    StringBuilder sb = new StringBuilder();
                    sb.Append("01"); // Pack 1
                    sb.Append(cellCount.ToString("X2"));
                    for (int i = 0; i < cellCount; i++) sb.Append(cellWarnByte.ToString("X2"));
                    sb.Append("04");
                    for (int i = 0; i < 4; i++) sb.Append(tempWarnByte.ToString("X2"));
                    sb.Append(currWarnByte.ToString("X2"));
                    sb.Append(voltWarnByte.ToString("X2"));
                    sb.Append(prot1.ToString("X2"));
                    sb.Append("00000000"); // 预留保护状态位

                    infoData = sb.ToString();
                    if (prot1 != 0)
                    {
                        decodedInfo = string.Format("[PYLON-44H 保护跳闸] 单包保护触发: Prot=0x{0:X2}", prot1);
                    }
                    else if (cellWarnByte != 0 || tempWarnByte != 0 || currWarnByte != 0 || voltWarnByte != 0)
                    {
                        decodedInfo = string.Format("[PYLON-44H 告警提示] 单包告警触发 (V:{0:X2}, T:{1:X2}, I:{2:X2}, Total:{3:X2})",
                            cellWarnByte, tempWarnByte, currWarnByte, voltWarnByte);
                    }
                    else
                    {
                        decodedInfo = "[PYLON-44H 状态正常] 单包正常 (无告警/无保护)";
                    }
                }
                else if (cid2 == "47") // 获取单包系统参数
                {
                    ushort cellOvMv = 3650;
                    ushort cellUvMv = 2800;
                    ushort cellCutoffMv = 2500;
                    ushort chgHtKelvin = (ushort)(550 + 2731);
                    ushort chgLtKelvin = (ushort)(0 + 2731);
                    ushort maxChg10Ma = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.MaxChargeCurrent * 100)));
                    ushort batOvMv = (ushort)Math.Max(0, Math.Min(65535, Math.Round((model.CVVoltage + 1.0) * 1000)));
                    ushort batUvMv = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.CutoffVoltage * 1000)));
                    ushort disHtKelvin = (ushort)(600 + 2731);
                    ushort disLtKelvin = (ushort)(-100 + 2731);
                    ushort maxDis10Ma = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.MaxDischargeCurrent * 100)));

                    StringBuilder sb = new StringBuilder();
                    sb.Append(cellOvMv.ToString("X4"));
                    sb.Append(cellUvMv.ToString("X4"));
                    sb.Append(cellCutoffMv.ToString("X4"));
                    sb.Append(chgHtKelvin.ToString("X4"));
                    sb.Append(chgLtKelvin.ToString("X4"));
                    sb.Append(maxChg10Ma.ToString("X4"));
                    sb.Append(batOvMv.ToString("X4"));
                    sb.Append(batUvMv.ToString("X4"));
                    sb.Append(disHtKelvin.ToString("X4"));
                    sb.Append(disLtKelvin.ToString("X4"));
                    sb.Append(maxDis10Ma.ToString("X4"));

                    infoData = sb.ToString();
                    decodedInfo = string.Format("[PYLON-47H Pack Config] 响应单包参数: CV={0:F2}V, 限流={1:F1}A", model.CVVoltage, model.MaxChargeCurrent);
                }
                else if (cid2 == "4F") // 获取通信协议版本
                {
                    infoData = "0200"; // V2.0
                    decodedInfo = "[PYLON-4FH Protocol] 响应协议版本号: V2.0";
                }
                else if (cid2 == "51") // 获取厂商信息
                {
                    string mfg = "Pylon               ";
                    StringBuilder sb = new StringBuilder();
                    foreach (char c in mfg) sb.Append(((byte)c).ToString("X2"));
                    infoData = sb.ToString();
                    decodedInfo = "[PYLON-51H Mfg] 响应厂商信息: Pylon";
                }
                else if (cid2 == "93") // 获取序列号
                {
                    string sn = "PYLON20260817001";
                    StringBuilder sb = new StringBuilder();
                    foreach (char c in sn) sb.Append(((byte)c).ToString("X2"));
                    infoData = sb.ToString();
                    decodedInfo = "[PYLON-93H SN] 响应电池序列号: PYLON20260817001";
                }
                else if (cid2 == "96") // 获取软件版本
                {
                    infoData = "0100";
                    decodedInfo = "[PYLON-96H Firmware] 响应固件版本号: V1.0";
                }
                else
                {
                    infoData = "00";
                    decodedInfo = string.Format("[PYLON-通用] 响应指令 CID2={0}", cid2);
                }
            }

            // 构造 LENGTH: 高4位 LCHKSUM, 低12位 LENID
            int len = infoData.Length;
            string lenid;
            if (len == 0)
            {
                lenid = "0000";
            }
            else
            {
                int d11_8 = (len >> 8) & 0x0F;
                int d7_4 = (len >> 4) & 0x0F;
                int d3_0 = len & 0x0F;
                int sumNibbles = (d11_8 + d7_4 + d3_0) % 16;
                int lchk = (~sumNibbles + 1) & 0x0F;
                lenid = lchk.ToString("X1") + len.ToString("X3");
            }

            string header = string.Format("~20{0}46{1}{2}{3}", adrStr, rtnCode, lenid, infoData);
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);

            ushort chk = PylonChecksum.Calculate(headerBytes, 1, headerBytes.Length - 1);
            string fullAscii = header + chk.ToString("X4") + "\r";

            txResponse = Encoding.ASCII.GetBytes(fullAscii);
            return true;
        }

        public int MasterPollPhaseCount
        {
            get { return 6; }
        }

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            // 主机轮询 (6阶段全量遥测与控制配置轮询):
            // Phase 0: 61H (系统运行模拟量: 总压, 电流, SOC, SOH, 温度, 循环次数)
            // Phase 1: 62H (系统告警与保护状态字)
            // Phase 2: 63H (系统充放电管理参数: CV, 截止, 充放限流, 使能字)
            // Phase 3: 42H (单包模拟量与电芯探头)
            // Phase 4: 44H (单包告警与保护状态)
            // Phase 5: 92H (单包充放电管理)
            string cid2;
            switch (pollPhase % 6)
            {
                case 0: cid2 = "61"; break;
                case 1: cid2 = "62"; break;
                case 2: cid2 = "63"; break;
                case 3: cid2 = "42"; break;
                case 4: cid2 = "44"; break;
                case 5: cid2 = "92"; break;
                default: cid2 = "61"; break;
            }

            // INFO 参数传入 targetId (例如 01 或 02)
            string cmd = string.Format("~20{0:X2}46{1}E002{0:X2}", targetId, cid2);
            byte[] cmdBytes = Encoding.ASCII.GetBytes(cmd);
            ushort chk = PylonChecksum.Calculate(cmdBytes, 1, cmdBytes.Length - 1);
            string full = cmd + chk.ToString("X4") + "\r";
            return Encoding.ASCII.GetBytes(full);
        }

        public bool TryDecodeMasterResponse(byte[] rxBuffer, int rxLength, BmsDataModel model, out string decodedInfo)
        {
            decodedInfo = "";
            if (rxLength < 15) return false;
            if (!PylonChecksum.Verify(rxBuffer)) return false;

            string asciiStr = Encoding.ASCII.GetString(rxBuffer, 0, rxLength);
            if (asciiStr.Length < 15) return false;

            // ~ VER(2) ADR(2) CID1(2) RTN(2) LENGTH(4) INFO(...) CHKSUM(4) \r
            string cid1 = asciiStr.Substring(5, 2);
            string rtn = asciiStr.Substring(7, 2);
            if (rtn != "00")
            {
                decodedInfo = string.Format("[PYLON] 从机返回提示码 RTN={0}", rtn);
                return false;
            }

            int infoLen = 0;
            int.TryParse(asciiStr.Substring(10, 3), System.Globalization.NumberStyles.HexNumber, null, out infoLen);
            if (asciiStr.Length < 13 + infoLen + 5) return false;

            string info = asciiStr.Substring(13, infoLen);

            lock (model.SyncRoot)
            {
                // 1. 优先解析 42H (模拟量) / 44H (告警与保护) 单包报文 (支持 0002 或 02 等前缀)
                int cellOffset = info.StartsWith("00") ? 4 : 2;
                int cellCount = 0;
                if (info.Length >= cellOffset + 2 && int.TryParse(info.Substring(cellOffset, 2), System.Globalization.NumberStyles.HexNumber, null, out cellCount) && cellCount > 0 && cellCount <= 32)
                {
                    // 检查是否为 42H 模拟量 (每个电芯电压 4 字符, 温度 4 字符, 电流/电压/容量/循环 20 字符以上)
                    int tempOffset42 = cellOffset + 2 + cellCount * 4;
                    if (info.Length >= tempOffset42 + 2)
                    {
                        int tempCount42 = 0;
                        int.TryParse(info.Substring(tempOffset42, 2), System.Globalization.NumberStyles.HexNumber, null, out tempCount42);
                        int currOffset42 = tempOffset42 + 2 + tempCount42 * 4;

                        if (tempCount42 <= 16 && info.Length >= currOffset42 + 20)
                        {
                            // 提取温度均值
                            if (tempCount42 > 0)
                            {
                                int tempSum = 0;
                                int validTempCount = 0;
                                for (int t = 0; t < tempCount42; t++)
                                {
                                    ushort tK;
                                    if (ushort.TryParse(info.Substring(tempOffset42 + 2 + t * 4, 4), System.Globalization.NumberStyles.HexNumber, null, out tK) && tK > 2000 && tK < 4000)
                                    {
                                        tempSum += (tK - 2731);
                                        validTempCount++;
                                    }
                                }
                                if (validTempCount > 0)
                                    model.Temperature = Math.Min(150.0, Math.Max(-50.0, (tempSum / (double)validTempCount) / 10.0));
                            }

                            short sCurr;
                            ushort uVolt;
                            if (short.TryParse(info.Substring(currOffset42, 4), System.Globalization.NumberStyles.HexNumber, null, out sCurr))
                                model.Current = sCurr / 10.0; // 42H 电流精度为 0.1A

                            if (ushort.TryParse(info.Substring(currOffset42 + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out uVolt))
                                model.Voltage = uVolt / 1000.0; // mV -> V

                            if (info.Length >= currOffset42 + 12)
                            {
                                ushort uRemCap;
                                if (ushort.TryParse(info.Substring(currOffset42 + 8, 4), System.Globalization.NumberStyles.HexNumber, null, out uRemCap) && uRemCap != 0xFFFF)
                                    model.RemainingCapacity = uRemCap / 100.0; // 10mAh -> Ah
                            }

                            if (info.Length >= currOffset42 + 18)
                            {
                                ushort uFullCap;
                                if (ushort.TryParse(info.Substring(currOffset42 + 14, 4), System.Globalization.NumberStyles.HexNumber, null, out uFullCap) && uFullCap != 0xFFFF)
                                    model.FullCapacity = uFullCap / 100.0; // 10mAh -> Ah
                            }

                            if (info.Length >= currOffset42 + 22)
                            {
                                ushort uCycles;
                                if (ushort.TryParse(info.Substring(currOffset42 + 18, 4), System.Globalization.NumberStyles.HexNumber, null, out uCycles))
                                    model.CycleCount = uCycles;
                            }

                            // 计算并校准 SOC
                            if (model.FullCapacity > 0 && model.RemainingCapacity > 0)
                            {
                                model.SOC = Math.Round(Math.Min(100.0, Math.Max(0.0, (model.RemainingCapacity / model.FullCapacity) * 100.0)), 1);
                            }
                            else
                            {
                                model.SOC = Math.Min(100.0, Math.Max(0.0, model.SOC));
                            }

                            decodedInfo = string.Format("[PYLON] 解析 42H 单包遥测成功: 电压={0:F2}V, 电流={1:F1}A, SOC={2:F0}%, 容量={3:F1}/{4:F1}Ah, 循环={5}次, 温度={6:F1}°C ({7}串)",
                                model.Voltage, model.Current, model.SOC, model.RemainingCapacity, model.FullCapacity, model.CycleCount, model.Temperature, cellCount);
                            return true;
                        }
                    }

                    // 检查是否为 44H 告警与保护 (每个电芯告警 2 字符, 温度告警 2 字符, 状态字 4 字符以上)
                    int tempOffset44 = cellOffset + 2 + cellCount * 2;
                    if (info.Length >= tempOffset44 + 2)
                    {
                        int tempCount44 = 0;
                        int.TryParse(info.Substring(tempOffset44, 2), System.Globalization.NumberStyles.HexNumber, null, out tempCount44);
                        int statusOffset44 = tempOffset44 + 2 + tempCount44 * 2;

                        if (tempCount44 <= 16 && info.Length >= statusOffset44 + 4)
                        {
                            bool cellOvWarn = false, cellUvWarn = false;
                            for (int c = 0; c < cellCount; c++)
                            {
                                byte cw = 0;
                                if (byte.TryParse(info.Substring(cellOffset + 2 + c * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out cw))
                                {
                                    if (cw == 0x02) cellOvWarn = true;
                                    else if (cw == 0x01) cellUvWarn = true;
                                }
                            }

                            bool tempHtWarn = false, tempLtWarn = false;
                            for (int t = 0; t < tempCount44; t++)
                            {
                                byte tw = 0;
                                if (byte.TryParse(info.Substring(tempOffset44 + 2 + t * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out tw))
                                {
                                    if (tw == 0x02) tempHtWarn = true;
                                    else if (tw == 0x01) tempLtWarn = true;
                                }
                            }

                            byte currWarn = 0, voltWarn = 0, prot1 = 0;
                            byte.TryParse(info.Substring(statusOffset44, 2), System.Globalization.NumberStyles.HexNumber, null, out currWarn);
                            byte.TryParse(info.Substring(statusOffset44 + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out voltWarn);
                            if (info.Length >= statusOffset44 + 6)
                                byte.TryParse(info.Substring(statusOffset44 + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out prot1);

                            model.WarnSingleOverVolt = cellOvWarn;
                            model.WarnSingleUnderVolt = cellUvWarn;
                            model.WarnHighTemp = tempHtWarn;
                            model.WarnLowTemp = tempLtWarn;
                            model.WarnOverCurrent = (currWarn != 0);
                            model.WarnGlobalOverVolt = (voltWarn & 0x02) != 0;
                            model.WarnGlobalUnderVolt = (voltWarn & 0x01) != 0;

                            model.ProtOverVolt = (prot1 & 0x80) != 0;
                            model.ProtUnderVolt = (prot1 & 0x40) != 0;
                            model.ProtOverCurrent = (prot1 & 0x20) != 0;
                            model.ProtHighTemp = (prot1 & 0x08) != 0;
                            model.ProtUnderTemp = (prot1 & 0x04) != 0;

                            decodedInfo = string.Format("[PYLON] 解析 44H 单包状态字: 告警(I:{0:X2},V:{1:X2}) 保护(0x{2:X2})",
                                currWarn, voltWarn, prot1);
                            return true;
                        }
                    }
                }

                // 2. 60H 系统设备信息响应 (ASCII 字符串)
                if (infoLen == 98 && (info.StartsWith("4C31") || info.Contains("4A4244")))
                {
                    decodedInfo = "[PYLON] 60H 系统设备信息: JBD-UP16S023-300A";
                    return true;
                }

                // 3. 61H 系统模拟量响应 (长度 98: 26项系统模拟量)
                if (infoLen == 98)
                {
                    ushort uVoltMv;
                    short sCurr10Ma;
                    byte uSoc;
                    ushort uCycles;
                    byte uSoh;
                    ushort uTempK;

                    if (ushort.TryParse(info.Substring(0, 4), System.Globalization.NumberStyles.HexNumber, null, out uVoltMv))
                        model.Voltage = uVoltMv / 1000.0;

                    if (short.TryParse(info.Substring(4, 4), System.Globalization.NumberStyles.HexNumber, null, out sCurr10Ma))
                        model.Current = sCurr10Ma / 100.0;

                    if (byte.TryParse(info.Substring(8, 2), System.Globalization.NumberStyles.HexNumber, null, out uSoc))
                        model.SOC = Math.Min(100.0, Math.Max(0.0, (double)uSoc));

                    if (ushort.TryParse(info.Substring(10, 4), System.Globalization.NumberStyles.HexNumber, null, out uCycles))
                        model.CycleCount = uCycles;

                    if (byte.TryParse(info.Substring(18, 2), System.Globalization.NumberStyles.HexNumber, null, out uSoh))
                        if (uSoh > 0 && uSoh <= 100) model.SOH = uSoh;

                    if (info.Length >= 42 && ushort.TryParse(info.Substring(38, 4), System.Globalization.NumberStyles.HexNumber, null, out uTempK))
                        model.Temperature = Math.Min(150.0, Math.Max(-50.0, (uTempK - 2731) / 10.0));

                    decodedInfo = string.Format("[PYLON] 解析 61H 系统遥测成功: 电压={0:F2}V, 电流={1:F2}A, SOC={2:F0}%, SOH={3:F0}%, 循环={4}次, 温度={5:F1}°C",
                        model.Voltage, model.Current, model.SOC, model.SOH, model.CycleCount, model.Temperature);
                    return true;
                }

                // 4. 62H 系统告警与保护状态字
                if (infoLen == 8)
                {
                    byte warn1, warn2, prot1, prot2;
                    if (byte.TryParse(info.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out warn1) &&
                        byte.TryParse(info.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out warn2) &&
                        byte.TryParse(info.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out prot1) &&
                        byte.TryParse(info.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out prot2))
                    {
                        model.WarnGlobalOverVolt = (warn1 & 0x80) != 0;
                        model.WarnGlobalUnderVolt = (warn1 & 0x40) != 0;
                        model.WarnSingleOverVolt = (warn1 & 0x20) != 0;
                        model.WarnSingleUnderVolt = (warn1 & 0x10) != 0;
                        model.WarnHighTemp = (warn1 & 0x0A) != 0;
                        model.WarnLowTemp = (warn1 & 0x04) != 0;
                        model.WarnVoltDiff = (warn1 & 0x01) != 0 || (warn2 & 0x80) != 0;
                        model.WarnOverCurrent = (warn2 & 0x60) != 0;

                        model.ProtOverVolt = (prot1 & 0xA0) != 0;
                        model.ProtUnderVolt = (prot1 & 0x50) != 0;
                        model.ProtHighTemp = (prot1 & 0x0A) != 0;
                        model.ProtUnderTemp = (prot1 & 0x04) != 0;
                        model.ProtOverCurrent = (prot2 & 0x60) != 0;
                        model.ProtSystemFault = (prot2 & 0x08) != 0;

                        decodedInfo = string.Format("[PYLON] 解析 62H 系统状态字: 告警(0x{0:X2},0x{1:X2}) 保护(0x{2:X2},0x{3:X2})",
                            warn1, warn2, prot1, prot2);
                        return true;
                    }
                }

                // 5. 63H / 92H 充放电控制管理参数 (18 或 20 字符)
                if (infoLen == 18 || infoLen == 20)
                {
                    int offset = (infoLen == 20 || info.Length >= 20) ? 2 : 0;
                    ushort uCvMv, uCutoffMv;
                    short sChg10Ma, sDis10Ma;
                    byte statusByte;

                    if (ushort.TryParse(info.Substring(offset + 0, 4), System.Globalization.NumberStyles.HexNumber, null, out uCvMv) && uCvMv > 0)
                        model.CVVoltage = uCvMv / 1000.0;

                    if (ushort.TryParse(info.Substring(offset + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out uCutoffMv) && uCutoffMv > 0)
                        model.CutoffVoltage = uCutoffMv / 1000.0;

                    if (short.TryParse(info.Substring(offset + 8, 4), System.Globalization.NumberStyles.HexNumber, null, out sChg10Ma))
                        model.MaxChargeCurrent = Math.Abs((double)sChg10Ma) / 100.0;

                    if (short.TryParse(info.Substring(offset + 12, 4), System.Globalization.NumberStyles.HexNumber, null, out sDis10Ma))
                        model.MaxDischargeCurrent = Math.Abs((double)sDis10Ma) / 100.0;

                    if (byte.TryParse(info.Substring(offset + 16, 2), System.Globalization.NumberStyles.HexNumber, null, out statusByte))
                    {
                        model.StatusChargeEnable = (statusByte & 0x80) != 0;
                        model.StatusDischargeEnable = (statusByte & 0x40) != 0;
                        model.StatusForceCharge = (statusByte & 0x20) != 0;
                        model.StatusFullCharge = (statusByte & 0x10) != 0;
                    }

                    decodedInfo = string.Format("[PYLON] 解析 63H/92H 充放控制成功: CV={0:F2}V, 截止={1:F2}V, 充限={2:F1}A, 放限={3:F1}A, 使能=0x{4:X2}",
                        model.CVVoltage, model.CutoffVoltage, model.MaxChargeCurrent, model.MaxDischargeCurrent, statusByte);
                    return true;
                }
            }

            decodedInfo = "[PYLON] 校验成功并更新报文";
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 5. LIB_MODBUS (RS485 V1.30) 协议处理器 (0x04 读输入寄存器 / 0x11 读产品信息)
    // ─────────────────────────────────────────────────────────────
    public class LibModbusHandler : IBmsProtocolHandler
    {
        public string ProtocolName
        {
            get { return "SHENGYANG (RS485 V1.30)"; }
        }

        public bool TryProcessQuery(byte[] rxBuffer, int rxLength, BmsDataModel model, bool replyAllIds, int specificId, out byte[] txResponse, out string decodedInfo)
        {
            txResponse = null;
            decodedInfo = "";

            if (rxLength < 4 || !ModbusCrc.Check(rxBuffer, rxLength)) return false;

            byte id = rxBuffer[0];
            byte cmd = rxBuffer[1];

            // 圣阳协议规范 (PDF p.5): 拨码从机地址合法范围为 0..15
            if (id > 15)
            {
                decodedInfo = string.Format("[忽略] 从机 ID:{0} (0x{0:X2}) 超出圣阳规范拨码范围 0..15", id);
                return false;
            }

            // 情况 A: 读产品信息 (功能码 0x11, 4 字节查询帧 [ID, 0x11, CRCH, CRCL])
            if (cmd == 0x11 && rxLength == 4)
            {
                if (!replyAllIds && id != specificId)
                {
                    decodedInfo = string.Format("收到对 ID:{0:X2} 的读产品信息查询 (已按单包规则忽略)", id);
                    return false;
                }

                int cellCount;
                lock (model.SyncRoot)
                {
                    cellCount = model.GetEffectiveCellCount();
                }
                string modelCode = string.Format("P{0:D2}S50A-6232", cellCount);

                // Payload: Model ASCII (12B) + '*' (1B) + SW Ver (2B) + '*' (1B) + HW Ver (2B) + '*' (1B) + SN ASCII (20B) + '*' (1B) = 40 字节 (0x28)
                byte[] payload = new byte[40];
                byte[] modelBytes = Encoding.ASCII.GetBytes(modelCode);
                Array.Copy(modelBytes, 0, payload, 0, Math.Min(12, modelBytes.Length));
                payload[12] = 0x2A; // '*'
                payload[13] = 0x01; // SW Major 1
                payload[14] = 0x00; // SW Minor 0 (V1.00)
                payload[15] = 0x2A; // '*'
                payload[16] = 0x01; // HW Major 1
                payload[17] = 0x20; // HW Minor 20 (V1.20)
                payload[18] = 0x2A; // '*'
                byte[] snBytes = Encoding.ASCII.GetBytes("20161111011800400026");
                Array.Copy(snBytes, 0, payload, 19, Math.Min(20, snBytes.Length));
                payload[39] = 0x2A; // '*'

                byte[] resp = new byte[3 + 40 + 2]; // 45 字节
                resp[0] = id;
                resp[1] = 0x11;
                resp[2] = 40;
                Array.Copy(payload, 0, resp, 3, 40);

                ushort crc = ModbusCrc.Calculate(resp, 43);
                resp[43] = (byte)(crc >> 8);
                resp[44] = (byte)(crc & 0xFF);

                txResponse = resp;
                decodedInfo = string.Format("[SHENGYANG] 应答 ID:{0:X2} 读产品信息 (0x11) -> {1}, SW:V1.00, HW:V1.20, SN:20161111011800400026", id, modelCode);
                return true;
            }

            // 情况 B: 读输入寄存器 (功能码 0x04, 8 字节查询帧 [ID, 0x04, AddrH, AddrL, CntH, CntL, CRCH, CRCL])
            if (cmd == 0x04 && rxLength == 8)
            {
                if (!replyAllIds && id != specificId)
                {
                    decodedInfo = string.Format("收到对 ID:{0:X2} 的查询 (已按单包规则忽略)", id);
                    return false;
                }

                ushort regStart = (ushort)((rxBuffer[2] << 8) | rxBuffer[3]);
                ushort regCount = (ushort)((rxBuffer[4] << 8) | rxBuffer[5]);

                if (regCount == 0 || regStart < 0x1000 || (regStart + regCount - 1) > 0x1016)
                {
                    txResponse = BuildExceptionFrame(id, cmd, 0x02);
                    decodedInfo = string.Format("[SHENGYANG] 异常应答 ID:{0:X2} 读 0x{1:X4} (数量:{2}) -> 错误码:0x02 (非法寄存器地址)", id, regStart, regCount);
                    return true;
                }

                int byteCount = regCount * 2;
                byte[] resp = new byte[3 + byteCount + 2];
                resp[0] = id;
                resp[1] = 0x04;
                resp[2] = (byte)byteCount;

                lock (model.SyncRoot)
                {
                    for (int i = 0; i < regCount; i++)
                    {
                        ushort regAddr = (ushort)(regStart + i);
                        ushort val = GetLibModbusRegisterValue(regAddr, model);
                        resp[3 + i * 2] = (byte)(val >> 8);
                        resp[3 + i * 2 + 1] = (byte)(val & 0xFF);
                    }

                    string statusTag = "";
                    if (model.HasProtection) statusTag += string.Format(" [保护跳闸: {0}]", model.GetActiveProtectionsSummary());
                    if (model.HasWarning) statusTag += string.Format(" [告警提示: {0}]", model.GetActiveWarningsSummary());

                    decodedInfo = string.Format("[SHENGYANG] 应答 ID:{0:X2} 读 0x{1:X4} ({2}寄存器) -> 电压:{3:F2}V, SOC:{4:F0}%, 电流:{5:F2}A, 温度:{6:F1}°C{7}",
                        id, regStart, regCount, model.Voltage, model.SOC, model.Current, model.Temperature, statusTag);
                }

                ushort crc = ModbusCrc.Calculate(resp, 3 + byteCount);
                resp[3 + byteCount] = (byte)(crc >> 8);
                resp[3 + byteCount + 1] = (byte)(crc & 0xFF);

                txResponse = resp;
                return true;
            }

            // 情况 C: 其它功能码 (当 LibModbusHandler 激活且收到合法 CRC 帧时，返回 0x01 非法功能码异常)
            if (!replyAllIds && id != specificId)
            {
                decodedInfo = string.Format("收到对 ID:{0:X2} 的查询 (已按单包规则忽略)", id);
                return false;
            }

            txResponse = BuildExceptionFrame(id, cmd, 0x01);
            decodedInfo = string.Format("[SHENGYANG] 异常应答 ID:{0:X2} 功能码 0x{1:X2} -> 错误码:0x01 (非法功能码)", id, cmd);
            return true;
        }

        private static byte[] BuildExceptionFrame(byte id, byte cmd, byte errCode)
        {
            byte[] resp = new byte[5];
            resp[0] = id;
            resp[1] = (byte)(cmd + 0x80);
            resp[2] = errCode;
            ushort crc = ModbusCrc.Calculate(resp, 3);
            resp[3] = (byte)(crc >> 8);
            resp[4] = (byte)(crc & 0xFF);
            return resp;
        }

        private ushort GetLibModbusRegisterValue(ushort regAddress, BmsDataModel model)
        {
            switch (regAddress)
            {
                case 0x1000: // 电压 (10mV)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.Voltage * 100)));

                case 0x1001: // 电流 (10mA, 有符号强钳位防溢出)
                    return (ushort)((short)Math.Max(-32768, Math.Min(32767, (int)Math.Round(model.Current * 100))));

                case 0x1002: // 剩余容量 (10mAh)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.RemainingCapacity * 100)));

                case 0x1003: // 电芯平均温度 (0.1°C, 有符号)
                    return (ushort)((short)Math.Round(model.Temperature * 10));

                case 0x1004: // 环境温度 (0.1°C, Environment temperature, 有符号)
                    return (ushort)((short)Math.Round(model.Temperature * 10));

                case 0x1005: // 告警标志 (Byte0=LSB, Byte1=MSB)
                    {
                        ushort warn = 0;
                        if (model.WarnSingleOverVolt) warn |= 0x0001;
                        if (model.WarnSingleUnderVolt) warn |= 0x0002;
                        if (model.WarnGlobalOverVolt) warn |= 0x0004;
                        if (model.WarnGlobalUnderVolt) warn |= 0x0008;
                        if (model.WarnOverCurrent)
                        {
                            if (model.Current >= 0) warn |= 0x0010;
                            if (model.Current <= 0) warn |= 0x0020;
                        }
                        if (model.WarnHighTemp) warn |= (0x0040 | 0x0100 | 0x0400);
                        if (model.WarnLowTemp) warn |= (0x0080 | 0x0200);
                        if (model.WarnLowCapacity) warn |= 0x0800;
                        return warn;
                    }

                case 0x1006: // 保护标志 (Byte0=LSB, Byte1=MSB)
                    {
                        ushort prot = 0;
                        if (model.ProtOverVolt) prot |= (0x0001 | 0x0004);
                        if (model.ProtUnderVolt) prot |= (0x0002 | 0x0008);
                        if (model.ProtShortCircuit) prot |= 0x0010;
                        if (model.ProtOverCurrent) prot |= 0x0020;
                        if (model.ProtHighTemp) prot |= (0x0040 | 0x0100);
                        if (model.ProtUnderTemp) prot |= (0x0080 | 0x0200);
                        return prot;
                    }

                case 0x1007: // 故障/状态标志 (Byte0=Fault LSB, Byte1=Status MSB)
                    {
                        ushort fs = 0;
                        if (model.ProtSystemFault) fs |= 0x0001;
                        if (model.ProtSystemFault && (model.ProtHighTemp || model.ProtUnderTemp)) fs |= 0x0002;
                        if (model.Current > 0.05) fs |= 0x0100;
                        else if (model.Current < -0.05) fs |= 0x0200;
                        if (model.CanCharge) fs |= 0x0400;
                        if (model.CanDischarge) fs |= 0x0800;
                        if (model.CanCharge && model.MaxChargeCurrent > 0) fs |= 0x1000;
                        return fs;
                    }

                case 0x1008: // SOC (0.1%)
                    return (ushort)Math.Max(0, Math.Min(1000, (int)Math.Round(model.SOC * 10)));

                case 0x1009: // SOH (0.1%)
                    return (ushort)Math.Max(0, Math.Min(1000, (int)Math.Round(model.SOH * 10)));

                case 0x100A: // 满充容量 (10mAh)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.FullCapacity * 100)));

                case 0x100B: // 循环次数 (Cycle Count)
                    return (ushort)Math.Max(0, Math.Min(65535, model.CycleCount));

                case 0x100C: // 最大充电电流 (10mA)
                    return model.CanCharge ? (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.MaxChargeCurrent * 100))) : (ushort)0;

                case 0x100D: // 最高单体电压 (mV)
                    {
                        int nCells = model.GetEffectiveCellCount();
                        ushort maxMv, minMv;
                        model.GetCellMillivolts(0, nCells, out maxMv, out minMv);
                        return maxMv;
                    }

                case 0x100E: // 最低单体电压 (mV)
                    {
                        int nCells = model.GetEffectiveCellCount();
                        ushort maxMv, minMv;
                        model.GetCellMillivolts(1, nCells, out maxMv, out minMv);
                        return minMv;
                    }

                case 0x100F: // 保留
                    return 0x0000;

                case 0x1010: // 最高单体温度 (0.1°C, 常态无偏置，高温告警时 +2.0°C)
                    return (ushort)((short)Math.Round((model.Temperature + (model.WarnHighTemp ? 2.0 : 0.0)) * 10));

                case 0x1011: // 最低单体温度 (0.1°C, 常态无偏置，低温告警时 -2.0°C)
                    return (ushort)((short)Math.Round((model.Temperature - (model.WarnLowTemp ? 2.0 : 0.0)) * 10));

                case 0x1012: // FET 温度 (0.1°C, FET temperature)
                    return (ushort)((short)Math.Round(model.Temperature * 10));

                case 0x1013: // 预留
                    return 0x0000;

                case 0x1014: // 建议恒压充电电压 CV (10mV)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.CVVoltage * 100)));

                case 0x1015: // 设计容量 (10mAh)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.FullCapacity * 100)));

                case 0x1016: // 预留
                    return 0x0000;

                default:
                    return 0x0000;
            }
        }

        public int MasterPollPhaseCount
        {
            get { return 1; }
        }

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            byte[] frame = new byte[8];
            frame[0] = targetId;
            frame[1] = 0x04;
            frame[2] = 0x10;
            frame[3] = 0x00;
            frame[4] = 0x00;
            frame[5] = 0x17; // 23 个寄存器 (0x1000 ~ 0x1016)
            ushort crc = ModbusCrc.Calculate(frame, 6);
            frame[6] = (byte)(crc >> 8);
            frame[7] = (byte)(crc & 0xFF);
            return frame;
        }

        public bool TryDecodeMasterResponse(byte[] rxBuffer, int rxLength, BmsDataModel model, out string decodedInfo)
        {
            decodedInfo = "";
            if (rxLength < 5) return false;
            if (!ModbusCrc.Check(rxBuffer, rxLength)) return false;

            byte id = rxBuffer[0];
            byte fc = rxBuffer[1];

            // 1. 异常应答帧 (0x84 / 0x91)
            if (fc == 0x84 || fc == 0x91)
            {
                byte errCode = rxBuffer[2];
                decodedInfo = string.Format("[SHENGYANG] 从机异常响应 ID:{0:X2}, 功能码:0x{1:X2}, 错误码:0x{2:X2} ({3})",
                    id, fc, errCode, errCode == 0x01 ? "非法功能码" : (errCode == 0x02 ? "非法寄存器地址" : "从机异常"));
                return true;
            }

            // 2. 0x11 产品信息应答
            if (fc == 0x11)
            {
                byte byteCount = rxBuffer[2];
                if (rxLength < 3 + byteCount + 2 || byteCount < 40) return false;

                string modelStr = Encoding.ASCII.GetString(rxBuffer, 3, 12).TrimEnd('\0', ' ');
                byte swMaj = rxBuffer[16];
                byte swMin = rxBuffer[17];
                byte hwMaj = rxBuffer[19];
                byte hwMin = rxBuffer[20];
                string snStr = Encoding.ASCII.GetString(rxBuffer, 22, 20).TrimEnd('\0', ' ');

                decodedInfo = string.Format("[SHENGYANG] 接收从机产品信息 ID:{0:X2}: 型号={1}, 固件=V{2:X}.{3:X2}, 硬件=V{4:X}.{5:X2}, 序列号={6}",
                    id, modelStr, swMaj, swMin, hwMaj, hwMin, snStr);
                return true;
            }

            // 3. 0x04 输入寄存器读取应答
            if (fc == 0x04)
            {
                byte byteCount = rxBuffer[2];
                if (rxLength != 3 + byteCount + 2) return false;

                lock (model.SyncRoot)
                {
                    if (byteCount >= 2)
                    {
                        // 0x1000: 电压 (10mV)
                        model.Voltage = ((rxBuffer[3] << 8) | rxBuffer[4]) / 100.0;
                    }
                    if (byteCount >= 4)
                    {
                        // 0x1001: 电流 (10mA, 有符号)
                        model.Current = (short)((rxBuffer[5] << 8) | rxBuffer[6]) / 100.0;
                    }
                    if (byteCount >= 6)
                    {
                        // 0x1002: 剩余容量 (10mAh)
                        model.RemainingCapacity = ((rxBuffer[7] << 8) | rxBuffer[8]) / 100.0;
                    }
                    if (byteCount >= 8)
                    {
                        // 0x1003: 温度 (0.1°C, 有符号)
                        model.Temperature = (short)((rxBuffer[9] << 8) | rxBuffer[10]) / 10.0;
                    }
                    if (byteCount >= 12)
                    {
                        // 0x1005: 告警标志
                        ushort warn = (ushort)((rxBuffer[13] << 8) | rxBuffer[14]);
                        model.WarnSingleOverVolt = (warn & 0x0001) != 0;
                        model.WarnSingleUnderVolt = (warn & 0x0002) != 0;
                        model.WarnGlobalOverVolt = (warn & 0x0004) != 0;
                        model.WarnGlobalUnderVolt = (warn & 0x0008) != 0;
                        model.WarnOverCurrent = (warn & (0x0010 | 0x0020)) != 0;
                        model.WarnHighTemp = (warn & (0x0040 | 0x0100 | 0x0400)) != 0;
                        model.WarnLowTemp = (warn & (0x0080 | 0x0200)) != 0;
                        model.WarnLowCapacity = (warn & 0x0800) != 0;
                    }
                    if (byteCount >= 14)
                    {
                        // 0x1006: 保护标志
                        ushort prot = (ushort)((rxBuffer[15] << 8) | rxBuffer[16]);
                        model.ProtOverVolt = (prot & (0x0001 | 0x0004)) != 0;
                        model.ProtUnderVolt = (prot & (0x0002 | 0x0008)) != 0;
                        model.ProtShortCircuit = (prot & 0x0010) != 0;
                        model.ProtOverCurrent = (prot & 0x0020) != 0;
                        model.ProtHighTemp = (prot & (0x0040 | 0x0100)) != 0;
                        model.ProtUnderTemp = (prot & (0x0080 | 0x0200)) != 0;
                    }
                    if (byteCount >= 16)
                    {
                        // 0x1007: 故障与状态标志
                        ushort fs = (ushort)((rxBuffer[17] << 8) | rxBuffer[18]);
                        model.ProtSystemFault = (fs & 0x0001) != 0;
                        model.StatusChargeEnable = (fs & 0x0400) != 0;
                        model.StatusDischargeEnable = (fs & 0x0800) != 0;
                    }
                    if (byteCount >= 18)
                    {
                        // 0x1008: SOC (0.1%)
                        model.SOC = ((rxBuffer[19] << 8) | rxBuffer[20]) / 10.0;
                    }
                    if (byteCount >= 20)
                    {
                        // 0x1009: SOH (0.1%)
                        model.SOH = ((rxBuffer[21] << 8) | rxBuffer[22]) / 10.0;
                    }
                    if (byteCount >= 22)
                    {
                        // 0x100A: 满充容量 (10mAh)
                        model.FullCapacity = ((rxBuffer[23] << 8) | rxBuffer[24]) / 100.0;
                    }
                    if (byteCount >= 24)
                    {
                        // 0x100B: 循环次数
                        model.CycleCount = (ushort)((rxBuffer[25] << 8) | rxBuffer[26]);
                    }
                    if (byteCount >= 26)
                    {
                        // 0x100C: 最大充电电流 (10mA)
                        model.MaxChargeCurrent = ((rxBuffer[27] << 8) | rxBuffer[28]) / 100.0;
                    }
                    if (byteCount >= 42)
                    {
                        // 0x1014: 建议恒压充电电压 (10mV)
                        model.CVVoltage = ((rxBuffer[43] << 8) | rxBuffer[44]) / 100.0;
                    }
                }

                decodedInfo = string.Format("[SHENGYANG] 成功解析 0x04 应答: 电压={0:F2}V, 电流={1:F2}A, SOC={2:F1}%, 温度={3:F1}°C, 满充容量={4:F2}Ah",
                    model.Voltage, model.Current, model.SOC, model.Temperature, model.FullCapacity);
                return true;
            }

            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 6. JBD (RS485/UART V12) 协议处理器 ("JBD Software board universal protocol V12")
    // ─────────────────────────────────────────────────────────────
    public class JbdBinaryHandler : IBmsProtocolHandler
    {
        private bool _inFactoryMode = false;
        private bool _heatingEnabled = false;
        private bool _softLockChgMos = false;
        private bool _softLockDisMos = false;

        public bool InFactoryMode
        {
            get { return _inFactoryMode; }
        }

        public string ProtocolName
        {
            get { return "JBD (RS485/UART V12)"; }
        }

        public int MasterPollPhaseCount
        {
            get { return 2; }
        }

        public bool TryProcessQuery(byte[] rxBuffer, int rxLength, BmsDataModel model, bool replyAllIds, int specificId, out byte[] txResponse, out string decodedInfo)
        {
            txResponse = null;
            decodedInfo = "";

            int coreLen, cbLen;
            if (!JbdChecksum.VerifyRequest(rxBuffer, 0, rxLength, out coreLen, out cbLen))
                return false;

            byte[] callbackId = null;
            if (cbLen > 0)
            {
                callbackId = new byte[cbLen];
                Array.Copy(rxBuffer, coreLen, callbackId, 0, cbLen);
            }

            byte rw = rxBuffer[1];
            byte cmd = rxBuffer[2];
            byte reqDataLen = rxBuffer[3];

            lock (model.SyncRoot)
            {
                // ── 1. 读寄存器指令 (RW == 0xA5) ──
                if (rw == 0xA5)
                {
                    if (cmd == 0x03)
                    {
                        // 0x03: 读基本信息与状态 (36 字节)
                        byte[] data = new byte[36];

                        bool useUnit100mA = (Math.Abs(model.Current) > 327.67) || (model.RemainingCapacity > 655.35) || (model.FullCapacity > 655.35);
                        double unitScale = useUnit100mA ? 10.0 : 100.0;

                        // Bytes 0..1: 总电压 (10mV)
                        ushort uVolt = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.Voltage * 100.0)));
                        data[0] = (byte)(uVolt >> 8);
                        data[1] = (byte)(uVolt & 0xFF);

                        // Bytes 2..3: 电流 (Bit7=0: 10mA; Bit7=1: 100mA, 有符号补码)
                        short sCurr = (short)Math.Max(-32768, Math.Min(32767, Math.Round(model.Current * unitScale)));
                        data[2] = (byte)((ushort)sCurr >> 8);
                        data[3] = (byte)((ushort)sCurr & 0xFF);

                        // Bytes 4..5: 剩余容量 (Bit7=0: 10mAh; Bit7=1: 100mAh)
                        ushort uRemCap = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.RemainingCapacity * unitScale)));
                        data[4] = (byte)(uRemCap >> 8);
                        data[5] = (byte)(uRemCap & 0xFF);

                        // Bytes 6..7: 满充容量 (Bit7=0: 10mAh; Bit7=1: 100mAh)
                        ushort uFullCap = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.FullCapacity * unitScale)));
                        data[6] = (byte)(uFullCap >> 8);
                        data[7] = (byte)(uFullCap & 0xFF);

                        // Bytes 8..9: 循环次数
                        ushort uCycles = (ushort)Math.Max(0, Math.Min(65535, model.CycleCount));
                        data[8] = (byte)(uCycles >> 8);
                        data[9] = (byte)(uCycles & 0xFF);

                        // Bytes 10..11: 生产日期 (0x2078 -> 2016-03-24)
                        data[10] = 0x20;
                        data[11] = 0x78;

                        // Bytes 12..13: 均衡状态低字节 (1..16串)
                        ushort eqLow = model.StatusBalancing ? (ushort)0x0001 : (ushort)0x0000;
                        data[12] = (byte)(eqLow >> 8);
                        data[13] = (byte)(eqLow & 0xFF);

                        // Bytes 14..15: 均衡状态高字节 (17..32串)
                        data[14] = 0x00;
                        data[15] = 0x00;

                        // Bytes 16..17: 保护状态字 (2 字节)
                        ushort prot = 0;
                        if (model.ProtOverVolt) prot |= (0x0001 | 0x0004);
                        if (model.ProtUnderVolt) prot |= (0x0002 | 0x0008);
                        if (model.ProtHighTemp) prot |= (0x0010 | 0x0040);
                        if (model.ProtUnderTemp) prot |= (0x0020 | 0x0080);
                        if (model.ProtOverCurrent)
                        {
                            if (model.Current > 0) prot |= 0x0100;
                            else if (model.Current < 0) prot |= 0x0200;
                            else prot |= (0x0100 | 0x0200);
                        }
                        if (model.ProtShortCircuit) prot |= 0x0400;
                        if (model.ProtSystemFault) prot |= 0x0800;
                        if (model.ProtSoftStart || (_softLockChgMos && _softLockDisMos)) prot |= 0x1000;
                        data[16] = (byte)(prot >> 8);
                        data[17] = (byte)(prot & 0xFF);

                        // Byte 18: 软件版本 (V1.0)
                        data[18] = 0x10;

                        // Byte 19: RSOC (0..100)
                        data[19] = (byte)Math.Max(0, Math.Min(100, Math.Round(model.SOC)));

                        // Byte 20: FET 控制状态字 (常态充放开=0x03; Bit7=1 表示 100mA/100mAh 单位)
                        byte fet = 0;
                        if (model.CanCharge && !_softLockChgMos) fet |= 0x01;
                        if (model.CanDischarge && !_softLockDisMos) fet |= 0x02;
                        if (_heatingEnabled) fet |= 0x08;
                        if (useUnit100mA) fet |= 0x80;
                        data[20] = fet;

                        // Byte 21: 电池串数 (显式串数配置)
                        byte strings = (byte)model.GetEffectiveCellCount();
                        data[21] = strings;

                        // Byte 22: NTC 探头数量
                        data[22] = 0x02;

                        // Bytes 23..26: NTC 1 & NTC 2 (0.1K, 2731 + T*10)
                        int tK = 2731 + (int)Math.Round(model.Temperature * 10.0);
                        if (tK < 0) tK = 0;
                        if (tK > 65535) tK = 65535;
                        ushort uTk = (ushort)tK;
                        data[23] = (byte)(uTk >> 8);
                        data[24] = (byte)(uTk & 0xFF);
                        data[25] = (byte)(uTk >> 8);
                        data[26] = (byte)(uTk & 0xFF);

                        // Byte 27: 湿度 (50%)
                        data[27] = 50;

                        // Bytes 28..29: 告警状态字 (2 字节)
                        ushort alarm = 0;
                        if (model.WarnSingleOverVolt) alarm |= 0x0001;
                        if (model.WarnSingleUnderVolt) alarm |= 0x0002;
                        if (model.WarnGlobalOverVolt) alarm |= 0x0004;
                        if (model.WarnGlobalUnderVolt) alarm |= 0x0008;
                        if (model.WarnHighTemp) alarm |= (0x0010 | 0x0040);
                        if (model.WarnLowTemp) alarm |= (0x0020 | 0x0080);
                        if (model.WarnOverCurrent)
                        {
                            if (model.Current >= 0) alarm |= 0x0100;
                            if (model.Current <= 0) alarm |= 0x0200;
                        }
                        if (model.WarnVoltDiff) alarm |= 0x0400;
                        if (model.WarnLowCapacity) alarm |= 0x0800;
                        data[28] = (byte)(alarm >> 8);
                        data[29] = (byte)(alarm & 0xFF);

                        // Bytes 30..31: 满充容量 (同步当前单位倍率)
                        data[30] = data[6];
                        data[31] = data[7];

                        // Bytes 32..33: 剩余容量 (同步当前单位倍率)
                        data[32] = data[4];
                        data[33] = data[5];

                        // Bytes 34..35: 均衡电流 (mA)
                        ushort balCurr = model.StatusBalancing ? (ushort)200 : (ushort)0;
                        data[34] = (byte)(balCurr >> 8);
                        data[35] = (byte)(balCurr & 0xFF);

                        string unitTag = useUnit100mA ? " [100mA/100mAh模式: FET Bit7=1]" : "";
                        string statusTag = "";
                        if (model.HasProtection) statusTag += string.Format(" [保护跳闸: {0}]", model.GetActiveProtectionsSummary());
                        if (model.HasWarning) statusTag += string.Format(" [告警提示: {0}]", model.GetActiveWarningsSummary());

                        txResponse = JbdChecksum.BuildResponse(0x03, 0x00, data, callbackId);
                        decodedInfo = string.Format("[JBD V12] 响应 0x03 基本信息: 电压={0:F2}V, 电流={1:F2}A, SOC={2:F0}%, 串数={3}S, 保护=0x{4:X4}, 告警=0x{5:X4}{6}{7}",
                            model.Voltage, model.Current, model.SOC, strings, prot, alarm, unitTag, statusTag);
                        return true;
                    }
                    else if (cmd == 0x04)
                    {
                        // 0x04: 读电芯单体电压 (mV)
                        int cellCount = model.GetEffectiveCellCount();
                        byte[] data = new byte[cellCount * 2];
                        ushort maxCellMv, minCellMv;
                        for (int i = 0; i < cellCount; i++)
                        {
                            ushort cellMv = model.GetCellMillivolts(i, cellCount, out maxCellMv, out minCellMv);
                            data[i * 2] = (byte)(cellMv >> 8);
                            data[i * 2 + 1] = (byte)(cellMv & 0xFF);
                        }
                        txResponse = JbdChecksum.BuildResponse(0x04, 0x00, data, callbackId);
                        decodedInfo = string.Format("[JBD V12] 响应 0x04 单体电压: 共 {0} 串电芯", cellCount);
                        return true;
                    }
                    else if (cmd == 0x05)
                    {
                        // 0x05: 读硬件版本/型号 ASCII
                        byte[] data = Encoding.ASCII.GetBytes("JBD-UP16S012-V12");
                        txResponse = JbdChecksum.BuildResponse(0x05, 0x00, data, callbackId);
                        decodedInfo = "[JBD V12] 响应 0x05 硬件型号: JBD-UP16S012-V12";
                        return true;
                    }
                    else if (cmd == 0xAA)
                    {
                        // 0xAA: 读保护历史统计
                        byte[] data = new byte[24];
                        ushort[] hist = new ushort[12];
                        hist[0] = model.ProtShortCircuit ? (ushort)1 : (ushort)0;
                        hist[1] = (model.ProtOverCurrent && model.Current >= 0) ? (ushort)1 : (ushort)0;
                        hist[2] = (model.ProtOverCurrent && model.Current <= 0) ? (ushort)1 : (ushort)0;
                        hist[3] = model.ProtOverVolt ? (ushort)1 : (ushort)0;
                        hist[4] = model.ProtUnderVolt ? (ushort)1 : (ushort)0;
                        hist[5] = model.ProtHighTemp ? (ushort)1 : (ushort)0;
                        hist[6] = model.ProtUnderTemp ? (ushort)1 : (ushort)0;
                        hist[7] = model.ProtHighTemp ? (ushort)1 : (ushort)0;
                        hist[8] = model.ProtUnderTemp ? (ushort)1 : (ushort)0;
                        hist[9] = model.ProtOverVolt ? (ushort)1 : (ushort)0;
                        hist[10] = model.ProtUnderVolt ? (ushort)1 : (ushort)0;
                        hist[11] = 1; // 系统重启次数
                        for (int i = 0; i < 12; i++)
                        {
                            data[i * 2] = (byte)(hist[i] >> 8);
                            data[i * 2 + 1] = (byte)(hist[i] & 0xFF);
                        }
                        txResponse = JbdChecksum.BuildResponse(0xAA, 0x00, data, callbackId);
                        decodedInfo = "[JBD V12] 响应 0xAA 保护历史统计";
                        return true;
                    }
                    else if (cmd == 0xF6)
                    {
                        // 0xF6: 读电芯内阻 (15串, 0.1mΩ, 0x0005 = 0.5mΩ)
                        byte[] data = new byte[30];
                        for (int i = 0; i < 15; i++)
                        {
                            data[i * 2] = 0x00;
                            data[i * 2 + 1] = 0x05;
                        }
                        txResponse = JbdChecksum.BuildResponse(0xF6, 0x00, data, callbackId);
                        decodedInfo = "[JBD V12] 响应 0xF6 电池内阻 (15串 0.5mΩ)";
                        return true;
                    }
                    else if (cmd == 0x00)
                    {
                        // 0x00: 读芯片方案 (TI 方案)
                        txResponse = JbdChecksum.BuildResponse(0x00, 0x00, new byte[] { 0x00, 0x00 }, callbackId);
                        decodedInfo = "[JBD V12] 响应 0x00 芯片方案: TI 方案 (0x0000)";
                        return true;
                    }
                    else if (cmd == 0xFA)
                    {
                        // 0xFA: 读配置参数
                        if (reqDataLen < 3)
                        {
                            txResponse = JbdChecksum.BuildResponse(0xFA, 0x81, new byte[0], callbackId);
                            decodedInfo = "[JBD V12] 读参数错误 (0x81): 载荷长度不足";
                            return true;
                        }

                        byte paramAddrHi = rxBuffer[4];
                        byte paramAddrLo = rxBuffer[5];
                        byte regCount = rxBuffer[6];
                        ushort paramAddr = (ushort)((paramAddrHi << 8) | paramAddrLo);

                        if (regCount == 0 || regCount > 95)
                        {
                            txResponse = JbdChecksum.BuildResponse(0xFA, 0x81, new byte[0], callbackId);
                            decodedInfo = "[JBD V12] 读参数错误 (0x81): 寄存器数量超出范围";
                            return true;
                        }

                        if (paramAddr == 0x0038 && regCount == 0x10)
                        {
                            byte[] data = new byte[3 + 32];
                            data[0] = paramAddrHi;
                            data[1] = paramAddrLo;
                            data[2] = regCount;
                            data[3] = 0x05;
                            byte[] nameBytes = Encoding.ASCII.GetBytes("DGJBD");
                            Array.Copy(nameBytes, 0, data, 4, nameBytes.Length);
                            txResponse = JbdChecksum.BuildResponse(0xFA, 0x00, data, callbackId);
                            decodedInfo = "[JBD V12] 响应 0xFA 读厂商信息 (0x0038): DGJBD";
                            return true;
                        }
                        if (paramAddr == 0x0048 && regCount == 0x10)
                        {
                            byte[] data = new byte[3 + 32];
                            data[0] = paramAddrHi;
                            data[1] = paramAddrLo;
                            data[2] = regCount;
                            data[3] = 16;
                            byte[] codeBytes = Encoding.ASCII.GetBytes("JBD-BMS-V12-PROT");
                            Array.Copy(codeBytes, 0, data, 4, codeBytes.Length);
                            txResponse = JbdChecksum.BuildResponse(0xFA, 0x00, data, callbackId);
                            decodedInfo = "[JBD V12] 响应 0xFA 读BMS编码 (0x0048)";
                            return true;
                        }
                        if (paramAddr == 0x0058 && regCount == 0x10)
                        {
                            byte[] data = new byte[3 + 32];
                            data[0] = paramAddrHi;
                            data[1] = paramAddrLo;
                            data[2] = regCount;
                            data[3] = 15;
                            byte[] barBytes = Encoding.ASCII.GetBytes("SN2026110500123");
                            Array.Copy(barBytes, 0, data, 4, barBytes.Length);
                            txResponse = JbdChecksum.BuildResponse(0xFA, 0x00, data, callbackId);
                            decodedInfo = "[JBD V12] 响应 0xFA 读条码 (0x0058)";
                            return true;
                        }

                        byte[] respData = new byte[3 + regCount * 2];
                        respData[0] = paramAddrHi;
                        respData[1] = paramAddrLo;
                        respData[2] = regCount;
                        int effCells = model.GetEffectiveCellCount();
                        for (int k = 0; k < regCount; k++)
                        {
                            ushort pIdx = (ushort)(paramAddr + k);
                            ushort val = 0;
                            switch (pIdx)
                            {
                                case 0: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.FullCapacity * 100.0))); break;
                                case 1: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.RemainingCapacity * 100.0))); break;
                                case 2: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round((model.CVVoltage / effCells) * 1000.0))); break;
                                case 3: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round((model.CutoffVoltage / effCells) * 1000.0))); break;
                                case 7: val = (ushort)Math.Max(0, Math.Min(65535, model.CycleCount)); break;
                                case 16: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.CVVoltage * 100.0))); break;
                                case 18: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.CutoffVoltage * 100.0))); break;
                                case 24: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.MaxChargeCurrent * 100.0))); break;
                                case 25:
                                    int disRaw = (int)Math.Round(model.MaxDischargeCurrent * 100.0);
                                    val = (ushort)(0x10000 - (disRaw & 0xFFFF));
                                    break;
                                case 31: val = (ushort)effCells; break;
                                case 114: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.Voltage * 10.0))); break;
                                case 117: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.CVVoltage * 10.0))); break;
                                case 118: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.MaxDischargeCurrent))); break;
                                case 119: val = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.MaxChargeCurrent))); break;
                                default: val = 0; break;
                            }
                            respData[3 + k * 2] = (byte)(val >> 8);
                            respData[3 + k * 2 + 1] = (byte)(val & 0xFF);
                        }
                        txResponse = JbdChecksum.BuildResponse(0xFA, 0x00, respData, callbackId);
                        decodedInfo = string.Format("[JBD V12] 响应 0xFA 读参数: 起始=0x{0:X4}, 数量={1}", paramAddr, regCount);
                        return true;
                    }
                    else
                    {
                        txResponse = JbdChecksum.BuildResponse(cmd, 0x80, new byte[0], callbackId);
                        decodedInfo = string.Format("[JBD V12] 错误 (0x80): 读功能码 0x{0:X2} 不存在", cmd);
                        return true;
                    }
                }
                // ── 2. 写寄存器指令 (RW == 0x5A) ──
                else if (rw == 0x5A)
                {
                    if (cmd == 0xFB)
                    {
                        // 0xFB: 控制 MOS 开关
                        if (reqDataLen >= 2)
                        {
                            byte yy = rxBuffer[4];
                            byte xx = rxBuffer[5];
                            if (yy == 0x0A)
                            {
                                bool en = (xx == 0x00);
                                model.StatusChargeEnable = en;
                                model.StatusDischargeEnable = en;
                                _softLockChgMos = !en;
                                _softLockDisMos = !en;
                            }
                            else if (yy == 0x01)
                            {
                                bool en = (xx == 0x00);
                                model.StatusChargeEnable = en;
                                _softLockChgMos = !en;
                            }
                            else if (yy == 0x00)
                            {
                                bool chgEn = (xx & 0x01) == 0;
                                bool disEn = (xx & 0x02) == 0;
                                model.StatusChargeEnable = chgEn;
                                model.StatusDischargeEnable = disEn;
                                _softLockChgMos = !chgEn;
                                _softLockDisMos = !disEn;
                            }
                        }
                        txResponse = JbdChecksum.BuildResponse(0xFB, 0x00, new byte[0], callbackId);
                        decodedInfo = string.Format("[JBD V12] 响应 0xFB 控制MOS: 充MOS={0}, 放MOS={1}",
                            model.StatusChargeEnable ? "开" : "关", model.StatusDischargeEnable ? "开" : "关");
                        return true;
                    }
                    else if (cmd == 0x00)
                    {
                        // 0x00: 进入工厂模式 (密码 0x5678)
                        if (reqDataLen >= 2 && rxBuffer[4] == 0x56 && rxBuffer[5] == 0x78)
                        {
                            _inFactoryMode = true;
                            txResponse = JbdChecksum.BuildResponse(0x00, 0x00, new byte[0], callbackId);
                            decodedInfo = "[JBD V12] 响应 0x00 进入工厂模式成功 (密码 0x5678)";
                        }
                        else
                        {
                            txResponse = JbdChecksum.BuildResponse(0x00, 0x81, new byte[0], callbackId);
                            decodedInfo = "[JBD V12] 响应 0x00 进入工厂模式失败 (密码错误)";
                        }
                        return true;
                    }
                    else if (cmd == 0x01)
                    {
                        // 0x01: 退出工厂模式
                        _inFactoryMode = false;
                        txResponse = JbdChecksum.BuildResponse(0x01, 0x00, new byte[0], callbackId);
                        decodedInfo = "[JBD V12] 响应 0x01 退出工厂模式成功";
                        return true;
                    }
                    else if (cmd == 0xFA)
                    {
                        // 0xFA: 写配置参数
                        if (reqDataLen >= 3)
                        {
                            byte addrHi = rxBuffer[4];
                            byte addrLo = rxBuffer[5];
                            byte countN = rxBuffer[6];
                            ushort addr = (ushort)((addrHi << 8) | addrLo);
                            if (reqDataLen >= 3 + countN * 2)
                            {
                                for (int k = 0; k < countN; k++)
                                {
                                    ushort regIdx = (ushort)(addr + k);
                                    ushort regVal = (ushort)((rxBuffer[7 + k * 2] << 8) | rxBuffer[7 + k * 2 + 1]);
                                    switch (regIdx)
                                    {
                                        case 0: model.FullCapacity = regVal / 100.0; break;
                                        case 1: model.RemainingCapacity = regVal / 100.0; break;
                                        case 7: model.CycleCount = regVal; break;
                                        case 16: model.CVVoltage = regVal / 100.0; break;
                                        case 18: model.CutoffVoltage = regVal / 100.0; break;
                                        case 24: model.MaxChargeCurrent = regVal / 100.0; break;
                                        case 25:
                                            short sVal = (short)regVal;
                                            model.MaxDischargeCurrent = Math.Abs(sVal) / 100.0;
                                            break;
                                        case 117: model.CVVoltage = regVal / 10.0; break;
                                        case 118: model.MaxDischargeCurrent = regVal; break;
                                        case 119: model.MaxChargeCurrent = regVal; break;
                                    }
                                }
                            }
                            txResponse = JbdChecksum.BuildResponse(0xFA, 0x00, new byte[] { addrHi, addrLo, countN }, callbackId);
                            decodedInfo = string.Format("[JBD V12] 响应 0xFA 写参数成功: 起始=0x{0:X4}, 数量={1}", addr, countN);
                            return true;
                        }
                        txResponse = JbdChecksum.BuildResponse(0xFA, 0x81, new byte[0], callbackId);
                        decodedInfo = "[JBD V12] 写参数错误 (0x81)";
                        return true;
                    }
                    else if (cmd == 0x0A)
                    {
                        // 0x0A: 控制指令
                        byte subCmd = reqDataLen >= 1 ? rxBuffer[4] : (byte)0;
                        if (subCmd == 0x01)
                        {
                            model.RemainingCapacity = model.FullCapacity * (model.SOC / 100.0);
                        }
                        else if (subCmd == 0x04)
                        {
                            model.ClearAllAlarmsAndProtections();
                        }
                        else if (subCmd == 0x05 || subCmd == 0x06 || subCmd == 0x08)
                        {
                            model.StatusSleep = true;
                            model.StatusChargeEnable = false;
                            model.StatusDischargeEnable = false;
                        }
                        else if (subCmd == 0x07)
                        {
                            model.StatusBalancing = true;
                        }
                        else if (subCmd == 0x0C)
                        {
                            _heatingEnabled = true;
                        }
                        txResponse = JbdChecksum.BuildResponse(0x0A, 0x00, new byte[0], callbackId);
                        decodedInfo = string.Format("[JBD V12] 响应 0x0A 控制指令: 子命令=0x{0:X2}", subCmd);
                        return true;
                    }
                    else if (cmd == 0x0B || cmd == 0x06 || cmd == 0x07 || cmd == 0x08)
                    {
                        txResponse = JbdChecksum.BuildResponse(cmd, 0x00, new byte[0], callbackId);
                        decodedInfo = string.Format("[JBD V12] 响应 0x{0:X2} 密码/配置指令", cmd);
                        return true;
                    }
                    else if (cmd == 0xF6)
                    {
                        txResponse = JbdChecksum.BuildResponse(0xF6, 0x00, new byte[0], callbackId);
                        decodedInfo = "[JBD V12] 响应 0xF6 写入内阻成功";
                        return true;
                    }
                    else if (cmd == 0xFC)
                    {
                        // 0xFC: 控制加热
                        byte act = reqDataLen >= 1 ? rxBuffer[4] : (byte)0;
                        if (act == 0x01) _heatingEnabled = true;
                        else if (act == 0x02) _heatingEnabled = false;
                        txResponse = JbdChecksum.BuildResponse(0xFC, 0x00, new byte[0], callbackId);
                        decodedInfo = string.Format("[JBD V12] 响应 0xFC 控制加热: {0}", _heatingEnabled ? "开启" : "关闭");
                        return true;
                    }
                    else
                    {
                        txResponse = JbdChecksum.BuildResponse(cmd, 0x80, new byte[0], callbackId);
                        decodedInfo = string.Format("[JBD V12] 错误 (0x80): 写功能码 0x{0:X2} 不存在", cmd);
                        return true;
                    }
                }
                else
                {
                    txResponse = JbdChecksum.BuildResponse(cmd, 0x80, new byte[0], callbackId);
                    decodedInfo = string.Format("[JBD V12] 错误 (0x80): 读写标志 0x{0:X2} 非法", rw);
                    return true;
                }
            }
        }

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            if (pollPhase == 1)
            {
                // Phase 1: 0x04 读单体电压
                return JbdChecksum.BuildRequest(0xA5, 0x04, new byte[0]);
            }
            // Phase 0: 0x03 读基本信息与状态
            return JbdChecksum.BuildRequest(0xA5, 0x03, new byte[0]);
        }

        public bool TryDecodeMasterResponse(byte[] rxBuffer, int rxLength, BmsDataModel model, out string decodedInfo)
        {
            decodedInfo = "";
            int coreLen;
            if (!JbdChecksum.VerifyResponse(rxBuffer, 0, rxLength, out coreLen))
                return false;

            byte cmd = rxBuffer[1];
            byte status = rxBuffer[2];
            byte dataLen = rxBuffer[3];

            if (status != 0x00)
            {
                string errDesc = status == 0x80 ? "命令不存在" :
                                 (status == 0x81 ? "非法操作/未进工厂模式" :
                                 (status == 0x82 ? "校验错误" :
                                 (status == 0x83 ? "密码错误" :
                                 (status == 0x84 ? "数据越界" : ("未知错误 0x" + status.ToString("X2"))))));
                decodedInfo = string.Format("[JBD V12] 从机异常响应 CMD=0x{0:X2}, 状态码=0x{1:X2} ({2})", cmd, status, errDesc);
                return true;
            }

            lock (model.SyncRoot)
            {
                if (cmd == 0x03 && dataLen >= 23)
                {
                    // Bytes 4..5: 总电压 (10mV)
                    ushort uVolt = (ushort)((rxBuffer[4] << 8) | rxBuffer[5]);
                    model.Voltage = uVolt / 100.0;

                    // Bytes 6..7: 电流 (10mA 有符号)
                    short sCurr = (short)((rxBuffer[6] << 8) | rxBuffer[7]);

                    // Bytes 8..9: 剩余容量 (10mAh)
                    ushort uRemCap = (ushort)((rxBuffer[8] << 8) | rxBuffer[9]);

                    // Bytes 10..11: 满充容量 (10mAh)
                    ushort uFullCap = (ushort)((rxBuffer[10] << 8) | rxBuffer[11]);

                    // Bytes 12..13: 循环次数
                    ushort uCycles = (ushort)((rxBuffer[12] << 8) | rxBuffer[13]);
                    model.CycleCount = uCycles;

                    // Bytes 16..19: 均衡状态 (低16串 + 高16串)
                    ushort eqLow = (ushort)((rxBuffer[16] << 8) | rxBuffer[17]);
                    ushort eqHigh = (ushort)((rxBuffer[18] << 8) | rxBuffer[19]);
                    model.StatusBalancing = (eqLow != 0 || eqHigh != 0);

                    // Bytes 20..21: 保护状态字
                    ushort prot = (ushort)((rxBuffer[20] << 8) | rxBuffer[21]);
                    model.ProtOverVolt = (prot & 0x0005) != 0;
                    model.ProtUnderVolt = (prot & 0x000A) != 0;
                    model.ProtHighTemp = (prot & 0x0050) != 0;
                    model.ProtUnderTemp = (prot & 0x00A0) != 0;
                    model.ProtOverCurrent = (prot & 0x0300) != 0;
                    model.ProtShortCircuit = (prot & 0x0400) != 0;
                    model.ProtSystemFault = (prot & 0x0800) != 0;
                    model.ProtSoftStart = (prot & 0x1000) != 0;

                    // Byte 23: SOC (0..100%)
                    model.SOC = rxBuffer[23];

                    // Byte 24: FET 状态
                    byte fet = rxBuffer[24];
                    model.StatusChargeEnable = (fet & 0x01) != 0;
                    model.StatusDischargeEnable = (fet & 0x02) != 0;
                    double iScale = (fet & 0x80) != 0 ? 10.0 : 100.0;
                    model.Current = sCurr / iScale;
                    model.RemainingCapacity = uRemCap / iScale;
                    model.FullCapacity = uFullCap / iScale;

                    // NTC 探头温度解析
                    byte ntcCount = rxBuffer[26];
                    if (dataLen >= 23 + ntcCount * 2 && ntcCount > 0)
                    {
                        ushort tK = (ushort)((rxBuffer[27] << 8) | rxBuffer[28]);
                        model.Temperature = (tK - 2731) / 10.0;
                    }

                    // 告警状态字解析 (在 NTC 与 1 字节湿度之后)
                    int alarmOffset = 4 + 23 + ntcCount * 2 + 1;
                    if (alarmOffset + 1 < 4 + dataLen)
                    {
                        ushort alarm = (ushort)((rxBuffer[alarmOffset] << 8) | rxBuffer[alarmOffset + 1]);
                        model.WarnSingleOverVolt = (alarm & 0x0001) != 0;
                        model.WarnSingleUnderVolt = (alarm & 0x0002) != 0;
                        model.WarnGlobalOverVolt = (alarm & 0x0004) != 0;
                        model.WarnGlobalUnderVolt = (alarm & 0x0008) != 0;
                        model.WarnHighTemp = (alarm & 0x0050) != 0;
                        model.WarnLowTemp = (alarm & 0x00A0) != 0;
                        model.WarnOverCurrent = (alarm & 0x0300) != 0;
                        model.WarnVoltDiff = (alarm & 0x0400) != 0;
                        model.WarnLowCapacity = (alarm & 0x0800) != 0;
                    }

                    string statusTag = "";
                    if (model.HasProtection) statusTag += string.Format(" [保护跳闸: {0}]", model.GetActiveProtectionsSummary());
                    if (model.HasWarning) statusTag += string.Format(" [告警提示: {0}]", model.GetActiveWarningsSummary());

                    decodedInfo = string.Format("[JBD V12] 成功解析 0x03 基本信息: 电压={0:F2}V, 电流={1:F2}A, SOC={2:F0}%, 温度={3:F1}°C, 满充容量={4:F2}Ah{5}",
                        model.Voltage, model.Current, model.SOC, model.Temperature, model.FullCapacity, statusTag);
                    return true;
                }
                else if (cmd == 0x04)
                {
                    int cellCount = dataLen / 2;
                    decodedInfo = string.Format("[JBD V12] 成功解析 0x04 单体电压: 共 {0} 串电芯", cellCount);
                    return true;
                }
                else if (cmd == 0x05)
                {
                    string modelName = Encoding.ASCII.GetString(rxBuffer, 4, dataLen).TrimEnd('\0', ' ');
                    decodedInfo = string.Format("[JBD V12] 成功解析 0x05 硬件型号: {0}", modelName);
                    return true;
                }
                else if (cmd == 0xAA)
                {
                    decodedInfo = string.Format("[JBD V12] 成功解析 0xAA 保护历史统计 (数据长度={0} Bytes)", dataLen);
                    return true;
                }
                else if (cmd == 0xFA)
                {
                    decodedInfo = string.Format("[JBD V12] 成功解析 0xFA 参数读写响应 (数据长度={0} Bytes)", dataLen);
                    return true;
                }
                else if (cmd == 0xFB || cmd == 0x0A || cmd == 0xFC || cmd == 0x00 || cmd == 0x01 || cmd == 0xF6)
                {
                    decodedInfo = string.Format("[JBD V12] 成功执行指令 0x{0:X2} 响应", cmd);
                    return true;
                }
            }

            return false;
        }
    }

    #region Inverter & Protocol Classifier (3KU / 6KU 机型与协议智能识别引擎)

    public enum InverterModel
    {
        Unknown,
        INV_3KU,
        INV_6KU
    }

    public enum DetectedProtocolType
    {
        Unknown,
        Growatt,
        Voltronic,
        Pylontech,
        Cvte,
        LibModbus,
        Jbd
    }

    public class DetectionResult
    {
        public DetectedProtocolType Protocol { get; set; }
        public InverterModel Model { get; set; }
        public string RawSignature { get; set; }
        public DateTime Timestamp { get; set; }
        public byte SlaveId { get; set; }
        public bool IsSignatureConfirmed { get; set; }

        public DetectionResult()
        {
            Protocol = DetectedProtocolType.Unknown;
            Model = InverterModel.Unknown;
            RawSignature = "";
            Timestamp = DateTime.Now;
            SlaveId = 0;
            IsSignatureConfirmed = false;
        }
    }

    public class InverterDetector
    {
        private InverterModel _latchedModel = InverterModel.Unknown;
        private DateTime _lastPylon60Time = DateTime.MinValue;

        public void Reset()
        {
            _latchedModel = InverterModel.Unknown;
            _lastPylon60Time = DateTime.MinValue;
        }

        public DetectionResult InspectFrame(byte[] buffer, int offset, int length)
        {
            if (buffer == null || length < 4) return null;

            // 0. JBD V12 特征帧校验 (0xDD 起始，0xA5/0x5A 读写，0x77 结束)
            int jbdCoreLen, jbdCbLen;
            if (buffer[offset] == 0xDD && JbdChecksum.VerifyRequest(buffer, offset, length, out jbdCoreLen, out jbdCbLen))
            {
                byte rw = buffer[offset + 1];
                byte cmd = buffer[offset + 2];
                byte dLen = buffer[offset + 3];
                DetectionResult res = new DetectionResult();
                res.SlaveId = 1;
                res.Timestamp = DateTime.Now;
                res.RawSignature = string.Format("JBD V12: {0} CMD=0x{1:X2}, LEN={2}", rw == 0xA5 ? "读(0xA5)" : "写(0x5A)", cmd, dLen);
                res.Protocol = DetectedProtocolType.Jbd;
                res.Model = InverterModel.Unknown;
                res.IsSignatureConfirmed = true;

                return res;
            }

            // 0. SHENGYANG 特征帧校验 (8 字节 0x04 读输入寄存器 或 4 字节 0x11 读产品信息，拨码地址限定 0..15)
            if (length >= 8 && buffer[offset] <= 15 && buffer[offset + 1] == 0x04 && ModbusCrc.Check(buffer, 8, offset))
            {
                byte slaveId = buffer[offset];
                ushort startReg = (ushort)((buffer[offset + 2] << 8) | buffer[offset + 3]);
                ushort regCount = (ushort)((buffer[offset + 4] << 8) | buffer[offset + 5]);

                if (startReg >= 0x1000 && startReg <= 0x1016)
                {
                    DetectionResult res = new DetectionResult();
                    res.SlaveId = slaveId;
                    res.Timestamp = DateTime.Now;
                    res.RawSignature = string.Format("SHENGYANG 0x04 读输入寄存器: 从机 ID={0}, 起始=0x{1:X4}, 长度={2}", slaveId, startReg, regCount);
                    res.Protocol = DetectedProtocolType.LibModbus;
                    res.Model = InverterModel.Unknown;
                    res.IsSignatureConfirmed = true;

                    return res;
                }
            }
            else if (length >= 4 && buffer[offset] <= 15 && buffer[offset + 1] == 0x11 && ModbusCrc.Check(buffer, 4, offset))
            {
                byte slaveId = buffer[offset];
                DetectionResult res = new DetectionResult();
                res.SlaveId = slaveId;
                res.Timestamp = DateTime.Now;
                res.RawSignature = string.Format("SHENGYANG 0x11 读产品信息: 从机 ID={0}", slaveId);
                res.Protocol = DetectedProtocolType.LibModbus;
                res.Model = InverterModel.Unknown;
                res.IsSignatureConfirmed = true;

                return res;
            }

            // 1. 优先尝试 Modbus RTU 校验 (8 字节标准查询: [ID, 0x03, AddrH, AddrL, LenH, LenL, CrcH, CrcL])
            if (length >= 8 && buffer[offset + 1] == 0x03 && ModbusCrc.Check(buffer, 8, offset))
            {
                byte slaveId = buffer[offset];
                ushort startReg = (ushort)((buffer[offset + 2] << 8) | buffer[offset + 3]);
                ushort regCount = (ushort)((buffer[offset + 4] << 8) | buffer[offset + 5]);

                DetectionResult res = new DetectionResult();
                res.SlaveId = slaveId;
                res.Timestamp = DateTime.Now;
                res.RawSignature = string.Format("Modbus RTU: 从机 ID={0}, 起始=0x{1:X4}, 长度={2}", slaveId, startReg, regCount);

                // ── A. 精确专属特征帧识别 (优先级最高) ──
                if (startReg == 0x0014 && regCount == 15)
                {
                    // Growatt 3KU 专属查询: 0x0014 长度 15
                    res.Protocol = DetectedProtocolType.Growatt;
                    res.Model = InverterModel.INV_3KU;
                    res.IsSignatureConfirmed = true;
                }
                else if (startReg == 0x0001 && regCount == 35)
                {
                    // Growatt 6KU 专属查询: 0x0001 长度 35
                    res.Protocol = DetectedProtocolType.Growatt;
                    res.Model = InverterModel.INV_6KU;
                    res.IsSignatureConfirmed = true;
                }
                else if (startReg == 0x0020 && regCount == 12)
                {
                    // CVTE 6KU 保持寄存器专属轮询: 0x0020 长度 12
                    res.Protocol = DetectedProtocolType.Cvte;
                    res.Model = InverterModel.INV_6KU; // CVTE RS485 仅在 6KU 固件中存在
                    res.IsSignatureConfirmed = true;
                }
                else if (startReg == 0x0040 && regCount == 12)
                {
                    // CVTE 6KU 告警保持寄存器轮询: 0x0040 长度 12
                    res.Protocol = DetectedProtocolType.Cvte;
                    res.Model = InverterModel.INV_6KU;
                    res.IsSignatureConfirmed = true;
                }
                else if (startReg == 0x0070 && regCount == 3)
                {
                    // Voltronic 3KU 第三阶段专属: 0x0070 长度 3
                    res.Protocol = DetectedProtocolType.Voltronic;
                    res.Model = InverterModel.INV_3KU;
                    res.IsSignatureConfirmed = true;
                }
                else if (startReg == 0x0070 && regCount == 5)
                {
                    // Voltronic 6KU 第三阶段专属: 0x0070 长度 5 (读取 0x0074 状态字)
                    res.Protocol = DetectedProtocolType.Voltronic;
                    res.Model = InverterModel.INV_6KU;
                    res.IsSignatureConfirmed = true;
                }
                else if (startReg == 0x0026 && regCount == 10)
                {
                    // Voltronic 6KU 第四阶段专属: 0x0026 长度 10 (温度探头)
                    res.Protocol = DetectedProtocolType.Voltronic;
                    res.Model = InverterModel.INV_6KU;
                    res.IsSignatureConfirmed = true;
                }
                else if (startReg == 0x0030 && regCount == 6)
                {
                    // Voltronic 第一阶段 (3KU 与 6KU 共有)
                    res.Protocol = DetectedProtocolType.Voltronic;
                    res.Model = _latchedModel != InverterModel.Unknown ? _latchedModel : InverterModel.Unknown;
                    res.IsSignatureConfirmed = (_latchedModel != InverterModel.Unknown);
                }
                else if (startReg == 0x0060 && regCount == 10)
                {
                    // Voltronic 第二阶段 (3KU 与 6KU 共有)
                    res.Protocol = DetectedProtocolType.Voltronic;
                    res.Model = _latchedModel != InverterModel.Unknown ? _latchedModel : InverterModel.Unknown;
                    res.IsSignatureConfirmed = (_latchedModel != InverterModel.Unknown);
                }
                // ── B. 次级范围启发式识别 ──
                else if ((startReg >= 0x0040 && startReg <= 0x005A) || (startReg >= 0x0065 && startReg <= 0x006E))
                {
                    res.Protocol = DetectedProtocolType.Cvte;
                    res.Model = InverterModel.INV_6KU;
                    res.IsSignatureConfirmed = true;
                }
                else if (startReg == 0x0001 || (startReg >= 0x0071 && startReg <= 0x0080))
                {
                    res.Protocol = DetectedProtocolType.Growatt;
                    res.Model = _latchedModel != InverterModel.Unknown ? _latchedModel : InverterModel.Unknown;
                    res.IsSignatureConfirmed = (_latchedModel != InverterModel.Unknown);
                }
                else
                {
                    res.Protocol = DetectedProtocolType.Unknown;
                    res.Model = InverterModel.Unknown;
                }

                if (res.Protocol != DetectedProtocolType.Unknown)
                {
                    if (res.Model != InverterModel.Unknown) _latchedModel = res.Model;
                }

                return res;
            }

            // 2. 尝试 Pylontech ASCII 校验 ('~' 起始，'\r' 结尾)
            if (buffer[offset] == (byte)'~')
            {
                int crIdx = -1;
                for (int i = offset; i < offset + length; i++)
                {
                    if (buffer[i] == 0x0D)
                    {
                        crIdx = i;
                        break;
                    }
                }

                if (crIdx > offset)
                {
                    int asciiLen = crIdx - offset + 1;
                    byte[] slice = new byte[asciiLen];
                    Array.Copy(buffer, offset, slice, 0, asciiLen);
                    if (PylonChecksum.Verify(slice))
                    {
                        string asciiStr = Encoding.ASCII.GetString(slice);
                        string cid2 = asciiStr.Substring(7, 2).ToUpper();
                        byte slaveId = 0;
                        if (asciiStr.Length >= 5)
                        {
                            string adrStr = asciiStr.Substring(3, 2);
                            byte.TryParse(adrStr, System.Globalization.NumberStyles.HexNumber, null, out slaveId);
                        }

                        DetectionResult res = new DetectionResult();
                        res.SlaveId = slaveId;
                        res.Protocol = DetectedProtocolType.Pylontech;
                        res.Timestamp = DateTime.Now;
                        res.RawSignature = string.Format("Pylontech ASCII: ADR=0x{0:X2}, CID2={1}H", slaveId, cid2);

                        if (cid2 == "60")
                        {
                            // 6KU 专属请求: CID2 60H (协议版本查询)
                            _lastPylon60Time = res.Timestamp;
                            res.Model = InverterModel.INV_6KU;
                            res.IsSignatureConfirmed = true;
                        }
                        else if (cid2 == "61" || cid2 == "62" || cid2 == "63")
                        {
                            if (_lastPylon60Time != DateTime.MinValue && (res.Timestamp - _lastPylon60Time).TotalSeconds < 15.0)
                            {
                                res.Model = InverterModel.INV_6KU;
                                res.IsSignatureConfirmed = true;
                            }
                            else
                            {
                                // 无 60H 前导查询时可能是 48V UPS1-3K 或 24V 3KU，保持 Unknown 以免降级用户电压设定
                                res.Model = InverterModel.Unknown;
                                res.IsSignatureConfirmed = false;
                            }
                        }

                        if (res.Model != InverterModel.Unknown) _latchedModel = res.Model;
                        return res;
                    }
                }
            }

            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 6. 报文深度解剖器 (支持双击日志行逐字段拆解 Modbus RTU / Voltronic / Pylontech ASCII)
    // ─────────────────────────────────────────────────────────────
    public static class FrameAnatomyInspector
    {
        private static string InferDissectedProtocolLabel(byte[] raw, string summaryPart, string currentProtocolName, out string resolvedModbusProto)
        {
            resolvedModbusProto = currentProtocolName;
            if (raw != null && raw.Length > 0)
            {
                if (raw[0] == 0x7E)
                {
                    resolvedModbusProto = "PYLONTECH";
                    return "PYLONTECH (RS485 ASCII) [按帧特征识别]";
                }
                if (raw[0] == 0xDD)
                {
                    resolvedModbusProto = "JBD";
                    return "JBD (RS485/UART V12) [按帧特征识别]";
                }
                if (raw.Length >= 2)
                {
                    byte fc = raw[1];
                    if (fc == 0x11 || fc == 0x04 || (raw.Length == 5 && (fc == 0x84 || fc == 0x91)))
                    {
                        resolvedModbusProto = "SHENGYANG";
                        return "SHENGYANG (RS485 V1.30) [按功能码识别]";
                    }
                    if (fc == 0x03 && raw.Length >= 6)
                    {
                        int vRegCount = (raw[2] << 8) | raw[3];
                        if (4 + vRegCount * 2 + 2 == raw.Length && (3 + raw[2] + 2 != raw.Length))
                        {
                            resolvedModbusProto = "VOLTRONIC";
                            return "VOLTRONIC (Modbus 4字节帧头) [按帧头识别]";
                        }
                    }
                    if (raw.Length == 8 && fc == 0x03)
                    {
                        ushort startReg = (ushort)((raw[2] << 8) | raw[3]);
                        ushort regCount = (ushort)((raw[4] << 8) | raw[5]);
                        if ((startReg == 0x0014 && regCount == 15) || (startReg == 0x0001 && regCount == 35) ||
                            (startReg >= 0x0071 && startReg <= 0x0080))
                        {
                            resolvedModbusProto = "GROWATT";
                            return "GROWATT (Modbus RTU) [按寄存器地址识别]";
                        }
                        if ((startReg == 0x0030 && regCount <= 6) || (startReg == 0x0060 && regCount <= 10) ||
                            (startReg == 0x0070 && regCount <= 5) || (startReg == 0x0026 && regCount <= 10) ||
                            (startReg == 0x0010 && regCount == 1) || (startReg == 0x0011 && regCount <= 16))
                        {
                            resolvedModbusProto = "VOLTRONIC";
                            return "VOLTRONIC (Modbus RTU) [按寄存器地址识别]";
                        }
                        if ((startReg == 0x0020 && regCount == 12) || (startReg == 0x0040 && regCount == 12) ||
                            (startReg >= 0x0011 && startReg <= 0x001F) || (startReg >= 0x0040 && startReg <= 0x005A))
                        {
                            resolvedModbusProto = "CVTE";
                            return "CVTE (Modbus RTU) [按寄存器地址识别]";
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(summaryPart))
            {
                string upperSum = summaryPart.ToUpperInvariant();
                if (upperSum.Contains("[CVTE")) { resolvedModbusProto = "CVTE"; return "CVTE (Modbus RTU) [按日志摘要识别]"; }
                if (upperSum.Contains("[GROWATT")) { resolvedModbusProto = "GROWATT"; return "GROWATT (Modbus RTU) [按日志摘要识别]"; }
                if (upperSum.Contains("[VOLTRONIC")) { resolvedModbusProto = "VOLTRONIC"; return "VOLTRONIC (Modbus RTU) [按日志摘要识别]"; }
                if (upperSum.Contains("[SHENGYANG")) { resolvedModbusProto = "SHENGYANG"; return "SHENGYANG (RS485 V1.30) [按日志摘要识别]"; }
                if (upperSum.Contains("[JBD")) { resolvedModbusProto = "JBD"; return "JBD (RS485/UART V12) [按日志摘要识别]"; }
                if (upperSum.Contains("[PYLON")) { resolvedModbusProto = "PYLONTECH"; return "PYLONTECH (RS485 ASCII) [按日志摘要识别]"; }
            }

            string fallback = string.IsNullOrEmpty(currentProtocolName) ? "未知" : currentProtocolName;
            return fallback + " (按界面当前协议参考)";
        }

        public static string DissectLogLine(string logLine, string currentProtocolName)
        {
            if (string.IsNullOrEmpty(logLine)) return "未选中有效报文行。";

            string trimmed = logLine.Trim();
            string direction = "未知方向";
            int txIdx = trimmed.IndexOf("TX:");
            int rxIdx = trimmed.IndexOf("RX:");
            int hexStartIdx = -1;
            if (txIdx >= 0 && (rxIdx < 0 || txIdx < rxIdx))
            {
                direction = "TX (本机发出)";
                hexStartIdx = txIdx + 3;
            }
            else if (rxIdx >= 0)
            {
                direction = "RX (外部发来)";
                hexStartIdx = rxIdx + 3;
            }

            System.Collections.Generic.List<byte> bytes = new System.Collections.Generic.List<byte>();
            string summaryPart = "";
            if (hexStartIdx >= 0 && hexStartIdx < trimmed.Length)
            {
                string rest = trimmed.Substring(hexStartIdx).TrimStart();
                string[] tokens = rest.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int consumedChars = 0;
                for (int i = 0; i < tokens.Length; i++)
                {
                    string tk = tokens[i];
                    byte b;
                    if (tk.Length == 2 && byte.TryParse(tk, System.Globalization.NumberStyles.HexNumber, null, out b))
                    {
                        bytes.Add(b);
                    }
                    else
                    {
                        int idxInRest = rest.IndexOf(tk, consumedChars);
                        if (idxInRest >= 0) summaryPart = rest.Substring(idxInRest);
                        break;
                    }
                    consumedChars += tk.Length;
                }
            }

            byte[] raw = bytes.ToArray();
            string resolvedModbusProto = currentProtocolName;
            string frameProtocolLabel = (raw.Length > 0)
                ? InferDissectedProtocolLabel(raw, summaryPart, currentProtocolName, out resolvedModbusProto)
                : (string.IsNullOrEmpty(currentProtocolName) ? "未知" : currentProtocolName);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("══════════════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine("  BMS 协议帧深度解剖报告 (Frame Anatomy Inspector)");
            sb.AppendLine("══════════════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine(string.Format("• 传输方向 : {0}", direction));
            sb.AppendLine(string.Format("• {0} : {1}", raw.Length > 0 ? "报文协议" : "参考协议", frameProtocolLabel));
            sb.AppendLine(string.Format("• 字节长度 : {0} Bytes", bytes.Count));
            if (!string.IsNullOrEmpty(summaryPart))
            {
                AppendWrappedSummary(sb, "• 业务摘要 : ", summaryPart, 82);
            }

            if (bytes.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("⚠️ 当前日志行未包含可解析的十六进制字节流（请勾选【显示HEX】后捕获报文）。");
                sb.AppendLine("原始文本: " + trimmed);
                return sb.ToString().TrimEnd('\r', '\n');
            }

            AppendFormattedHexBlock(sb, "• 原始 HEX : ", raw);
            sb.AppendLine("──────────────────────────────────────────────────────────────────────────────────────");

            // 1. Pylontech ASCII 帧解剖
            if (raw[0] == 0x7E)
            {
                string ascii = Encoding.ASCII.GetString(raw).TrimEnd('\r', '\n');
                bool chkOk = PylonChecksum.Verify(raw);
                ushort calcChk = raw.Length >= 6 ? PylonChecksum.Calculate(raw, 1, raw.Length - 6) : (ushort)0;

                sb.AppendLine("[协议帧类型] : Pylontech RS485 ASCII 协议帧");
                AppendWrappedSummary(sb, "[ASCII 文本] : ", ascii + "\\r", 74);
                sb.AppendLine(string.Format("[校验码状态] : {0} (计算值: 0x{1:X4})", chkOk ? "✓ 校验通过 (PASS)" : "✕ 校验错误 (FAIL)", calcChk));
                sb.AppendLine();
                if (ascii.Length >= 17)
                {
                    string ver = ascii.Substring(1, 2);
                    string adr = ascii.Substring(3, 2);
                    string cid1 = ascii.Substring(5, 2);
                    string cid2 = ascii.Substring(7, 2);
                    string lenField = ascii.Substring(9, 4);
                    int infoHexLen = ascii.Length - 13 - 4;
                    string infoStr = infoHexLen > 0 ? ascii.Substring(13, infoHexLen) : "(空)";
                    string chkStr = ascii.Substring(ascii.Length - 4, 4);

                    sb.AppendLine("┌──────────┬──────────┬────────────────────────────────────────────┐");
                    sb.AppendLine("│ " + PadRightDisplay("字段名称", 8) + " │ " + PadRightDisplay("报文内容", 8) + " │ " + PadRightDisplay("物理意义与规范解析", 42) + " │");
                    sb.AppendLine("├──────────┼──────────┼────────────────────────────────────────────┤");
                    sb.AppendLine("│ " + PadRightDisplay("SOI", 8) + " │ " + PadRightDisplay("0x7E (~)", 8) + " │ " + PadRightDisplay("起始标志位", 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("VER", 8) + " │ " + PadRightDisplay(ver, 8) + " │ " + PadRightDisplay(string.Format("协议版本号 (V{0})", ver), 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("ADR", 8) + " │ " + PadRightDisplay("0x" + adr, 8) + " │ " + PadRightDisplay("电池包通信地址", 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("CID1", 8) + " │ " + PadRightDisplay("0x" + cid1, 8) + " │ " + PadRightDisplay("设备类型码 (46H=磷酸铁锂电池)", 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("CID2/RTN", 8) + " │ " + PadRightDisplay("0x" + cid2, 8) + " │ " + PadRightDisplay(GetPylonCid2Description(cid2), 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("LENGTH", 8) + " │ " + PadRightDisplay("0x" + lenField, 8) + " │ " + PadRightDisplay(string.Format("INFO 长度字段 ({0} HEX字符)", infoHexLen), 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("CHKSUM", 8) + " │ " + PadRightDisplay("0x" + chkStr, 8) + " │ " + PadRightDisplay(string.Format("帧校验和 ({0})", chkOk ? "合法" : "异常"), 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("EOI", 8) + " │ " + PadRightDisplay("0x0D(\\r)", 8) + " │ " + PadRightDisplay("结束标志位 (CR)", 42) + " │");
                    sb.AppendLine("└──────────┴──────────┴────────────────────────────────────────────┘");
                    sb.AppendLine();
                    sb.AppendLine("[INFO 数据域载荷]:");
                    for (int k = 0; k < infoStr.Length; k += 64)
                    {
                        int chunk = Math.Min(64, infoStr.Length - k);
                        sb.AppendLine("  " + infoStr.Substring(k, chunk));
                    }
                }
                return sb.ToString().TrimEnd('\r', '\n');
            }

            // 2. JBD (RS485/UART V12) 帧解剖 (0xDD 开头)
            if (raw[0] == 0xDD)
            {
                int coreLen, cbLen;
                bool isReq = JbdChecksum.VerifyRequest(raw, 0, raw.Length, out coreLen, out cbLen);
                bool isResp = JbdChecksum.VerifyResponse(raw, 0, raw.Length, out coreLen);
                bool isHostReqType = (raw.Length >= 2 && (raw[1] == 0xA5 || raw[1] == 0x5A));

                sb.AppendLine("[协议帧类型] : JBD (RS485/UART V12) 二进制协议帧");

                if (isHostReqType)
                {
                    byte rw = raw[1];
                    byte cmd = raw.Length >= 3 ? raw[2] : (byte)0;
                    byte dLen = raw.Length >= 4 ? raw[3] : (byte)0;
                    ushort frameChk = (raw.Length >= 4 + dLen + 2) ? (ushort)((raw[4 + dLen] << 8) | raw[4 + dLen + 1]) : (ushort)0;
                    ushort calcChk = (raw.Length >= 4 + dLen) ? JbdChecksum.Calculate(raw, 2, 2 + dLen) : (ushort)0;
                    bool chkOk = isReq || (frameChk == calcChk && raw.Length >= 7 + dLen && raw[4 + dLen + 2] == 0x77);

                    sb.AppendLine(string.Format("[报文属性]   : 主机下发{0}指令帧 (RW=0x{1:X2})", rw == 0xA5 ? "读取" : "写入/控制", rw));
                    sb.AppendLine(string.Format("[校验码状态] : {0} (报文值: 0x{1:X4}, 计算值: 0x{2:X4})",
                        chkOk ? "✓ 校验通过 (PASS)" : "✕ 校验错误 (FAIL)", frameChk, calcChk));
                    sb.AppendLine();

                    sb.AppendLine("┌──────────┬──────────┬────────────────────────────────────────────┐");
                    sb.AppendLine("│ " + PadRightDisplay("字段名称", 8) + " │ " + PadRightDisplay("报文内容", 8) + " │ " + PadRightDisplay("物理意义与规范解析", 42) + " │");
                    sb.AppendLine("├──────────┼──────────┼────────────────────────────────────────────┤");
                    sb.AppendLine("│ " + PadRightDisplay("SOI", 8) + " │ " + PadRightDisplay("0xDD", 8) + " │ " + PadRightDisplay("起始标志位 (Start of Frame)", 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("R/W 标志", 8) + " │ " + PadRightDisplay(string.Format("0x{0:X2}", rw), 8) + " │ " + PadRightDisplay(rw == 0xA5 ? "0xA5 (读数据/状态)" : (rw == 0x5A ? "0x5A (写控制/参数)" : "未知读写标志"), 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("命令码", 8) + " │ " + PadRightDisplay(string.Format("0x{0:X2}", cmd), 8) + " │ " + PadRightDisplay(GetJbdCmdDescription(cmd, rw), 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("数据长度", 8) + " │ " + PadRightDisplay(string.Format("0x{0:X2}({0})", dLen), 8) + " │ " + PadRightDisplay(string.Format("请求载荷长度 ({0} 字节)", dLen), 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("校验和", 8) + " │ " + PadRightDisplay(string.Format("0x{0:X4}", frameChk), 8) + " │ " + PadRightDisplay("CMD+LEN+DATA 累加和按位取反加1", 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("EOI", 8) + " │ " + PadRightDisplay("0x77", 8) + " │ " + PadRightDisplay("结束标志位 (End of Frame)", 42) + " │");
                    int actualCbLen = Math.Max(0, raw.Length - (7 + dLen));
                    if (actualCbLen > 0)
                    {
                        byte[] cbBytes = new byte[actualCbLen];
                        Array.Copy(raw, 7 + dLen, cbBytes, 0, actualCbLen);
                        sb.AppendLine("│ " + PadRightDisplay("CALLBACK", 8) + " │ " + PadRightDisplay(BitConverter.ToString(cbBytes).Replace("-", " "), 8) + " │ " + PadRightDisplay(string.Format("尾随透传 ID ({0} 字节, 从机原样附带回传)", actualCbLen), 42) + " │");
                    }
                    sb.AppendLine("└──────────┴──────────┴────────────────────────────────────────────┘");

                    if (dLen > 0 && raw.Length >= 4 + dLen)
                    {
                        sb.AppendLine();
                        sb.AppendLine(string.Format("[载荷数据 HEX]: {0}", BitConverter.ToString(raw, 4, dLen).Replace("-", " ")));
                    }
                    return sb.ToString().TrimEnd('\r', '\n');
                }
                else
                {
                    byte cmd = raw.Length >= 2 ? raw[1] : (byte)0;
                    byte status = raw.Length >= 3 ? raw[2] : (byte)0;
                    byte dLen = raw.Length >= 4 ? raw[3] : (byte)0;
                    ushort frameChk = (raw.Length >= 4 + dLen + 2) ? (ushort)((raw[4 + dLen] << 8) | raw[4 + dLen + 1]) : (ushort)0;
                    ushort calcChk = (raw.Length >= 4 + dLen) ? JbdChecksum.Calculate(raw, 2, 2 + dLen) : (ushort)0;
                    bool chkOk = isResp || (frameChk == calcChk && raw.Length >= 7 + dLen && raw[4 + dLen + 2] == 0x77);

                    string statusDesc = status == 0x00 ? "0x00 (执行成功 / OK)" :
                                        (status == 0x80 ? "0x80 (错误: 命令码不存在)" :
                                        (status == 0x81 ? "0x81 (错误: 非法操作/未进工厂模式)" :
                                        (status == 0x82 ? "0x82 (错误: 校验码错误)" :
                                        (status == 0x83 ? "0x83 (错误: 密码错误)" :
                                        (status == 0x84 ? "0x84 (错误: 数据越界)" : ("0x" + status.ToString("X2") + " (未知状态码)"))))));

                    sb.AppendLine("[报文属性]   : BMS 从机响应帧 (Slave Response)");
                    sb.AppendLine(string.Format("[校验码状态] : {0} (报文值: 0x{1:X4}, 计算值: 0x{2:X4})",
                        chkOk ? "✓ 校验通过 (PASS)" : "✕ 校验错误 (FAIL)", frameChk, calcChk));
                    sb.AppendLine();

                    sb.AppendLine("┌──────────┬──────────┬────────────────────────────────────────────┐");
                    sb.AppendLine("│ " + PadRightDisplay("字段名称", 8) + " │ " + PadRightDisplay("报文内容", 8) + " │ " + PadRightDisplay("物理意义与规范解析", 42) + " │");
                    sb.AppendLine("├──────────┼──────────┼────────────────────────────────────────────┤");
                    sb.AppendLine("│ " + PadRightDisplay("SOI", 8) + " │ " + PadRightDisplay("0xDD", 8) + " │ " + PadRightDisplay("起始标志位 (Start of Frame)", 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("命令码", 8) + " │ " + PadRightDisplay(string.Format("0x{0:X2}", cmd), 8) + " │ " + PadRightDisplay(GetJbdResponseCmdDescription(cmd, status, dLen), 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("状态码", 8) + " │ " + PadRightDisplay(string.Format("0x{0:X2}", status), 8) + " │ " + PadRightDisplay(statusDesc, 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("数据长度", 8) + " │ " + PadRightDisplay(string.Format("0x{0:X2}({0})", dLen), 8) + " │ " + PadRightDisplay(string.Format("应答数据载荷长度 ({0} 字节)", dLen), 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("校验和", 8) + " │ " + PadRightDisplay(string.Format("0x{0:X4}", frameChk), 8) + " │ " + PadRightDisplay("STATUS+LEN+DATA 累加和按位取反加1", 42) + " │");
                    sb.AppendLine("│ " + PadRightDisplay("EOI", 8) + " │ " + PadRightDisplay("0x77", 8) + " │ " + PadRightDisplay("结束标志位 (End of Frame)", 42) + " │");
                    int actualCbLen = Math.Max(0, raw.Length - (7 + dLen));
                    if (actualCbLen > 0)
                    {
                        byte[] cbBytes = new byte[actualCbLen];
                        Array.Copy(raw, 7 + dLen, cbBytes, 0, actualCbLen);
                        sb.AppendLine("│ " + PadRightDisplay("CALLBACK", 8) + " │ " + PadRightDisplay(BitConverter.ToString(cbBytes).Replace("-", " "), 8) + " │ " + PadRightDisplay(string.Format("尾随回显 ID ({0} 字节, 原样回传上位机)", actualCbLen), 42) + " │");
                    }
                    sb.AppendLine("└──────────┴──────────┴────────────────────────────────────────────┘");

                    if (dLen > 0 && raw.Length >= 4 + dLen)
                    {
                        if (cmd == 0x03 && dLen >= 23)
                        {
                            ushort uVolt = (ushort)((raw[4] << 8) | raw[5]);
                            short sCurr = (short)((raw[6] << 8) | raw[7]);
                            ushort uRemCap = (ushort)((raw[8] << 8) | raw[9]);
                            ushort uFullCap = (ushort)((raw[10] << 8) | raw[11]);
                            ushort uCycles = (ushort)((raw[12] << 8) | raw[13]);
                            ushort prot = (ushort)((raw[20] << 8) | raw[21]);
                            byte rsoc = raw[23];
                            byte fet = raw[24];
                            byte strings = raw[25];
                            byte ntcCount = raw[26];

                            sb.AppendLine();
                            sb.AppendLine("┌────────────────────┬────────────────────────────────────────────────────────────┐");
                            sb.AppendLine("│ " + PadRightDisplay("0x03 状态遥测属性", 18) + " │ " + PadRightDisplay("解析数值与物理量", 58) + " │");
                            sb.AppendLine("├────────────────────┼────────────────────────────────────────────────────────────┤");
                            sb.AppendLine("│ " + PadRightDisplay("电池组总电压", 18) + " │ " + PadRightDisplay(string.Format("{0:F2} V (原始值: {1} × 10mV)", uVolt / 100.0, uVolt), 58) + " │");
                            bool is100MaUnit = (fet & 0x80) != 0;
                            double iScale = is100MaUnit ? 10.0 : 100.0;
                            sb.AppendLine("│ " + PadRightDisplay("充放电实时电流", 18) + " │ " + PadRightDisplay(string.Format("{0:F2} A ({1}, {2:F2}A)", sCurr / iScale, sCurr >= 0 ? "充电" : "放电", Math.Abs(sCurr / iScale)), 58) + " │");
                            sb.AppendLine("│ " + PadRightDisplay("剩余容量/满充容量", 18) + " │ " + PadRightDisplay(string.Format("{0:F2} Ah / {1:F2} Ah", uRemCap / iScale, uFullCap / iScale), 58) + " │");
                            sb.AppendLine("│ " + PadRightDisplay("荷电状态 (SOC)", 18) + " │ " + PadRightDisplay(string.Format("{0}%", rsoc), 58) + " │");
                            sb.AppendLine("│ " + PadRightDisplay("循环次数 (Cycles)", 18) + " │ " + PadRightDisplay(string.Format("{0} 次", uCycles), 58) + " │");
                            sb.AppendLine("│ " + PadRightDisplay("MOS 开关状态(FET)", 18) + " │ " + PadRightDisplay(string.Format("充:{0} | 放:{1} | 限流:{2} | 加热:{3} (0x{4:X2})",
                                (fet & 0x01) != 0 ? "开" : "关", (fet & 0x02) != 0 ? "开" : "关",
                                (fet & 0x04) != 0 ? "开" : "关", (fet & 0x08) != 0 ? "开" : "关", fet), 58) + " │");
                            sb.AppendLine("│ " + PadRightDisplay("电流/容量单位倍率", 18) + " │ " + PadRightDisplay(is100MaUnit ? "0.1A / 0.1Ah 大倍率 (FET Bit7=1)" : "10mA / 10mAh 标准倍率 (FET Bit7=0)", 58) + " │");
                            sb.AppendLine("│ " + PadRightDisplay("电池串数/NTC探头", 18) + " │ " + PadRightDisplay(string.Format("{0} 串 / {1} 个温度探头", strings, ntcCount), 58) + " │");
                            if (dLen >= 23 + ntcCount * 2 && ntcCount > 0)
                            {
                                for (int k = 0; k < ntcCount; k++)
                                {
                                    int ntcIdx = 4 + 23 + k * 2;
                                    if (ntcIdx + 1 < raw.Length)
                                    {
                                        ushort tk = (ushort)((raw[ntcIdx] << 8) | raw[ntcIdx + 1]);
                                        sb.AppendLine("│ " + PadRightDisplay(string.Format("NTC 探头 #{0} 温度", k + 1), 18) + " │ " + PadRightDisplay(string.Format("{0:F1} °C ({1:F1} K)", (tk - 2731) / 10.0, tk / 10.0), 58) + " │");
                                    }
                                }
                            }
                            AppendTwoColRowWrapped(sb, "保护状态字 (Prot)", FormatJbdProtBits(prot), 18, 58);

                            int humOffset = 4 + 23 + ntcCount * 2;
                            if (dLen >= 23 + ntcCount * 2 + 1 && raw.Length >= humOffset + 1)
                            {
                                sb.AppendLine("│ " + PadRightDisplay("环境湿度", 18) + " │ " + PadRightDisplay(string.Format("{0} %", raw[humOffset]), 58) + " │");
                            }
                            if (dLen >= 23 + ntcCount * 2 + 3 && raw.Length >= humOffset + 3)
                            {
                                ushort alarm = (ushort)((raw[humOffset + 1] << 8) | raw[humOffset + 2]);
                                AppendTwoColRowWrapped(sb, "告警状态字 (Alarm)", FormatJbdAlarmBits(alarm), 18, 58);
                            }
                            sb.AppendLine("└────────────────────┴────────────────────────────────────────────────────────────┘");
                        }
                        else if (cmd == 0x04)
                        {
                            int cellCount = dLen / 2;
                            sb.AppendLine();
                            sb.AppendLine("┌──────────┬──────────┬────────────┐");
                            sb.AppendLine("│ " + PadRightDisplay("电芯序号", 8) + " │ " + PadRightDisplay("原始 HEX", 8) + " │ " + PadRightDisplay("电芯电压", 10) + " │");
                            sb.AppendLine("├──────────┼──────────┼────────────┤");
                            for (int k = 0; k < cellCount; k++)
                            {
                                int cIdx = 4 + k * 2;
                                if (cIdx + 1 < raw.Length)
                                {
                                    ushort cMv = (ushort)((raw[cIdx] << 8) | raw[cIdx + 1]);
                                    sb.AppendLine("│ " + PadRightDisplay(string.Format("Cell #{0}", k + 1), 8) + " │ " + PadRightDisplay(string.Format("0x{0:X4}", cMv), 8) + " │ " + PadRightDisplay(string.Format("{0} mV", cMv), 10) + " │");
                                }
                            }
                            sb.AppendLine("└──────────┴──────────┴────────────┘");
                        }
                        else if (cmd == 0x05)
                        {
                            string modelStr = Encoding.ASCII.GetString(raw, 4, dLen).TrimEnd('\0', ' ');
                            sb.AppendLine();
                            sb.AppendLine(string.Format("• 硬件型号 ASCII: {0}", modelStr));
                        }
                    }
                    return sb.ToString().TrimEnd('\r', '\n');
                }
            }

            // 3. Modbus RTU / Voltronic 帧解剖
            bool crcOk = (raw.Length >= 4) && ModbusCrc.Check(raw, raw.Length);
            ushort calcCrc = (raw.Length >= 3) ? ModbusCrc.Calculate(raw, raw.Length - 2) : (ushort)0;
            ushort frameCrc = (raw.Length >= 2) ? (ushort)((raw[raw.Length - 2] << 8) | raw[raw.Length - 1]) : (ushort)0;

            sb.AppendLine("[协议帧类型] : Modbus RTU 二进制帧");
            sb.AppendLine(string.Format("[CRC16 校验] : {0} (报文值: 0x{1:X4}, 计算值: 0x{2:X4})",
                crcOk ? "✓ 校验通过 (PASS)" : "✕ 校验错误 (FAIL)", frameCrc, calcCrc));
            sb.AppendLine();

            if (raw.Length == 8 && (raw[1] == 0x03 || raw[1] == 0x04 || raw[1] == 0x06 || raw[1] == 0x10))
            {
                ushort addr = (ushort)((raw[2] << 8) | raw[3]);
                ushort valOrCount = (ushort)((raw[4] << 8) | raw[5]);
                string fcDesc = raw[1] == 0x03 ? "0x03 (读保持寄存器)" :
                                (raw[1] == 0x04 ? "0x04 (读输入寄存器)" :
                                (raw[1] == 0x06 ? "0x06 (写单个寄存器)" : "0x10 (写多个寄存器)"));
                sb.AppendLine(string.Format("• 从机地址 (Slave ID) : 0x{0:X2} ({0})", raw[0]));
                sb.AppendLine(string.Format("• 功能码   (Function) : {0}", fcDesc));
                sb.AppendLine(string.Format("• 目标地址 (Reg Addr) : 0x{0:X4} (十进制: {0})", addr));
                if (raw[1] == 0x03 || raw[1] == 0x04)
                    sb.AppendLine(string.Format("• 读取数量 (Reg Cnt ) : {0} 个寄存器 ({1} Bytes)", valOrCount, valOrCount * 2));
                else
                    sb.AppendLine(string.Format("• 写入数值 (Reg Val ) : 0x{0:X4} (无符号: {0}, 有符号: {1})", valOrCount, (short)valOrCount));
            }
            else if (raw.Length == 4 && raw[1] == 0x11)
            {
                sb.AppendLine(string.Format("• 从机地址 (Slave ID) : 0x{0:X2} ({0})", raw[0]));
                sb.AppendLine("• 功能码   (Function) : 0x11 (读设备/产品信息)");
                sb.AppendLine("• 请求载荷 : 查询从机产品型号、软硬件版本及出厂序列号");
            }
            else if (raw.Length == 5 && (raw[1] & 0x80) != 0)
            {
                byte origFc = (byte)(raw[1] & 0x7F);
                byte errCode = raw[2];
                string errDesc = errCode == 0x01 ? "非法功能码 (Illegal Function)" :
                                 errCode == 0x02 ? "非法数据地址 (Illegal Data Address)" :
                                 errCode == 0x03 ? "非法数据值 (Illegal Data Value)" :
                                 errCode == 0x04 ? "从机设备故障 (Slave Device Failure)" : ("错误码: 0x" + errCode.ToString("X2"));
                sb.AppendLine(string.Format("• 从机地址 (Slave ID) : 0x{0:X2} ({0})", raw[0]));
                sb.AppendLine(string.Format("• 异常功能码(Exception): 0x{0:X2} (对应请求功能码: 0x{1:X2})", raw[1], origFc));
                sb.AppendLine(string.Format("• 异常代码 (Error Code): 0x{0:X2} - {1}", errCode, errDesc));
            }
            else if (raw.Length >= 5 && raw[1] == 0x11)
            {
                byte byteCount = raw[2];
                sb.AppendLine(string.Format("• 从机地址 (Slave ID) : 0x{0:X2} ({0})", raw[0]));
                sb.AppendLine(string.Format("• 功能码   (Function) : 0x11 (读产品信息应答, 数据长度={0} Bytes)", byteCount));
                int payloadStart = 3;
                int payloadEnd = payloadStart + byteCount;
                if (raw.Length >= payloadEnd + 2)
                {
                    int s1 = -1;
                    int modelScanEnd = Math.Min(payloadEnd, payloadStart + 16);
                    for (int idx = payloadStart; idx < modelScanEnd; idx++)
                    {
                        if (raw[idx] == 0x2A) { s1 = idx; break; }
                    }
                    int s2 = (s1 > payloadStart) ? (s1 + 3) : -1;
                    int s3 = (s2 > 0) ? (s2 + 3) : -1;
                    if (s1 > payloadStart && s3 < payloadEnd && raw[s2] == 0x2A && raw[s3] == 0x2A)
                    {
                        string modelStr = Encoding.ASCII.GetString(raw, payloadStart, s1 - payloadStart).TrimEnd('\0', ' ');
                        byte swMaj = raw[s1 + 1];
                        byte swMin = raw[s1 + 2];
                        byte hwMaj = raw[s2 + 1];
                        byte hwMin = raw[s2 + 2];
                        int snStart = s3 + 1;
                        int s4 = payloadEnd;
                        for (int idx = snStart; idx < payloadEnd; idx++)
                        {
                            if (raw[idx] == 0x2A) { s4 = idx; break; }
                        }
                        int snLen = Math.Max(0, Math.Min(20, s4 - snStart));
                        string snStr = snLen > 0 ? Encoding.ASCII.GetString(raw, snStart, snLen).TrimEnd('\0', ' ') : "";

                        sb.AppendLine();
                        sb.AppendLine("┌────────────────────┬────────────────────────────────────────┐");
                        sb.AppendLine("│ " + PadRightDisplay("产品属性", 18) + " │ " + PadRightDisplay("解析数值", 38) + " │");
                        sb.AppendLine("├────────────────────┼────────────────────────────────────────┤");
                        sb.AppendLine("│ " + PadRightDisplay("产品型号 (Model)", 18) + " │ " + PadRightDisplay(modelStr, 38) + " │");
                        sb.AppendLine("│ " + PadRightDisplay("固件版本 (SW Ver)", 18) + " │ " + PadRightDisplay(string.Format("V{0:X}.{1:X2}", swMaj, swMin), 38) + " │");
                        sb.AppendLine("│ " + PadRightDisplay("硬件版本 (HW Ver)", 18) + " │ " + PadRightDisplay(string.Format("V{0:X}.{1:X2}", hwMaj, hwMin), 38) + " │");
                        sb.AppendLine("│ " + PadRightDisplay("序列号   (SN)", 18) + " │ " + PadRightDisplay(snStr, 38) + " │");
                        sb.AppendLine("└────────────────────┴────────────────────────────────────────┘");
                    }
                }
            }
            else if (raw.Length >= 5 && (raw[1] == 0x03 || raw[1] == 0x04))
            {
                // 判断是否为 Voltronic 4字节帧头 ([ID][03][LenHi][LenLo]) 或 标准 Modbus 3字节帧头 ([ID][03/04][ByteCount])
                int dataStart = 3;
                int regCount = raw[2] / 2;
                bool isVoltronicHeader = false;
                if (raw[1] == 0x03 && raw.Length >= 6)
                {
                    int vRegCount = (raw[2] << 8) | raw[3];
                    if (4 + vRegCount * 2 + 2 == raw.Length && (3 + raw[2] + 2 != raw.Length))
                    {
                        isVoltronicHeader = true;
                        dataStart = 4;
                        regCount = vRegCount;
                    }
                }

                ushort inferredStartReg;
                bool hasStartReg = TryInferModbusStartAddress(summaryPart, resolvedModbusProto, raw[1], regCount, isVoltronicHeader, out inferredStartReg);

                sb.AppendLine(string.Format("• 从机地址 (Slave ID) : 0x{0:X2} ({0})", raw[0]));
                sb.AppendLine(string.Format("• 功能码   (Function) : 0x{0:X2} ({1})", raw[1], raw[1] == 0x04 ? "读输入寄存器应答" : "读保持寄存器应答"));
                sb.AppendLine(string.Format("• 帧头格式 (Header  ) : {0} (共 {1} 个 16-bit 寄存器字)",
                    isVoltronicHeader ? "Voltronic 双字节长度头 [ID][03][RegHi][RegLo]" : ("标准 Modbus 单字节长度头 [ID][0" + raw[1].ToString("X1") + "][ByteCnt]"), regCount));
                sb.AppendLine();

                if (hasStartReg)
                {
                    sb.AppendLine("┌──────────┬──────────┬──────────┬──────────┬────────────────┬──────────────────────────────────────┐");
                    sb.AppendLine("│ " + PadRightDisplay("寄存器序", 8) + " │ " + PadRightDisplay("寄存器址", 8) + " │ " + PadRightDisplay("十六进制", 8) + " │ " + PadRightDisplay("无符号值", 8) + " │ " + PadRightDisplay("常见缩放参考", 14) + " │ " + PadRightDisplay("协议字段与物理量解析", 36) + " │");
                    sb.AppendLine("├──────────┼──────────┼──────────┼──────────┼────────────────┼──────────────────────────────────────┤");
                    for (int i = 0; i < regCount; i++)
                    {
                        int idx = dataStart + i * 2;
                        if (idx + 1 >= raw.Length - 2) break;
                        ushort uVal = (ushort)((raw[idx] << 8) | raw[idx + 1]);
                        short sVal = (short)uVal;
                        ushort regAddr = (ushort)(inferredStartReg + i);
                        string scaleCol = PadLeftDisplay(string.Format("{0:F2}", sVal / 100.0), 7) + " (÷100)";
                        string semantic = GetModbusRegisterSemantic(resolvedModbusProto, raw[1], regAddr, uVal, sVal);
                        sb.AppendLine("│ " + PadRightDisplay(string.Format("Word #{0}", i), 8)
                            + " │ " + PadRightDisplay(string.Format("0x{0:X4}", regAddr), 8)
                            + " │ " + PadRightDisplay(string.Format("0x{0:X4}", uVal), 8)
                            + " │ " + PadRightDisplay(uVal.ToString(), 8)
                            + " │ " + PadRightDisplay(scaleCol, 14)
                            + " │ " + PadRightDisplay(semantic, 36) + " │");
                    }
                    sb.AppendLine("└──────────┴──────────┴──────────┴──────────┴────────────────┴──────────────────────────────────────┘");
                }
                else
                {
                    sb.AppendLine("┌──────────┬──────────┬────────────┬────────────┬────────────────┐");
                    sb.AppendLine("│ " + PadRightDisplay("寄存器序", 8) + " │ " + PadRightDisplay("十六进制", 8) + " │ " + PadRightDisplay("无符号十进", 10) + " │ " + PadRightDisplay("有符号十进", 10) + " │ " + PadRightDisplay("常见缩放参考", 14) + " │");
                    sb.AppendLine("├──────────┼──────────┼────────────┼────────────┼────────────────┤");
                    for (int i = 0; i < regCount; i++)
                    {
                        int idx = dataStart + i * 2;
                        if (idx + 1 >= raw.Length - 2) break;
                        ushort uVal = (ushort)((raw[idx] << 8) | raw[idx + 1]);
                        short sVal = (short)uVal;
                        string scaleCol = PadLeftDisplay(string.Format("{0:F2}", sVal / 100.0), 7) + " (÷100)";
                        sb.AppendLine("│ " + PadRightDisplay(string.Format("Word #{0}", i), 8)
                            + " │ " + PadRightDisplay(string.Format("0x{0:X4}", uVal), 8)
                            + " │ " + PadRightDisplay(uVal.ToString(), 10)
                            + " │ " + PadRightDisplay(sVal.ToString(), 10)
                            + " │ " + PadRightDisplay(scaleCol, 14) + " │");
                    }
                    sb.AppendLine("└──────────┴──────────┴────────────┴────────────┴────────────────┘");
                }
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        public static int GetCharDisplayWidth(char c)
        {
            if (c <= 0x00FF) return 1;
            if (c >= 0x2500 && c <= 0x257F) return 1;
            if (c == '✓' || c == '✕' || c == '•') return 1;
            if (c >= 0x2E80 && c <= 0x9FFF) return 2;
            if (c >= 0xF900 && c <= 0xFAFF) return 2;
            if (c >= 0xFE30 && c <= 0xFE6F) return 2;
            if (c >= 0xFF01 && c <= 0xFF60) return 2;
            return 1;
        }

        public static int GetDisplayWidth(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int w = 0;
            for (int i = 0; i < s.Length; i++) w += GetCharDisplayWidth(s[i]);
            return w;
        }

        private static string PadRightDisplay(string s, int targetWidth)
        {
            if (s == null) s = "";
            int w = GetDisplayWidth(s);
            if (w > targetWidth)
            {
                StringBuilder cut = new StringBuilder();
                int cw = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    int charW = GetCharDisplayWidth(s[i]);
                    if (cw + charW > targetWidth) break;
                    cut.Append(s[i]);
                    cw += charW;
                }
                return cut.ToString() + new string(' ', targetWidth - cw);
            }
            return s + new string(' ', targetWidth - w);
        }

        private static string PadLeftDisplay(string s, int targetWidth)
        {
            if (s == null) s = "";
            int w = GetDisplayWidth(s);
            if (w >= targetWidth) return s;
            return new string(' ', targetWidth - w) + s;
        }

        private static void AppendFormattedHexBlock(StringBuilder sb, string prefix, byte[] raw)
        {
            if (raw == null || raw.Length == 0) return;
            if (raw.Length <= 16)
            {
                sb.AppendLine(prefix + BitConverter.ToString(raw).Replace("-", " "));
                return;
            }

            string indent = new string(' ', GetDisplayWidth(prefix));
            for (int offset = 0; offset < raw.Length; offset += 16)
            {
                int count = Math.Min(16, raw.Length - offset);
                StringBuilder lineHex = new StringBuilder();
                lineHex.AppendFormat("[{0:D2}..{1:D2}] ", offset, offset + count - 1);
                for (int i = 0; i < count; i++)
                {
                    if (i == 8) lineHex.Append(' ');
                    lineHex.AppendFormat("{0:X2}", raw[offset + i]);
                    if (i < count - 1) lineHex.Append(' ');
                }
                sb.AppendLine((offset == 0 ? prefix : indent) + lineHex.ToString());
            }
        }

        private static void AppendWrappedSummary(StringBuilder sb, string prefix, string content, int maxContentCols)
        {
            if (string.IsNullOrEmpty(content)) return;
            if (GetDisplayWidth(content) <= maxContentCols)
            {
                sb.AppendLine(prefix + content);
                return;
            }

            string indent = new string(' ', GetDisplayWidth(prefix));
            int pos = 0;
            bool firstLine = true;
            while (pos < content.Length)
            {
                int cols = 0;
                int end = pos;
                int lastBreak = -1;
                while (end < content.Length)
                {
                    int cw = GetCharDisplayWidth(content[end]);
                    if (cols + cw > maxContentCols) break;
                    cols += cw;
                    if (content[end] == ',' || content[end] == ' ' || content[end] == '|')
                        lastBreak = end + 1;
                    end++;
                }

                int cutEnd = (end < content.Length && lastBreak > pos + 10) ? lastBreak : end;
                string chunk = content.Substring(pos, cutEnd - pos).Trim();
                sb.AppendLine((firstLine ? prefix : indent) + chunk);
                firstLine = false;
                pos = cutEnd;
                while (pos < content.Length && content[pos] == ' ') pos++;
            }
        }

        private static bool TryInferModbusStartAddress(string summaryPart, string protocolName, byte funcCode, int regCount, bool isVoltronicHeader, out ushort startAddr)
        {
            startAddr = 0;
            if (!string.IsNullOrEmpty(summaryPart))
            {
                int oxIdx = summaryPart.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
                while (oxIdx >= 0 && oxIdx + 6 <= summaryPart.Length)
                {
                    string hexCandidate = summaryPart.Substring(oxIdx + 2, 4);
                    ushort parsed;
                    if (ushort.TryParse(hexCandidate, System.Globalization.NumberStyles.HexNumber, null, out parsed))
                    {
                        startAddr = parsed;
                        return true;
                    }
                    oxIdx = summaryPart.IndexOf("0x", oxIdx + 2, StringComparison.OrdinalIgnoreCase);
                }
            }

            string prot = (protocolName ?? "").ToUpperInvariant();
            if (funcCode == 0x04 || prot.Contains("LIB_MODBUS") || prot.Contains("SHENGYANG"))
            {
                startAddr = 0x1000;
                return true;
            }
            if (isVoltronicHeader || prot.Contains("VOLTRONIC"))
            {
                if (regCount == 6) { startAddr = 0x0030; return true; }
                if (regCount == 10) { startAddr = 0x0060; return true; }
                if (regCount == 5 || regCount == 3) { startAddr = 0x0070; return true; }
            }
            if (prot.Contains("CVTE"))
            {
                if (regCount == 13 || regCount == 12) { startAddr = 0x0020; return true; }
            }
            if (prot.Contains("GROWATT"))
            {
                if (regCount == 17) { startAddr = 0x0013; return true; }
                if (regCount == 15) { startAddr = 0x0014; return true; }
                if (regCount == 35) { startAddr = 0x0001; return true; }
            }
            return false;
        }

        private static string GetModbusRegisterSemantic(string protocolName, byte funcCode, ushort regAddr, ushort uVal, short sVal)
        {
            string prot = (protocolName ?? "").ToUpperInvariant();
            if (funcCode == 0x04 || regAddr >= 0x1000 || prot.Contains("LIB_MODBUS") || prot.Contains("SHENGYANG"))
            {
                bool isInvalid = (uVal == 0xFFFF);
                switch (regAddr)
                {
                    case 0x1000: return isInvalid ? "电池总电压: 无效值 (0xFFFF)" : string.Format("电池总电压: {0:F2} V", uVal / 100.0);
                    case 0x1001: return isInvalid ? "充放电电流: 无效值 (0xFFFF)" : string.Format("充放电电流: {0:F2} A", sVal / 100.0);
                    case 0x1002: return isInvalid ? "剩余容量: 无效值 (0xFFFF)" : string.Format("剩余容量: {0:F2} Ah", uVal / 100.0);
                    case 0x1003: return isInvalid ? "电芯平均温度: 无效值 (0xFFFF)" : string.Format("电芯平均温度: {0:F1} °C", sVal / 10.0);
                    case 0x1004: return isInvalid ? "环境温度: 无效值 (0xFFFF)" : string.Format("环境温度: {0:F1} °C", sVal / 10.0);
                    case 0x1005: return string.Format("告警标志字: 0x{0:X4}", uVal);
                    case 0x1006: return string.Format("保护标志字: 0x{0:X4}", uVal);
                    case 0x1007: return string.Format("故障与状态字: 0x{0:X4}", uVal);
                    case 0x1008: return isInvalid ? "电池 SOC: 无效值 (0xFFFF)" : string.Format("电池 SOC: {0:F1} %", uVal / 10.0);
                    case 0x1009: return isInvalid ? "健康度 SOH: 无效值 (0xFFFF)" : string.Format("健康度 SOH: {0:F1} %", uVal / 10.0);
                    case 0x100A: return isInvalid ? "满充容量: 无效值 (0xFFFF)" : string.Format("满充容量: {0:F2} Ah", uVal / 100.0);
                    case 0x100B: return isInvalid ? "循环次数: 无效值 (0xFFFF)" : string.Format("循环次数: {0} 次", uVal);
                    case 0x100C: return isInvalid ? "最大充电电流: 无效值 (0xFFFF)" : string.Format("最大充电电流: {0:F2} A", uVal / 100.0);
                    case 0x100D: return isInvalid ? "最高单体电压: 无效值 (0xFFFF)" : string.Format("最高单体电压: {0} mV", uVal);
                    case 0x100E: return isInvalid ? "最低单体电压: 无效值 (0xFFFF)" : string.Format("最低单体电压: {0} mV", uVal);
                    case 0x100F: return "预留寄存器 (Reserved)";
                    case 0x1010: return isInvalid ? "最高单体温度: 无效值 (0xFFFF)" : string.Format("最高单体温度: {0:F1} °C", sVal / 10.0);
                    case 0x1011: return isInvalid ? "最低单体温度: 无效值 (0xFFFF)" : string.Format("最低单体温度: {0:F1} °C", sVal / 10.0);
                    case 0x1012: return isInvalid ? "FET(MOS)温度: 无效值 (0xFFFF)" : string.Format("FET(MOS)温度: {0:F1} °C", sVal / 10.0);
                    case 0x1013: return "预留寄存器 (Reserved)";
                    case 0x1014: return isInvalid ? "CV 恒压点: 无效值 (0xFFFF)" : string.Format("CV 恒压点: {0:F2} V", uVal / 100.0);
                    case 0x1015: return isInvalid ? "设计容量: 无效值 (0xFFFF)" : string.Format("设计容量: {0:F2} Ah", uVal / 100.0);
                    case 0x1016: return "预留寄存器 (Reserved)";
                }
            }
            if (prot.Contains("VOLTRONIC"))
            {
                switch (regAddr)
                {
                    case 0x0030: return string.Format("充电电流: {0:F1} A", uVal / 10.0);
                    case 0x0031: return string.Format("放电电流: {0:F1} A", uVal / 10.0);
                    case 0x0032: return string.Format("电池总压: {0:F1} V", uVal / 10.0);
                    case 0x0033: return string.Format("电池 SOC: {0} %", uVal);
                    case 0x0034: return "总容量高字 (mAh Hi)";
                    case 0x0035: return "总容量低字 (mAh Lo)";
                    case 0x0070: return string.Format("CV 充电限压: {0:F1} V", uVal / 10.0);
                    case 0x0071: return string.Format("放电截止电压: {0:F1} V", uVal / 10.0);
                    case 0x0072: return string.Format("最大充电电流: {0:F1} A", uVal / 10.0);
                    case 0x0073: return string.Format("最大放电电流: {0:F1} A", uVal / 10.0);
                    case 0x0074: return string.Format("充放电状态字: 0x{0:X4}", uVal);
                }
                if (regAddr >= 0x0060 && regAddr <= 0x0069)
                    return uVal == 0 ? "状态正常 (00H)" : string.Format("告警/保护触发 (0x{0:X2})", uVal);
                if (regAddr >= 0x0026 && regAddr <= 0x002F)
                    return string.Format("温度探头: {0:F1} °C", (uVal / 10.0) - 273.15);
            }
            if (prot.Contains("GROWATT"))
            {
                switch (regAddr)
                {
                    case 0x0013: return string.Format("系统状态字: 0x{0:X4}", uVal);
                    case 0x0014: return string.Format("故障保护字: 0x{0:X4}", uVal);
                    case 0x0015: return string.Format("电池 SOC: {0} %", uVal);
                    case 0x0016: return string.Format("电池总电压: {0:F2} V", uVal / 100.0);
                    case 0x0017: return string.Format("充放电电流: {0:F2} A", sVal / 100.0);
                    case 0x0018: return string.Format("电池温度: {0} °C", sVal);
                    case 0x0019: return string.Format("最大充电电流: {0:F2} A", uVal / 100.0);
                    case 0x001A: return string.Format("剩余容量: {0:F2} Ah", uVal / 100.0);
                    case 0x001B: return string.Format("满充容量: {0:F2} Ah", uVal / 100.0);
                    case 0x001C: return string.Format("软硬件版本: 0x{0:X4}", uVal);
                    case 0x001D: return string.Format("单体压差: {0} mV", uVal);
                    case 0x001E: return string.Format("循环次数: {0} 次", uVal);
                    case 0x0020: return string.Format("健康度 SOH: {0} %", uVal & 0x7F);
                    case 0x0021: return string.Format("CV 恒压点: {0:F2} V", uVal / 100.0);
                    case 0x0022: return string.Format("警告状态字: 0x{0:X4}", uVal);
                    case 0x0023: return string.Format("最大放电电流: {0:F2} A", uVal / 100.0);
                    case 0x0025: return string.Format("最高单体电压: {0} mV", uVal);
                    case 0x0026: return string.Format("最低单体电压: {0} mV", uVal);
                }
                if (regAddr >= 0x0071 && regAddr <= 0x0080)
                    return string.Format("电芯 #{0} 电压: {1} mV", regAddr - 0x0070, uVal);
            }
            if (prot.Contains("CVTE"))
            {
                switch (regAddr)
                {
                    case 0x0020: return string.Format("运行状态字: 0x{0:X4}", uVal);
                    case 0x0021: return string.Format("电池总电压: {0:F2} V", uVal / 100.0);
                    case 0x0022: return string.Format("充放电电流: {0:F2} A", sVal / 100.0);
                    case 0x0023: return string.Format("最高温度: {0} °C", sVal);
                    case 0x0024: return string.Format("电池 SOC: {0} %", uVal);
                    case 0x0025: return string.Format("剩余容量: {0:F2} Ah", uVal / 100.0);
                    case 0x0026: return string.Format("满充容量: {0:F2} Ah", uVal / 100.0);
                    case 0x0027: return string.Format("故障保护字: 0x{0:X4}", uVal);
                    case 0x0028: return string.Format("警告状态字: 0x{0:X4}", uVal);
                    case 0x0029: return string.Format("最大充电电流: {0:F2} A", uVal / 100.0);
                    case 0x002A: return string.Format("CV 恒压点: {0:F2} V", uVal / 100.0);
                    case 0x002B: return string.Format("健康度 SOH: {0} %", uVal);
                    case 0x002C: return string.Format("循环次数: {0} 次", uVal);
                    case 0x002E: return string.Format("最高单体电压: {0} mV", uVal);
                    case 0x0030: return string.Format("最低单体电压: {0} mV", uVal);
                    case 0x0032: return string.Format("单体压差: {0} mV", uVal);
                }
                if (regAddr >= 0x0041 && regAddr <= 0x005A)
                    return string.Format("电芯 #{0} 电压: {1} mV", regAddr - 0x0040, uVal);
            }
            return string.Format("有符号: {0}", sVal);
        }

        private static void AppendTwoColRowWrapped(StringBuilder sb, string label, string content, int leftWidth, int rightWidth)
        {
            if (string.IsNullOrEmpty(content))
            {
                sb.AppendLine("│ " + PadRightDisplay(label, leftWidth) + " │ " + PadRightDisplay("", rightWidth) + " │");
                return;
            }
            if (GetDisplayWidth(content) <= rightWidth)
            {
                sb.AppendLine("│ " + PadRightDisplay(label, leftWidth) + " │ " + PadRightDisplay(content, rightWidth) + " │");
                return;
            }

            int pos = 0;
            bool firstLine = true;
            while (pos < content.Length)
            {
                int cols = 0;
                int end = pos;
                int lastBreak = -1;
                while (end < content.Length)
                {
                    int cw = GetCharDisplayWidth(content[end]);
                    if (cols + cw > rightWidth) break;
                    cols += cw;
                    if (content[end] == '|' || content[end] == ',' || content[end] == ' ')
                        lastBreak = end + 1;
                    end++;
                }

                int cutEnd = (end < content.Length && lastBreak > pos + 4) ? lastBreak : Math.Max(pos + 1, end);
                string chunk = content.Substring(pos, cutEnd - pos).Trim();
                sb.AppendLine("│ " + PadRightDisplay(firstLine ? label : "", leftWidth) + " │ " + PadRightDisplay(chunk, rightWidth) + " │");
                firstLine = false;
                pos = cutEnd;
                while (pos < content.Length && content[pos] == ' ') pos++;
            }
        }

        private static string FormatJbdProtBits(ushort prot)
        {
            if (prot == 0) return "0x0000 (无保护触发)";
            System.Collections.Generic.List<string> bits = new System.Collections.Generic.List<string>();
            if ((prot & (1 << 0)) != 0) bits.Add("单体OV(Bit0)");
            if ((prot & (1 << 1)) != 0) bits.Add("单体UV(Bit1)");
            if ((prot & (1 << 2)) != 0) bits.Add("整组OV(Bit2)");
            if ((prot & (1 << 3)) != 0) bits.Add("整组UV(Bit3)");
            if ((prot & (1 << 4)) != 0) bits.Add("充HT(Bit4)");
            if ((prot & (1 << 5)) != 0) bits.Add("充LT(Bit5)");
            if ((prot & (1 << 6)) != 0) bits.Add("放HT(Bit6)");
            if ((prot & (1 << 7)) != 0) bits.Add("放LT(Bit7)");
            if ((prot & (1 << 8)) != 0) bits.Add("充OC(Bit8)");
            if ((prot & (1 << 9)) != 0) bits.Add("放OC(Bit9)");
            if ((prot & (1 << 10)) != 0) bits.Add("短路(Bit10)");
            if ((prot & (1 << 11)) != 0) bits.Add("前端IC错(Bit11)");
            if ((prot & (1 << 12)) != 0) bits.Add("软件锁MOS(Bit12)");
            if ((prot & (1 << 13)) != 0) bits.Add("充MOS击穿(Bit13)");
            if ((prot & (1 << 14)) != 0) bits.Add("放MOS击穿(Bit14)");
            if ((prot & (1 << 15)) != 0) bits.Add("保留(Bit15)");
            return string.Format("0x{0:X4} ({1})", prot, string.Join("|", bits.ToArray()));
        }

        private static string FormatJbdAlarmBits(ushort alarm)
        {
            if (alarm == 0) return "0x0000 (无告警)";
            System.Collections.Generic.List<string> bits = new System.Collections.Generic.List<string>();
            if ((alarm & (1 << 0)) != 0) bits.Add("单体OV(Bit0)");
            if ((alarm & (1 << 1)) != 0) bits.Add("单体UV(Bit1)");
            if ((alarm & (1 << 2)) != 0) bits.Add("整组OV(Bit2)");
            if ((alarm & (1 << 3)) != 0) bits.Add("整组UV(Bit3)");
            if ((alarm & (1 << 4)) != 0) bits.Add("充HT(Bit4)");
            if ((alarm & (1 << 5)) != 0) bits.Add("充LT(Bit5)");
            if ((alarm & (1 << 6)) != 0) bits.Add("放HT(Bit6)");
            if ((alarm & (1 << 7)) != 0) bits.Add("放LT(Bit7)");
            if ((alarm & (1 << 8)) != 0) bits.Add("充OC(Bit8)");
            if ((alarm & (1 << 9)) != 0) bits.Add("放OC(Bit9)");
            if ((alarm & (1 << 10)) != 0) bits.Add("压差大(Bit10)");
            if ((alarm & (1 << 11)) != 0) bits.Add("低电量(Bit11)");
            return string.Format("0x{0:X4} ({1})", alarm, string.Join("|", bits.ToArray()));
        }

        private static string GetJbdCmdDescription(byte cmd, byte rw)
        {
            switch (cmd)
            {
                case 0x00: return rw == 0x5A ? "0x00 (进入工厂模式)" : "0x00 (读芯片方案)";
                case 0x01: return "0x01 (退出工厂模式)";
                case 0x03: return "0x03 (读基本信息与状态)";
                case 0x04: return "0x04 (读电芯单体电压)";
                case 0x05: return "0x05 (读硬件版本/型号)";
                case 0x06: return "0x06 (蓝牙/休眠控制)";
                case 0x07: return "0x07 (蓝牙密码校验)";
                case 0x08: return "0x08 (重置蓝牙密码)";
                case 0x0A: return "0x0A (控制指令: 关机/清告警/休眠)";
                case 0x0B: return "0x0B (修改工厂密码)";
                case 0xAA: return "0xAA (读保护历史统计)";
                case 0xF6: return rw == 0x5A ? "0xF6 (写电芯内阻)" : "0xF6 (读电芯内阻)";
                case 0xFA: return rw == 0x5A ? "0xFA (写配置参数)" : "0xFA (读配置参数)";
                case 0xFB: return "0xFB (控制 MOS 充放电开关)";
                case 0xFC: return "0xFC (控制加热模块)";
                default: return string.Format("0x{0:X2} (自定义/扩展指令)", cmd);
            }
        }

        private static string GetJbdResponseCmdDescription(byte cmd, byte status, byte dLen)
        {
            bool errNoData = (status != 0x00 && dLen == 0);
            switch (cmd)
            {
                case 0x00:
                    if (errNoData) return "0x00 (工厂模式/芯片方案异常响应)";
                    return dLen >= 2 ? "0x00 (读芯片方案响应)" : "0x00 (进入工厂模式响应)";
                case 0x01: return "0x01 (退出工厂模式响应)";
                case 0x03: return "0x03 (读基本信息与状态)";
                case 0x04: return "0x04 (读电芯单体电压)";
                case 0x05: return "0x05 (读硬件版本/型号)";
                case 0x06: return "0x06 (蓝牙/休眠控制响应)";
                case 0x07: return "0x07 (蓝牙密码校验响应)";
                case 0x08: return "0x08 (重置蓝牙密码响应)";
                case 0x0A: return "0x0A (控制指令响应: 复位/清告警/休眠)";
                case 0x0B: return "0x0B (修改工厂密码响应)";
                case 0xAA: return "0xAA (读保护历史统计)";
                case 0xF6:
                    if (errNoData) return "0xF6 (电芯内阻读/写异常响应)";
                    return dLen > 0 ? "0xF6 (读电芯内阻响应)" : "0xF6 (写电芯内阻响应)";
                case 0xFA:
                    if (errNoData) return "0xFA (配置参数读/写异常响应)";
                    return dLen > 3 ? "0xFA (读配置参数响应)" : "0xFA (写配置参数响应)";
                case 0xFB: return "0xFB (控制 MOS 开关响应)";
                case 0xFC: return "0xFC (控制加热模块响应)";
                default: return string.Format("0x{0:X2} (自定义/扩展指令响应)", cmd);
            }
        }

        private static string GetPylonCid2Description(string cid2)
        {
            switch (cid2.ToUpper())
            {
                case "00": return "RTN=00H (正常应答无错误)";
                case "60": return "CID2=60H (读电池系统基本版本与条码)";
                case "61": return "CID2=61H (读电池系统运行模拟量)";
                case "62": return "CID2=62H (读电池系统告警与保护状态)";
                case "63": return "CID2=63H (读电池系统充放电管理参数)";
                case "64": return "CID2=64H (控制电池系统关机指令)";
                case "42": return "CID2=42H (读单包模拟量与单芯电压)";
                case "44": return "CID2=44H (读单包告警与保护状态)";
                case "47": return "CID2=47H (读单包系统配置参数)";
                case "92": return "CID2=92H (读单包充放电管理参数)";
                default: return "CID2/RTN=" + cid2 + "H";
            }
        }
    }

    #endregion
}

