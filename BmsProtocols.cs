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

        public static ushort Calculate(byte[] data, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                byte b = data[i];
                crc = (ushort)(Table[(b ^ crc) & 0x0F] ^ (crc >> 4));
                crc = (ushort)(Table[((b >> 4) ^ crc) & 0x0F] ^ (crc >> 4));
            }
            return (ushort)(((crc & 0xFF) << 8) | ((crc >> 8) & 0xFF));
        }

        public static bool Check(byte[] data, int length)
        {
            if (length < 4) return false;
            ushort expectedCrc = (ushort)((data[length - 2] << 8) | data[length - 1]);
            ushort calcCrc = Calculate(data, length - 2);
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
            if (fullFrame.Length < 10) return false;
            if (fullFrame[0] != (byte)'~' || fullFrame[fullFrame.Length - 1] != 0x0D) return false;

            string recvHex = Encoding.ASCII.GetString(fullFrame, fullFrame.Length - 5, 4);
            ushort recvChk;
            if (!ushort.TryParse(recvHex, System.Globalization.NumberStyles.HexNumber, null, out recvChk))
                return false;

            ushort calcChk = Calculate(fullFrame, 1, fullFrame.Length - 6);
            return recvChk == calcChk;
        }
    }

    public interface IBmsProtocolHandler
    {
        string ProtocolName { get; }
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
            }

            ushort crc = ModbusCrc.Calculate(resp, 3 + byteCount);
            resp[3 + byteCount] = (byte)(crc >> 8);
            resp[3 + byteCount + 1] = (byte)(crc & 0xFF);

            txResponse = resp;
            decodedInfo = string.Format("[CVTE] 应答 ID:{0:X2} 读 0x{1:X4} ({2}寄存器) -> 电压:{3:F2}V, 电流:{4:F2}A, SOC:{5:F0}%, 容量:{6:F1}/{7:F1}Ah, CV:{8:F2}V, 限流:{9:F1}A",
                id, regStart, regCount, model.Voltage, model.Current, model.SOC, model.RemainingCapacity, model.FullCapacity, model.CVVoltage, model.MaxChargeCurrent);
            return true;
        }

        private ushort GetCvteRegisterValue(ushort regAddress, BmsDataModel model)
        {
            switch (regAddress)
            {
                case 0x0011: return 0x0103; // MCU 版本 V1.3
                case 0x0012: return 0x0001; // 第一代
                case 0x0013: return 0x0000; // 电池类型
                case 0x001C: return 0x4356; // 'C','V'
                case 0x001D: return 0x5445; // 'T','E'
                case 0x001E: return 0x5F43; // '_','C'
                case 0x001F: return 0x4F4D; // 'O','M' -> "CVTE_COM" (PDF V1.4专用协议标识符)

                case 0x0020: // 状态信息 (PDF Page 8)
                    ushort mode = 0;
                    // Bit 0~1: 状态 (00:软启动, 01:待机, 10:充电, 11:放电)
                    if (model.Current > 0.05) mode |= 0x0002;      // 10: 充电
                    else if (model.Current < -0.05) mode |= 0x0003; // 11: 放电
                    else mode |= 0x0001;                            // 01: 待机

                    if (model.ProtOverVolt || model.ProtUnderVolt || model.ProtOverCurrent ||
                        model.ProtShortCircuit || model.ProtHighTemp || model.ProtUnderTemp ||
                        model.ProtSystemFault || model.ProtSoftStart)
                    {
                        mode |= (1 << 2); // Bit 2: 错误位有效标志
                    }
                    if (model.StatusBalancing) mode |= (1 << 3);      // Bit 3: 电芯均衡中
                    if (model.StatusSleep) mode |= (1 << 4);          // Bit 4: 休眠状态
                    if (model.StatusDischargeEnable) mode |= (1 << 5);// Bit 5: 放电输出开启
                    if (model.StatusChargeEnable) mode |= (1 << 6);   // Bit 6: 充电输入开启
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
                    if (model.WarnVoltDiff) err |= 0x0040;     // Bit 6: 压差保护
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
                    return warn;

                case 0x0029: // 最大充电电流 (10mA, PDF Page 7)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.MaxChargeCurrent * 100)));

                case 0x002A: // CV 恒压充电点 (10mV, PDF Page 7)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.CVVoltage * 100)));

                case 0x002B: // SOH (1%, PDF Page 7)
                    return (ushort)Math.Max(0, Math.Min(100, (int)Math.Round(model.SOH)));

                case 0x002C: // 循环次数 (PDF Page 7)
                    return 50;

                case 0x002D: // 充电剩余时间 (min, PDF Page 7)
                    return (model.Current > 0.1 && model.MaxChargeCurrent > 0) ? (ushort)60 : (ushort)0;

                case 0x002E: // 单体最高电压 (mV, PDF Page 7)
                    return (ushort)Math.Round((model.Voltage / 8.0) * 1000 + 10);

                case 0x002F: // 单体最高电压位号
                    return 1;

                case 0x0030: // 单体最低电压 (mV, PDF Page 7)
                    return (ushort)Math.Round((model.Voltage / 8.0) * 1000 - 10);

                case 0x0031: // 单体最低电压位号
                    return 8;

                case 0x0032: // 电芯压差 (mV, PDF Page 8)
                    return 20;

                case 0x0033: // 单芯最高温度 (℃, PDF Page 8)
                    return (ushort)Math.Round(model.Temperature);

                case 0x0034: // 单芯最高温度位号
                    return 1;

                case 0x0035: // 单芯最低温度 (℃, PDF Page 8)
                    return (ushort)Math.Round(model.Temperature - 1);

                case 0x0036: // 单芯最低温度位号
                    return 2;

                case 0x0040: // 电池串数 (PDF Page 10)
                    return 8;

                default:
                    // 默认按 8 串电芯电压填充 0x0041~0x0048
                    if (regAddress >= 0x0041 && regAddress <= 0x0048)
                    {
                        return (ushort)Math.Round((model.Voltage / 8.0) * 1000);
                    }
                    // 默认多 NTC 温度填充 0x0065~0x0068
                    if (regAddress >= 0x0065 && regAddress <= 0x0068)
                    {
                        return (ushort)Math.Round(model.Temperature);
                    }
                    return 0x0000;
            }
        }

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            byte[] frame = new byte[8];
            frame[0] = targetId;
            frame[1] = 0x03;
            frame[2] = 0x00; frame[3] = 0x20; // 0x0020
            frame[4] = 0x00; frame[5] = 0x0C; // 12 寄存器 (0x0020~0x002B)
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
            if (rxBuffer[1] != 0x03) return false;
            byte byteCount = rxBuffer[2];
            if (rxLength != 3 + byteCount + 2) return false;

            lock (model.SyncRoot)
            {
                if (byteCount >= 24)
                {
                    model.Voltage = ((rxBuffer[5] << 8) | rxBuffer[6]) / 100.0;
                    model.Current = (short)((rxBuffer[7] << 8) | rxBuffer[8]) / 100.0;
                    model.Temperature = (short)((rxBuffer[9] << 8) | rxBuffer[10]);
                    model.SOC = ((rxBuffer[11] << 8) | rxBuffer[12]);
                    model.RemainingCapacity = ((rxBuffer[13] << 8) | rxBuffer[14]) / 100.0;
                    model.FullCapacity = ((rxBuffer[15] << 8) | rxBuffer[16]) / 100.0;
                    model.MaxChargeCurrent = ((rxBuffer[21] << 8) | rxBuffer[22]) / 100.0;
                    model.CVVoltage = ((rxBuffer[23] << 8) | rxBuffer[24]) / 100.0;
                    model.SOH = ((rxBuffer[25] << 8) | rxBuffer[26]);
                }
            }

            decodedInfo = string.Format("[CVTE] 解析成功: 电压={0:F2}V, 电流={1:F2}A, SOC={2:F0}%, 容量={3:F1}Ah, CV={4:F2}V",
                model.Voltage, model.Current, model.SOC, model.RemainingCapacity, model.CVVoltage);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. GROWATT (Modbus RTU) 协议处理器
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

            if (cmd != 0x03 || regStart != 0x0014 || regCount != 0x000F)
                return false;

            if (!replyAllIds && id != specificId)
                return false;

            byte[] resp = new byte[35];
            resp[0] = id;
            resp[1] = 0x03;
            resp[2] = 30;

            lock (model.SyncRoot)
            {
                // 0x0014 保护状态
                ushort prot = 0;
                if (model.ProtOverVolt) prot |= 0x0001;
                if (model.ProtUnderVolt) prot |= 0x0002;
                if (model.ProtOverCurrent) prot |= 0x0004;
                if (model.ProtHighTemp) prot |= 0x0008;
                resp[3] = (byte)(prot >> 8); resp[4] = (byte)(prot & 0xFF);

                // 0x0015 SOC
                ushort uSoc = (ushort)Math.Round(model.SOC);
                resp[5] = (byte)(uSoc >> 8); resp[6] = (byte)(uSoc & 0xFF);

                // 0x0016 电压 (0.01V -> 10mV)
                ushort uVolt = (ushort)Math.Round(model.Voltage * 100);
                resp[7] = (byte)(uVolt >> 8); resp[8] = (byte)(uVolt & 0xFF);

                // 0x0017 电流 (0.1A)
                short sCurr = (short)Math.Round(model.Current * 10);
                resp[9] = (byte)((ushort)sCurr >> 8); resp[10] = (byte)((ushort)sCurr & 0xFF);

                // 0x0018 温度 (°C)
                short sTemp = (short)Math.Round(model.Temperature);
                resp[11] = (byte)((ushort)sTemp >> 8); resp[12] = (byte)((ushort)sTemp & 0xFF);

                // 0x0019 最大充电流 (0.1A)
                ushort uMaxChgI = (ushort)Math.Round(model.MaxChargeCurrent * 10);
                resp[13] = (byte)(uMaxChgI >> 8); resp[14] = (byte)(uMaxChgI & 0xFF);

                // 0x001A 剩余容量 (0.1Ah)
                ushort uRemCap = (ushort)Math.Round(model.RemainingCapacity * 10);
                resp[15] = (byte)(uRemCap >> 8); resp[16] = (byte)(uRemCap & 0xFF);

                // 0x001B 满充容量 (0.1Ah)
                ushort uFullCap = (ushort)Math.Round(model.FullCapacity * 10);
                resp[17] = (byte)(uFullCap >> 8); resp[18] = (byte)(uFullCap & 0xFF);

                for (int i = 19; i <= 28; i++) resp[i] = 0;

                // 0x0021 CV 充电压 (0.01V -> 10mV)
                ushort uCvV = (ushort)Math.Round(model.CVVoltage * 100);
                resp[29] = (byte)(uCvV >> 8); resp[30] = (byte)(uCvV & 0xFF);

                // 0x0022 告警状态
                ushort warn = 0;
                if (model.WarnSingleOverVolt || model.WarnGlobalOverVolt) warn |= 0x0001;
                if (model.WarnSingleUnderVolt || model.WarnGlobalUnderVolt) warn |= 0x0002;
                if (model.WarnOverCurrent) warn |= 0x0004;
                if (model.WarnHighTemp) warn |= 0x0008;
                resp[31] = (byte)(warn >> 8); resp[32] = (byte)(warn & 0xFF);
            }

            ushort crc = ModbusCrc.Calculate(resp, 33);
            resp[33] = (byte)(crc >> 8);
            resp[34] = (byte)(crc & 0xFF);

            txResponse = resp;
            decodedInfo = string.Format("[GROWATT] 应答 ID:{0:X2} -> 电压:{1:F2}V, SOC:{2:F0}%, 电流:{3:F1}A, 温度:{4:F0}°C, CV:{5:F2}V",
                id, model.Voltage, model.SOC, model.Current, model.Temperature, model.CVVoltage);
            return true;
        }

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            byte[] frame = new byte[8];
            frame[0] = targetId;
            frame[1] = 0x03;
            frame[2] = 0x00; frame[3] = 0x14; // 0x0014
            frame[4] = 0x00; frame[5] = 0x0F; // 15 寄存器
            ushort crc = ModbusCrc.Calculate(frame, 6);
            frame[6] = (byte)(crc >> 8);
            frame[7] = (byte)(crc & 0xFF);
            return frame;
        }

        public bool TryDecodeMasterResponse(byte[] rxBuffer, int rxLength, BmsDataModel model, out string decodedInfo)
        {
            decodedInfo = "";
            if (rxLength != 35) return false;
            if (!ModbusCrc.Check(rxBuffer, 35)) return false;

            lock (model.SyncRoot)
            {
                model.SOC = (rxBuffer[5] << 8) | rxBuffer[6];
                model.Voltage = ((rxBuffer[7] << 8) | rxBuffer[8]) / 100.0;
                model.Current = (short)((rxBuffer[9] << 8) | rxBuffer[10]) / 10.0;
                model.Temperature = (short)((rxBuffer[11] << 8) | rxBuffer[12]);
                model.MaxChargeCurrent = ((rxBuffer[13] << 8) | rxBuffer[14]) / 10.0;
                model.RemainingCapacity = ((rxBuffer[15] << 8) | rxBuffer[16]) / 10.0;
                model.FullCapacity = ((rxBuffer[17] << 8) | rxBuffer[18]) / 10.0;
                model.CVVoltage = ((rxBuffer[29] << 8) | rxBuffer[30]) / 100.0;
            }

            decodedInfo = string.Format("[GROWATT] 解析成功: 电压={0:F2}V, SOC={1:F0}%, 电流={2:F1}A, CV={3:F2}V",
                model.Voltage, model.SOC, model.Current, model.CVVoltage);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. VOLTRONIC (Modbus RTU) 协议处理器
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

            if (cmd != 0x03) return false;
            if (!replyAllIds && id != specificId) return false;

            if (regStart == 0x0030 && regCount == 0x0006)
            {
                byte[] resp = new byte[17];
                resp[0] = id; resp[1] = 0x03; resp[2] = 12;
                lock (model.SyncRoot)
                {
                    short sChgI = (short)(model.Current > 0 ? Math.Round(model.Current * 10) : 0);
                    short sDisI = (short)(model.Current < 0 ? Math.Round(-model.Current * 10) : 0);
                    resp[3] = (byte)(sChgI >> 8); resp[4] = (byte)(sChgI & 0xFF);
                    resp[5] = (byte)(sDisI >> 8); resp[6] = (byte)(sDisI & 0xFF);

                    ushort uVolt = (ushort)Math.Round(model.Voltage * 100);
                    resp[7] = (byte)(uVolt >> 8); resp[8] = (byte)(uVolt & 0xFF);

                    ushort uSoc = (ushort)Math.Round(model.SOC);
                    resp[9] = (byte)(uSoc >> 8); resp[10] = (byte)(uSoc & 0xFF);

                    uint uCap = (uint)Math.Round(model.FullCapacity * 10);
                    resp[11] = (byte)(uCap >> 24); resp[12] = (byte)(uCap >> 16);
                    resp[13] = (byte)(uCap >> 8); resp[14] = (byte)(uCap & 0xFF);
                }
                ushort crc = ModbusCrc.Calculate(resp, 15);
                resp[15] = (byte)(crc >> 8); resp[16] = (byte)(crc & 0xFF);
                txResponse = resp;
                decodedInfo = string.Format("[VOLTRONIC-Info] 响应 0x0030: 电压={0:F2}V, SOC={1:F0}%, 电流={2:F1}A",
                    model.Voltage, model.SOC, model.Current);
                return true;
            }
            else if (regStart == 0x0060 && regCount == 0x000A)
            {
                byte[] resp = new byte[25];
                resp[0] = id; resp[1] = 0x03; resp[2] = 20;
                lock (model.SyncRoot)
                {
                    resp[3] = 0; resp[4] = (byte)(model.WarnSingleOverVolt ? 1 : 0);
                    resp[5] = 0; resp[6] = (byte)(model.WarnSingleUnderVolt ? 1 : 0);
                    resp[7] = 0; resp[8] = (byte)(model.WarnHighTemp ? 1 : 0);
                    resp[9] = 0; resp[10] = (byte)(model.WarnLowTemp ? 1 : 0);
                    resp[11] = 0; resp[12] = (byte)(model.WarnOverCurrent ? 1 : 0);
                    resp[13] = 0; resp[14] = (byte)(model.ProtOverVolt ? 1 : 0);
                    resp[15] = 0; resp[16] = (byte)(model.ProtUnderVolt ? 1 : 0);
                    resp[17] = 0; resp[18] = (byte)(model.ProtHighTemp ? 1 : 0);
                    resp[19] = 0; resp[20] = (byte)(model.ProtUnderTemp ? 1 : 0);
                    resp[21] = 0; resp[22] = (byte)(model.ProtOverCurrent ? 1 : 0);
                }
                ushort crc = ModbusCrc.Calculate(resp, 23);
                resp[23] = (byte)(crc >> 8); resp[24] = (byte)(crc & 0xFF);
                txResponse = resp;
                decodedInfo = "[VOLTRONIC-Status] 响应 0x0060 状态/保护矩阵";
                return true;
            }
            else if (regStart == 0x0070 && regCount == 0x0003)
            {
                byte[] resp = new byte[11];
                resp[0] = id; resp[1] = 0x03; resp[2] = 6;
                lock (model.SyncRoot)
                {
                    ushort uCvV = (ushort)Math.Round(model.CVVoltage * 100);
                    resp[3] = (byte)(uCvV >> 8); resp[4] = (byte)(uCvV & 0xFF);
                    resp[5] = 0; resp[6] = 0;
                    ushort uMaxChgI = (ushort)Math.Round(model.MaxChargeCurrent * 10);
                    resp[7] = (byte)(uMaxChgI >> 8); resp[8] = (byte)(uMaxChgI & 0xFF);
                }
                ushort crc = ModbusCrc.Calculate(resp, 9);
                resp[9] = (byte)(crc >> 8); resp[10] = (byte)(crc & 0xFF);
                txResponse = resp;
                decodedInfo = string.Format("[VOLTRONIC-Config] 响应 0x0070: CV={0:F2}V, 限流={1:F1}A", model.CVVoltage, model.MaxChargeCurrent);
                return true;
            }

            return false;
        }

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            byte[] frame = new byte[8];
            frame[0] = targetId;
            frame[1] = 0x03;
            if (pollPhase == 0)
            {
                frame[2] = 0x00; frame[3] = 0x30; frame[4] = 0x00; frame[5] = 0x06;
            }
            else if (pollPhase == 1)
            {
                frame[2] = 0x00; frame[3] = 0x60; frame[4] = 0x00; frame[5] = 0x0A;
            }
            else
            {
                frame[2] = 0x00; frame[3] = 0x70; frame[4] = 0x00; frame[5] = 0x03;
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

            if (rxBuffer[2] == 12)
            {
                lock (model.SyncRoot)
                {
                    model.Voltage = ((rxBuffer[7] << 8) | rxBuffer[8]) / 100.0;
                    model.SOC = ((rxBuffer[9] << 8) | rxBuffer[10]);
                }
                decodedInfo = string.Format("[VOLTRONIC] 解析 Info 成功: 电压={0:F2}V, SOC={1:F0}%", model.Voltage, model.SOC);
                return true;
            }
            else if (rxBuffer[2] == 6)
            {
                lock (model.SyncRoot)
                {
                    model.CVVoltage = ((rxBuffer[3] << 8) | rxBuffer[4]) / 100.0;
                    model.MaxChargeCurrent = ((rxBuffer[7] << 8) | rxBuffer[8]) / 10.0;
                }
                decodedInfo = string.Format("[VOLTRONIC] 解析 Config 成功: CV={0:F2}V, 限流={1:F1}A", model.CVVoltage, model.MaxChargeCurrent);
                return true;
            }

            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. PYLONTECH (Pylon RS485 ASCII) 协议处理器
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

            if (rxLength < 12) return false;
            if (rxBuffer[0] != (byte)'~' || rxBuffer[rxLength - 1] != 0x0D) return false;

            string asciiStr = Encoding.ASCII.GetString(rxBuffer, 0, rxLength);
            if (asciiStr.Length < 10) return false;
            string cid2 = asciiStr.Substring(7, 2);

            string infoData = "";

            lock (model.SyncRoot)
            {
                if (cid2 == "42" || cid2 == "61")
                {
                    int uVoltMv = (int)Math.Round(model.Voltage * 1000);
                    short sCurr100Ma = (short)Math.Round(model.Current * 10);
                    int uRemCapMah = (int)Math.Round(model.RemainingCapacity * 1000);
                    int uFullCapMah = (int)Math.Round(model.FullCapacity * 1000);
                    ushort uSoc = (ushort)Math.Round(model.SOC);
                    ushort uKelvin = (ushort)Math.Round((model.Temperature + 273.15) * 10);

                    StringBuilder sb = new StringBuilder();
                    sb.Append("01");
                    sb.Append("0F");
                    for (int i = 0; i < 15; i++) sb.Append("0D48");
                    sb.Append("04");
                    for (int i = 0; i < 4; i++) sb.Append(uKelvin.ToString("X4"));
                    sb.Append(((ushort)sCurr100Ma).ToString("X4"));
                    sb.Append(((ushort)uVoltMv).ToString("X4"));
                    sb.Append(((ushort)(uRemCapMah / 100)).ToString("X4"));
                    sb.Append("00");
                    sb.Append(((ushort)(uFullCapMah / 100)).ToString("X4"));
                    sb.Append(uSoc.ToString("X2"));

                    infoData = sb.ToString();
                    decodedInfo = string.Format("[PYLON-42H Info] 应答: 电压={0:F2}V, 电流={1:F1}A, SOC={2:F0}%, 温度={3:F0}°C",
                        model.Voltage, model.Current, model.SOC, model.Temperature);
                }
                else if (cid2 == "44" || cid2 == "62")
                {
                    ushort warn = 0;
                    if (model.WarnSingleOverVolt || model.WarnGlobalOverVolt) warn |= 0xA000;
                    if (model.WarnSingleUnderVolt || model.WarnGlobalUnderVolt) warn |= 0x5000;
                    if (model.WarnOverCurrent) warn |= 0x0060;
                    if (model.WarnHighTemp) warn |= 0x0900;
                    if (model.WarnLowTemp) warn |= 0x0400;

                    ushort prot = 0;
                    if (model.ProtOverVolt) prot |= 0xA000;
                    if (model.ProtUnderVolt) prot |= 0x5000;
                    if (model.ProtOverCurrent) prot |= 0x0060;
                    if (model.ProtHighTemp) prot |= 0x0900;
                    if (model.ProtUnderTemp) prot |= 0x0400;

                    infoData = "010F" + new string('0', 30) + "0400000000" + warn.ToString("X4") + prot.ToString("X4") + "0000";
                    decodedInfo = "[PYLON-44H Status] 响应告警与保护状态字";
                }
                else if (cid2 == "47" || cid2 == "63")
                {
                    ushort uCvMv = (ushort)Math.Round(model.CVVoltage * 1000);
                    ushort uCutoffMv = (ushort)Math.Round((model.Voltage > 25 ? 21.0 : 42.0) * 1000);
                    short sMaxChg = (short)Math.Round(model.MaxChargeCurrent * 10);
                    short sMaxDis = (short)Math.Round(model.MaxDischargeCurrent * 10);

                    infoData = uCvMv.ToString("X4") + uCutoffMv.ToString("X4") + ((ushort)sMaxChg).ToString("X4") + ((ushort)sMaxDis).ToString("X4") + "00";
                    decodedInfo = string.Format("[PYLON-47H Config] 响应充放电控制: CV={0:F2}V, 充电限流={1:F1}A",
                        model.CVVoltage, model.MaxChargeCurrent);
                }
                else
                {
                    infoData = "00";
                    decodedInfo = string.Format("[PYLON-通用] 响应指令 CID2={0}", cid2);
                }
            }

            int len = infoData.Length;
            int lchk = (~((len & 0x0F) + ((len >> 4) & 0x0F) + ((len >> 8) & 0x0F)) + 1) & 0x0F;
            string lenid = lchk.ToString("X1") + len.ToString("X3");

            string adr = asciiStr.Substring(3, 2);
            string header = string.Format("~20{0}4600{1}{2}", adr, lenid, infoData);
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);

            ushort chk = PylonChecksum.Calculate(headerBytes, 1, headerBytes.Length - 1);
            string fullAscii = header + chk.ToString("X4") + "\r";

            txResponse = Encoding.ASCII.GetBytes(fullAscii);
            return true;
        }

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            string cid2 = pollPhase == 0 ? "42" : (pollPhase == 1 ? "44" : "47");
            string cmd = string.Format("~20{0:X2}46{1}E00202", targetId, cid2);
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

            decodedInfo = "[PYLON] 校验成功并更新报文";
            return true;
        }
    }
}
