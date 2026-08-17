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

            if (rxLength < 10) return false;
            if (rxBuffer[0] != (byte)'~' || rxBuffer[rxLength - 1] != 0x0D) return false;

            string asciiStr = Encoding.ASCII.GetString(rxBuffer, 0, rxLength);
            if (asciiStr.Length < 10) return false;

            // 提取协议字段: ~ VER(2) ADR(2) CID1(2) CID2(2) LENGTH(4) [INFO] CHKSUM(4) \r
            string ver = asciiStr.Substring(1, 2);
            string adrStr = asciiStr.Substring(3, 2);
            string cid1 = asciiStr.Substring(5, 2);
            string cid2 = asciiStr.Substring(7, 2).ToUpper();

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

                    int cellCount = model.Voltage < 35.0 ? 8 : 16;
                    ushort cellVoltMv = (ushort)Math.Max(0, Math.Min(65535, Math.Round((model.Voltage / cellCount) * 1000)));
                    ushort cellVoltMax = (ushort)(cellVoltMv + (model.WarnVoltDiff ? 100 : 5));
                    ushort cellVoltMin = (ushort)Math.Max(0, cellVoltMv - (model.WarnVoltDiff ? 100 : 5));

                    // 温度: 绝对开尔文温度 * 10 (Kelvin: 0.1K, e.g. 25.5°C = 25.5*10 + 2731 = 2986)
                    ushort uKelvinAvg = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.Temperature * 10 + 2731)));
                    ushort uKelvinMax = (ushort)(uKelvinAvg + 5);
                    ushort uKelvinMin = (ushort)Math.Max(0, uKelvinAvg - 5);

                    StringBuilder sb = new StringBuilder();
                    sb.Append(((ushort)uVoltMv).ToString("X4"));          // 1. 电池组系统总平均电压 (mV)
                    sb.Append(((ushort)sCurr10Ma).ToString("X4"));         // 2. 电池组系统总电流 (10mA, 有符号)
                    sb.Append(uSoc.ToString("X2"));                       // 3. 电池组系统 SOC (%)
                    sb.Append(((ushort)50).ToString("X4"));                // 4. 平均循环次数
                    sb.Append(((ushort)50).ToString("X4"));                // 5. 最大循环次数
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
                    decodedInfo = string.Format("[PYLON-61H System Analog] 应答系统模拟量: 总压={0:F2}V, 电流={1:F2}A, SOC={2:F0}%, SOH={3:F0}%, 温度={4:F1}°C, 单芯={5}串({6}mV)",
                        model.Voltage, model.Current, model.SOC, model.SOH, model.Temperature, cellCount, cellVoltMv);
                }
                else if (cid2 == "62") // 2.3 获取电池组系统状态告警量信息 (Page 17-18, 4字节 / 8 HEX字符)
                {
                    byte warn1 = 0;
                    if (model.WarnGlobalOverVolt) warn1 |= 0x80;  // Bit 7: 模块总压高压
                    if (model.WarnGlobalUnderVolt) warn1 |= 0x40; // Bit 6: 模块总压低压
                    if (model.WarnSingleOverVolt) warn1 |= 0x20;  // Bit 5: 单芯电压高压
                    if (model.WarnSingleUnderVolt) warn1 |= 0x10; // Bit 4: 单芯电压低压
                    if (model.WarnHighTemp) warn1 |= 0x08;        // Bit 3: 单芯温度高温
                    if (model.WarnLowTemp) warn1 |= 0x04;         // Bit 2: 单芯温度低温
                    if (model.WarnHighTemp) warn1 |= 0x02;        // Bit 1: MOSFET 高温
                    if (model.WarnVoltDiff) warn1 |= 0x01;        // Bit 0: 单芯电压一致性告警

                    byte warn2 = 0;
                    if (model.WarnVoltDiff) warn2 |= 0x80;        // Bit 7: 单芯温度一致性告警
                    if (model.WarnOverCurrent && model.Current >= 0) warn2 |= 0x40; // Bit 6: 充电过流告警
                    if (model.WarnOverCurrent && model.Current < 0) warn2 |= 0x20;  // Bit 5: 放电过流告警
                    if (model.ProtSystemFault) warn2 |= 0x10;     // Bit 4: 内部通信错误

                    byte prot1 = 0;
                    if (model.ProtOverVolt) prot1 |= 0x80;        // Bit 7: 模块总压过压
                    if (model.ProtUnderVolt) prot1 |= 0x40;       // Bit 6: 模块总压欠压
                    if (model.ProtOverVolt) prot1 |= 0x20;        // Bit 5: 单芯电压过压
                    if (model.ProtUnderVolt) prot1 |= 0x10;       // Bit 4: 单芯电压欠压
                    if (model.ProtHighTemp) prot1 |= 0x08;        // Bit 3: 单芯温度过温
                    if (model.ProtUnderTemp) prot1 |= 0x04;       // Bit 2: 单芯温度欠温
                    if (model.ProtHighTemp) prot1 |= 0x02;        // Bit 1: MOSFET 过温

                    byte prot2 = 0;
                    if (model.ProtOverCurrent && model.Current >= 0) prot2 |= 0x40; // Bit 6: 充电过流保护
                    if (model.ProtOverCurrent && model.Current < 0) prot2 |= 0x20;  // Bit 5: 放电过流保护
                    if (model.ProtSystemFault || model.ProtShortCircuit) prot2 |= 0x08; // Bit 3: 系统故障保护

                    infoData = string.Format("{0:X2}{1:X2}{2:X2}{3:X2}", warn1, warn2, prot1, prot2);
                    if (prot1 != 0 || prot2 != 0)
                    {
                        decodedInfo = string.Format("[PYLON-62H 保护跳闸] Prot1=0x{0:X2}, Prot2=0x{1:X2}, Warn1=0x{2:X2}, Warn2=0x{3:X2}",
                            prot1, prot2, warn1, warn2);
                    }
                    else if (warn1 != 0 || warn2 != 0)
                    {
                        decodedInfo = string.Format("[PYLON-62H 告警提示] Warn1=0x{0:X2}, Warn2=0x{1:X2}", warn1, warn2);
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
                    // 2. 放电电压建议下限 (mV, Accuracy 3)
                    double cutoffV = model.Voltage < 35.0 ? 21.0 : 42.0;
                    ushort uCutoffMv = (ushort)Math.Max(0, Math.Min(65535, Math.Round(cutoffV * 1000)));
                    // 3. 最大充电电流 (10mA / 0.01A: PDF page 20: 0x09C4 = 2500 -> 25.0A; 100.0A -> 10000 = 0x2710)
                    ushort uMaxChg = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.MaxChargeCurrent * 100)));
                    // 4. 最大放电电流 (10mA)
                    ushort uMaxDis = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.MaxDischargeCurrent * 100)));

                    // 5. 充放电状态字 (1 字节):
                    // Bit 7: Charge enable (1: yes; 0: request stop charge)
                    // Bit 6: Discharge enable (1: yes; 0: request stop discharge)
                    // Bit 5: 强充，立即充电/charge immediately (1: yes; 0: normal)
                    // Bit 4: 满充请求/full charge request (1: yes; 0: normal)
                    byte statusByte = 0;
                    if (model.StatusChargeEnable && !model.ProtOverVolt && !model.ProtHighTemp && !model.ProtSystemFault)
                        statusByte |= 0x80; // 允许充电 (Bit 7 = 1)
                    if (model.StatusDischargeEnable && !model.ProtUnderVolt && !model.ProtUnderTemp && !model.ProtSystemFault)
                        statusByte |= 0x40; // 允许放电 (Bit 6 = 1)
                    if (model.StatusForceCharge)
                        statusByte |= 0x20; // 强制充电 (Bit 5 = 1)

                    StringBuilder sb = new StringBuilder();
                    sb.Append(uCvMv.ToString("X4"));
                    sb.Append(uCutoffMv.ToString("X4"));
                    sb.Append(uMaxChg.ToString("X4"));
                    sb.Append(uMaxDis.ToString("X4"));
                    sb.Append(statusByte.ToString("X2"));

                    infoData = sb.ToString();
                    decodedInfo = string.Format("[PYLON-63H System Config] 响应充放电控制: CV={0:F2}V, 截止={1:F2}V, 充电限流={2:F1}A, 放电限流={3:F1}A, 状态=0x{4:X2}(充使能:{5}, 放使能:{6})",
                        model.CVVoltage, cutoffV, model.MaxChargeCurrent, model.MaxDischargeCurrent, statusByte,
                        (statusByte & 0x80) != 0, (statusByte & 0x40) != 0);
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
                    int cellCount = model.Voltage < 35.0 ? 8 : 16;
                    ushort cellMv = (ushort)Math.Max(0, Math.Min(65535, Math.Round((model.Voltage / cellCount) * 1000)));
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
                    for (int i = 0; i < cellCount; i++) sb.Append(cellMv.ToString("X4")); // 各串电芯电压 (mV)
                    sb.Append("04"); // 4 个温度传感器
                    for (int i = 0; i < 4; i++) sb.Append(uKelvin.ToString("X4")); // 温度 (0.1K)
                    sb.Append(((ushort)sCurr100Ma).ToString("X4")); // 电流 (0.1A, 有符号)
                    sb.Append(uVoltMv.ToString("X4"));              // 总压 (mV)
                    sb.Append(uRemCap10Mah.ToString("X4"));         // 剩余容量 (10mAh)
                    sb.Append("02");                                // P - 预留/已定义
                    sb.Append(uFullCap10Mah.ToString("X4"));        // 满充总容量 (10mAh)
                    sb.Append(((ushort)50).ToString("X4"));          // 循环次数
                    sb.Append(uSoc.ToString("X2"));                 // 电池 SOC (%)

                    infoData = sb.ToString();
                    decodedInfo = string.Format("[PYLON-42H Pack Info] 应答单包数据: 电压={0:F2}V, 电流={1:F1}A, SOC={2:F0}%, 容量={3:F1}/{4:F1}Ah, 温度={5:F1}°C ({6}串)",
                        model.Voltage, model.Current, model.SOC, model.RemainingCapacity, model.FullCapacity, model.Temperature, cellCount);
                }
                else if (cid2 == "44") // 获取单包告警信息
                {
                    int cellCount = model.Voltage < 35.0 ? 8 : 16;
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
                    double cutoffV = model.Voltage < 35.0 ? 21.0 : 42.0;
                    ushort batUvMv = (ushort)Math.Max(0, Math.Min(65535, Math.Round(cutoffV * 1000)));
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

        public byte[] BuildMasterPollFrame(byte targetId, int pollPhase)
        {
            // 主机轮询: Phase 0: 61H (系统模拟量), Phase 1: 62H (系统告警), Phase 2: 63H (充放电控制)
            string cid2 = pollPhase == 0 ? "61" : (pollPhase == 1 ? "62" : "63");
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
                decodedInfo = string.Format("[PYLON] 从机返回错误码 RTN={0}", rtn);
                return false;
            }

            int infoLen = 0;
            int.TryParse(asciiStr.Substring(10, 3), System.Globalization.NumberStyles.HexNumber, null, out infoLen);
            if (asciiStr.Length < 13 + infoLen + 5) return false;

            string info = asciiStr.Substring(13, infoLen);

            lock (model.SyncRoot)
            {
                // 如果是 61H 响应 (长度 98)
                if (infoLen == 98)
                {
                    ushort uVoltMv;
                    short sCurr10Ma;
                    byte uSoc;
                    ushort uTempK;

                    if (ushort.TryParse(info.Substring(0, 4), System.Globalization.NumberStyles.HexNumber, null, out uVoltMv))
                        model.Voltage = uVoltMv / 1000.0;

                    if (short.TryParse(info.Substring(4, 4), System.Globalization.NumberStyles.HexNumber, null, out sCurr10Ma))
                        model.Current = sCurr10Ma / 100.0;

                    if (byte.TryParse(info.Substring(8, 2), System.Globalization.NumberStyles.HexNumber, null, out uSoc))
                        model.SOC = uSoc;

                    if (ushort.TryParse(info.Substring(26, 4), System.Globalization.NumberStyles.HexNumber, null, out uTempK))
                        model.Temperature = (uTempK - 2731) / 10.0;

                    decodedInfo = string.Format("[PYLON] 解析 61H 成功: 电压={0:F2}V, 电流={1:F2}A, SOC={2:F0}%, 温度={3:F1}°C",
                        model.Voltage, model.Current, model.SOC, model.Temperature);
                    return true;
                }
                // 如果是 63H 响应 (长度 18)
                else if (infoLen == 18)
                {
                    ushort uCvMv, uChg10Ma, uDis10Ma;
                    if (ushort.TryParse(info.Substring(0, 4), System.Globalization.NumberStyles.HexNumber, null, out uCvMv))
                        model.CVVoltage = uCvMv / 1000.0;

                    if (ushort.TryParse(info.Substring(8, 4), System.Globalization.NumberStyles.HexNumber, null, out uChg10Ma))
                        model.MaxChargeCurrent = uChg10Ma / 100.0;

                    if (ushort.TryParse(info.Substring(12, 4), System.Globalization.NumberStyles.HexNumber, null, out uDis10Ma))
                        model.MaxDischargeCurrent = uDis10Ma / 100.0;

                    decodedInfo = string.Format("[PYLON] 解析 63H 成功: CV={0:F2}V, 充电限流={1:F1}A, 放电限流={2:F1}A",
                        model.CVVoltage, model.MaxChargeCurrent, model.MaxDischargeCurrent);
                    return true;
                }
                // 如果是 42H 响应 (单包模拟量)
                else if (infoLen >= 30 && info.StartsWith("01"))
                {
                    int cellCount;
                    if (int.TryParse(info.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out cellCount))
                    {
                        int tempOffset = 4 + cellCount * 4;
                        if (info.Length >= tempOffset + 2)
                        {
                            int tempCount;
                            if (int.TryParse(info.Substring(tempOffset, 2), System.Globalization.NumberStyles.HexNumber, null, out tempCount))
                            {
                                int currOffset = tempOffset + 2 + tempCount * 4;
                                if (info.Length >= currOffset + 12)
                                {
                                    short sCurr;
                                    ushort uVolt;
                                    if (short.TryParse(info.Substring(currOffset, 4), System.Globalization.NumberStyles.HexNumber, null, out sCurr))
                                        model.Current = sCurr / 10.0;

                                    if (ushort.TryParse(info.Substring(currOffset + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out uVolt))
                                        model.Voltage = uVolt / 1000.0;

                                    decodedInfo = string.Format("[PYLON] 解析 42H 成功: 电压={0:F2}V, 电流={1:F1}A", model.Voltage, model.Current);
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            decodedInfo = "[PYLON] 校验成功并更新报文";
            return true;
        }
    }
}
