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
        int MasterPollPhaseCount { get; }
        // 尝试解析逆变器发来的查询，并生成从机应答包
        bool TryProcessQuery(byte[] rxBuffer, int rxLength, BmsDataModel model, bool replyAllIds, int specificId, out byte[] txResponse, out string decodedInfo);
        // 主机轮询模式：构造发送给真实电池包的查询帧
        byte[] BuildMasterPollFrame(byte targetId, int pollPhase);
        // 主机模式：构造向从机下发的控制帧 (如关机、写参数等)
        byte[] BuildMasterControlFrame(byte targetId, string commandType, object param, out string description);
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
                                   (regStart >= 0x0020 && regStart <= 0x002F && regCount <= 16) ||
                                   (regStart >= 0x0040 && regStart <= 0x005A && regCount <= 27) ||
                                   (regStart >= 0x0065 && regStart <= 0x006E && regCount <= 10);
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
                    id, regStart, regCount, model.Voltage, model.Current, model.SOC, model.RemainingCapacity, model.FullCapacity, model.CVVoltage, model.MaxChargeCurrent, statusTag);
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
                    if (!model.StatusChargeEnable || model.ProtOverVolt || model.ProtHighTemp || model.ProtSystemFault)
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

                case 0x002E: // 单体最高电压 (mV, 动态适配 6KU 16串 / 3KU 8串, PDF Page 7)
                    int cellCountMax = model.Voltage > 35.0 ? 16 : 8;
                    return (ushort)Math.Round((model.Voltage / cellCountMax) * 1000 + 10);

                case 0x002F: // 单体最高电压位号
                    return 1;

                case 0x0030: // 单体最低电压 (mV, 动态适配 6KU 16串 / 3KU 8串, PDF Page 7)
                    int cellCountMin = model.Voltage > 35.0 ? 16 : 8;
                    return (ushort)Math.Round((model.Voltage / cellCountMin) * 1000 - 10);

                case 0x0031: // 单体最低电压位号
                    return (ushort)(model.Voltage > 35.0 ? 16 : 8);

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

                case 0x0040: // 电池串数 (动态适配 6KU 16串 / 3KU 8串, PDF Page 10)
                    return (ushort)(model.Voltage > 35.0 ? 16 : 8);

                default:
                    // 默认按 16 串 (6KU) 或 8 串 (3KU) 电芯电压填充 0x0041~0x005A
                    if (regAddress >= 0x0041 && regAddress <= 0x005A)
                    {
                        int nCells = model.Voltage > 35.0 ? 16 : 8;
                        int cellIdx = regAddress - 0x0040;
                        if (cellIdx <= nCells)
                        {
                            return (ushort)Math.Round((model.Voltage / nCells) * 1000);
                        }
                    }
                    // 默认多 NTC 温度填充 0x0065~0x006E
                    if (regAddress >= 0x0065 && regAddress <= 0x006E)
                    {
                        return (ushort)Math.Round(model.Temperature);
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

        public byte[] BuildMasterControlFrame(byte targetId, string commandType, object param, out string description)
        {
            ushort regAddr = 0;
            ushort regVal = 0;
            description = "";

            if (commandType == "WRITE_MAX_CHG_CURRENT")
            {
                double currentA = param is double ? (double)param : 100.0;
                regAddr = 0x0029;
                regVal = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(currentA * 100)));
                description = string.Format("[CVTE 主机下发] 写最大充电电流: {0:F1}A (0x{1:X4}=0x{2:X4})", currentA, regAddr, regVal);
            }
            else if (commandType == "WRITE_CV_VOLT")
            {
                double cvV = param is double ? (double)param : 56.0;
                regAddr = 0x002A;
                regVal = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(cvV * 100)));
                description = string.Format("[CVTE 主机下发] 写 CV 恒压点: {0:F2}V (0x{1:X4}=0x{2:X4})", cvV, regAddr, regVal);
            }
            else if (commandType == "FORCE_CHARGE")
            {
                regAddr = 0x0020;
                regVal = 0x1060; // 充电+放电使能 + 强制充电请求 Bit12
                description = string.Format("[CVTE 主机下发] 触发强制充电控制 (0x{0:X4}=0x{1:X4})", regAddr, regVal);
            }
            else
            {
                description = "[CVTE] 不支持的主机控制指令: " + commandType;
                return null;
            }

            byte[] frame = new byte[8];
            frame[0] = targetId;
            frame[1] = 0x06; // 写单个寄存器
            frame[2] = (byte)(regAddr >> 8);
            frame[3] = (byte)(regAddr & 0xFF);
            frame[4] = (byte)(regVal >> 8);
            frame[5] = (byte)(regVal & 0xFF);
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
            bool isValidGrowattAddr = (regStart == 0x0001 && regCount <= 35) ||
                                      (regStart >= 0x0010 && regStart <= 0x0014 && regCount <= 20) ||
                                      (regStart >= 0x0015 && regStart <= 0x0029 && regCount <= 2) ||
                                      (regStart == 0x0071 && regCount <= 16);
            if (!isValidGrowattAddr)
                return false;


            if (!replyAllIds && id != specificId)
                return false;

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
                    id, regStart, regCount, model.Voltage, model.SOC, model.Current, model.Temperature, model.CVVoltage, model.MaxChargeCurrent, statusTag);
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

                    bool canGwDis = model.StatusDischargeEnable && !model.ProtUnderVolt && !model.ProtUnderTemp && !model.ProtSystemFault;
                    bool canGwChg = model.StatusChargeEnable && !model.ProtOverVolt && !model.ProtHighTemp && !model.ProtSystemFault;
                    if (canGwDis) status |= (1 << 5);// Bit 5: Output Discharge status
                    if (canGwChg) status |= (1 << 6);// Bit 6: Output Charge status

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
                    if (!model.StatusChargeEnable || model.ProtOverVolt || model.ProtHighTemp || model.ProtSystemFault)
                        return 0;
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.MaxChargeCurrent * 100)));

                case 0x001A: // Gauge RM 剩余容量 (10mAh, PDF Page 9)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.RemainingCapacity * 100)));

                case 0x001B: // Gauge FCC 满充容量 (10mAh, PDF Page 9)
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.FullCapacity * 100)));

                case 0x001C: // YW / FW (硬件/软件版本 1~9, PDF Page 9, 11)
                    return 0x0101;

                case 0x001D: // Delta Cell Voltage (mV -> V, PDF Page 9)
                    return 20; // 20mV

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
                    if (!model.StatusDischargeEnable || model.ProtUnderVolt || model.ProtUnderTemp || model.ProtSystemFault)
                        return 0;
                    return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(model.MaxDischargeCurrent * 100)));

                case 0x0024: // Extended Error (PDF Page 9, 13)
                    return 0x0000;

                case 0x0025: // Maximum Cell Voltage (1mV, PDF Page 9)
                    int cellCountMax = model.Voltage > 35.0 ? 16 : 8;
                    return (ushort)Math.Round((model.Voltage / cellCountMax) * 1000 + 10);

                case 0x0026: // Minimum Cell Voltage (1mV, PDF Page 9)
                    int cellCountMin = model.Voltage > 35.0 ? 16 : 8;
                    return (ushort)Math.Round((model.Voltage / cellCountMin) * 1000 - 10);

                case 0x0027: // Maximum Cell Voltage Number (PDF Page 9)
                    return 1;

                case 0x0028: // Minimum Cell Voltage Number (PDF Page 9)
                    return (ushort)(model.Voltage > 35.0 ? 16 : 8);

                case 0x0029: // Cell Series (PDF Page 9)
                    return (ushort)(model.Voltage > 35.0 ? 16 : 8);

                default:
                    // 单体电压上报 0x0071 ~ 0x0080 (1mV, PDF Page 14-15)
                    if (regAddress >= 0x0071 && regAddress <= 0x0080)
                    {
                        int nCells = model.Voltage > 35.0 ? 16 : 8;
                        int cellIdx = regAddress - 0x0070;
                        if (cellIdx <= nCells)
                        {
                            return (ushort)Math.Round((model.Voltage / nCells) * 1000);
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

        public byte[] BuildMasterControlFrame(byte targetId, string commandType, object param, out string description)
        {
            ushort regAddr = 0;
            ushort regVal = 0;
            description = "";

            if (commandType == "WRITE_MAX_CHG_CURRENT")
            {
                double currentA = param is double ? (double)param : 100.0;
                regAddr = 0x0019;
                regVal = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(currentA * 100)));
                description = string.Format("[GROWATT 主机下发] 写最大充电电流: {0:F1}A (0x{1:X4}=0x{2:X4})", currentA, regAddr, regVal);
            }
            else if (commandType == "WRITE_MAX_DIS_CURRENT")
            {
                double currentA = param is double ? (double)param : 100.0;
                regAddr = 0x0023;
                regVal = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(currentA * 100)));
                description = string.Format("[GROWATT 主机下发] 写最大放电电流: {0:F1}A (0x{1:X4}=0x{2:X4})", currentA, regAddr, regVal);
            }
            else if (commandType == "WRITE_CV_VOLT")
            {
                double cvV = param is double ? (double)param : 56.0;
                regAddr = 0x0021;
                regVal = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(cvV * 100)));
                description = string.Format("[GROWATT 主机下发] 写 CV 恒压点: {0:F2}V (0x{1:X4}=0x{2:X4})", cvV, regAddr, regVal);
            }
            else if (commandType == "FORCE_CHARGE")
            {
                regAddr = 0x0013;
                regVal = 0x1060; // 充电+放电使能 + 强制充电请求 Bit12
                description = string.Format("[GROWATT 主机下发] 触发强制充电控制 (0x{0:X4}=0x{1:X4})", regAddr, regVal);
            }
            else
            {
                description = "[GROWATT] 不支持的主机控制指令: " + commandType;
                return null;
            }

            byte[] frame = new byte[8];
            frame[0] = targetId;
            frame[1] = 0x06; // 写单个寄存器
            frame[2] = (byte)(regAddr >> 8);
            frame[3] = (byte)(regAddr & 0xFF);
            frame[4] = (byte)(regVal >> 8);
            frame[5] = (byte)(regVal & 0xFF);
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

                    // 0x0014: 错误与告警
                    ushort err = (ushort)((rxBuffer[5] << 8) | rxBuffer[6]);
                    model.WarnSingleOverVolt = (err & (1 << 0)) != 0;
                    model.WarnSingleUnderVolt = (err & (1 << 1)) != 0;
                    model.WarnGlobalOverVolt = (err & (1 << 2)) != 0;
                    model.WarnGlobalUnderVolt = (err & (1 << 3)) != 0;
                    model.WarnOverCurrent = (err & ((1 << 4) | (1 << 5))) != 0;
                    model.WarnHighTemp = (err & ((1 << 6) | (1 << 8) | (1 << 10) | (1 << 11))) != 0;
                    model.WarnLowTemp = (err & ((1 << 7) | (1 << 9) | (1 << 12))) != 0;
                    model.ProtSoftStart = (err & (1 << 8)) != 0;
                    model.ProtSystemFault = (err & (1 << 9)) != 0;
                    model.WarnVoltDiff = (err & (1 << 10)) != 0;
                    model.WarnLowCapacity = (err & (1 << 13)) != 0;

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
                    // 0x001F: SOH
                    ushort soh = (ushort)((rxBuffer[27] << 8) | rxBuffer[28]);
                    if (soh > 0 && soh <= 100) model.SOH = soh;

                    // 0x0021: CV 恒压点 (10mV)
                    model.CVVoltage = ((rxBuffer[31] << 8) | rxBuffer[32]) / 100.0;
                    // 0x0023: 最大放电电流 (10mA)
                    model.MaxDischargeCurrent = ((rxBuffer[35] << 8) | rxBuffer[36]) / 100.0;
                }
                else if (byteCount >= 30) // 15 寄存器 (0x0014~0x0022 兼容格式)
                {
                    model.SOC = (rxBuffer[5] << 8) | rxBuffer[6];
                    model.Voltage = ((rxBuffer[7] << 8) | rxBuffer[8]) / 100.0;
                    model.Current = (short)((rxBuffer[9] << 8) | rxBuffer[10]) / 100.0;
                    model.Temperature = (short)((rxBuffer[11] << 8) | rxBuffer[12]);
                    model.MaxChargeCurrent = ((rxBuffer[13] << 8) | rxBuffer[14]) / 100.0;
                    model.RemainingCapacity = ((rxBuffer[15] << 8) | rxBuffer[16]) / 100.0;
                    model.FullCapacity = ((rxBuffer[17] << 8) | rxBuffer[18]) / 100.0;
                    model.CVVoltage = ((rxBuffer[29] << 8) | rxBuffer[30]) / 100.0;
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
                                        (regStart == 0x0026 && regCount <= 10) ||
                                        (regStart == 0x0001 && regCount <= 2) ||
                                        (regStart == 0x0010 && regCount == 1) ||
                                        (regStart == 0x0011 && regCount <= 16);
            if (!isValidVoltronicAddr)
                return false;


            if (!replyAllIds && id != specificId) return false;

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
                    id, regStart, regCount, model.Voltage, model.SOC, model.Current, model.CVVoltage, model.CutoffVoltage, model.MaxChargeCurrent, model.MaxDischargeCurrent, statusTag);
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
                // ── 1. 版本与电芯总数 (0x0001~0x0010, PDF Page 3-4) ──
                case 0x0001: return 0x0001; // Protocol Type
                case 0x0002: return 0x0001; // Protocol Version
                case 0x0010: return (ushort)(model.Voltage > 35.0 ? 16 : 8); // Number of cell: L pcs

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
                    return (ushort)((model.WarnOverCurrent || model.ProtOverCurrent) ? 0x0002 : 0x0000);

                case 0x0066: // Module charge temperature state (01H: low temp, 02H: high temp)
                    if (model.WarnLowTemp || model.ProtUnderTemp) return 0x0001;
                    if (model.WarnHighTemp || model.ProtHighTemp) return 0x0002;
                    return 0x0000;

                case 0x0067: // Module discharge temperature state (01H: low temp, 02H: high temp)
                    if (model.WarnLowTemp || model.ProtUnderTemp) return 0x0001;
                    if (model.WarnHighTemp || model.ProtHighTemp) return 0x0002;
                    return 0x0000;

                case 0x0068: // Cell charge temperature state (02H: high temp)
                    return (ushort)((model.WarnHighTemp || model.ProtHighTemp) ? 0x0002 : 0x0000);

                case 0x0069: // Cell discharge temperature state (02H: high temp)
                    return (ushort)((model.WarnHighTemp || model.ProtHighTemp) ? 0x0002 : 0x0000);

                // ── 4. 充放电控制参数组 (0x0070~0x0074, PDF Page 5-6) ──
                case 0x0070: // Charge voltage limit / CV Voltage (0.1V, PDF Page 5)
                    return (ushort)Math.Max(0, (int)Math.Round(model.CVVoltage * 10));

                case 0x0071: // Discharge voltage limit / Cutoff Voltage (0.1V, PDF Page 5)
                    return (ushort)Math.Max(0, (int)Math.Round(model.CutoffVoltage * 10));

                case 0x0072: // Charge current limit (0.1A, PDF Page 5)
                    if (!model.StatusChargeEnable || model.ProtOverVolt || model.ProtHighTemp || model.ProtSystemFault)
                        return 0;
                    return (ushort)Math.Max(0, (int)Math.Round(model.MaxChargeCurrent * 10));

                case 0x0073: // Discharge current limit (0.1A, PDF Page 5)
                    if (!model.StatusDischargeEnable || model.ProtUnderVolt || model.ProtUnderTemp || model.ProtSystemFault)
                        return 0;
                    return (ushort)Math.Max(0, (int)Math.Round(model.MaxDischargeCurrent * 10));

                case 0x0074: // Charge, discharge status (PDF Page 5-6)
                    ushort vStatus = 0;
                    bool canVChg = model.StatusChargeEnable && !model.ProtOverVolt && !model.ProtHighTemp && !model.ProtSystemFault;
                    bool canVDis = model.StatusDischargeEnable && !model.ProtUnderVolt && !model.ProtUnderTemp && !model.ProtSystemFault;
                    if (canVChg) vStatus |= (1 << 7);    // Bit 7: Charge enable (1: yes, 0: request stop charge)
                    if (canVDis) vStatus |= (1 << 6);    // Bit 6: Discharge enable (1: yes, 0: request stop discharge)
                    if (model.StatusForceCharge) vStatus |= (1 << 5);     // Bit 5: Charge immediately (强制充电请求)
                    if (model.StatusFullCharge) vStatus |= (1 << 3);      // Bit 3: Full charge request (满充请求)
                    return vStatus;


                // ── 5. 温度传感器组 (0x0026~0x002F, 0.1K 开尔文温度, PDF Page 4) ──
                default:
                    if (regAddress >= 0x0026 && regAddress <= 0x002F)
                    {
                        // 0.1K = (273.15 + Temperature) * 10
                        return (ushort)Math.Round((273.15 + model.Temperature) * 10);
                    }
                    if (regAddress >= 0x0011 && regAddress <= 0x0024)
                    {
                        int nCells = model.Voltage > 35.0 ? 16 : 8;
                        int cellIdx = regAddress - 0x0010;
                        if (cellIdx <= nCells)
                        {
                            return (ushort)Math.Round((model.Voltage / nCells) * 10); // 0.1V
                        }
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

        public byte[] BuildMasterControlFrame(byte targetId, string commandType, object param, out string description)
        {
            ushort regAddr = 0;
            ushort regVal = 0;
            description = "";

            if (commandType == "WRITE_MAX_CHG_CURRENT")
            {
                double currentA = param is double ? (double)param : 100.0;
                regAddr = 0x0072;
                regVal = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(currentA * 10)));
                description = string.Format("[VOLTRONIC 主机下发] 写最大充电限流: {0:F1}A (0x{1:X4}=0x{2:X4})", currentA, regAddr, regVal);
            }
            else if (commandType == "WRITE_MAX_DIS_CURRENT")
            {
                double currentA = param is double ? (double)param : 100.0;
                regAddr = 0x0073;
                regVal = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(currentA * 10)));
                description = string.Format("[VOLTRONIC 主机下发] 写最大放电限流: {0:F1}A (0x{1:X4}=0x{2:X4})", currentA, regAddr, regVal);
            }
            else if (commandType == "WRITE_CV_VOLT")
            {
                double cvV = param is double ? (double)param : 56.0;
                regAddr = 0x0070;
                regVal = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(cvV * 10)));
                description = string.Format("[VOLTRONIC 主机下发] 写 CV 恒压点: {0:F1}V (0x{1:X4}=0x{2:X4})", cvV, regAddr, regVal);
            }
            else if (commandType == "WRITE_CUTOFF_VOLT")
            {
                double cutV = param is double ? (double)param : 42.0;
                regAddr = 0x0071;
                regVal = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(cutV * 10)));
                description = string.Format("[VOLTRONIC 主机下发] 写放电截止点: {0:F1}V (0x{1:X4}=0x{2:X4})", cutV, regAddr, regVal);
            }
            else if (commandType == "FORCE_CHARGE")
            {
                regAddr = 0x0074;
                regVal = 0x00E8; // Bit 7(充电使能) + Bit 6(放电使能) + Bit 5(请求强充) + Bit 3(请求满充)
                description = string.Format("[VOLTRONIC 主机下发] 触发强制充电控制字 (0x{0:X4}=0x{1:X4})", regAddr, regVal);
            }
            else
            {
                description = "[VOLTRONIC] 不支持的主机控制指令: " + commandType;
                return null;
            }

            byte[] frame = new byte[8];
            frame[0] = targetId;
            frame[1] = 0x06; // 写单个寄存器
            frame[2] = (byte)(regAddr >> 8);
            frame[3] = (byte)(regAddr & 0xFF);
            frame[4] = (byte)(regVal >> 8);
            frame[5] = (byte)(regVal & 0xFF);
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

                    model.WarnGlobalOverVolt = s0 == 0x02;
                    model.WarnGlobalUnderVolt = s1 == 0x01;
                    model.WarnSingleOverVolt = s2 == 0x02;
                    model.WarnSingleUnderVolt = s3 == 0x01;
                    model.WarnOverCurrent = (s4 == 0x02 || s5 == 0x02);
                    model.WarnHighTemp = (s6 == 0x02 || s7 == 0x02);
                    model.WarnLowTemp = (s6 == 0x01 || s7 == 0x01);

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
                    ushort uKelvinMax = (ushort)(uKelvinAvg + (model.WarnHighTemp ? 40 : 5));
                    ushort uKelvinMin = (ushort)Math.Max(0, uKelvinAvg - (model.WarnLowTemp ? 40 : 5));

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

                    decodedInfo = string.Format("[PYLON-61H System Analog] 应答系统模拟量: 总压={0:F2}V, 电流={1:F2}A, SOC={2:F0}%, SOH={3:F0}%, 温度={4:F1}°C, 单芯={5}串({6}mV){7}",
                        model.Voltage, model.Current, model.SOC, model.SOH, model.Temperature, cellCount, cellVoltMv, statusTag61);
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
                    // 3. 最大充电电流 (10mA / 0.01A: 当禁止充电或处于过压/高温/故障保护时强制输出 0A)
                    bool canCharge = model.StatusChargeEnable && !model.ProtOverVolt && !model.ProtHighTemp && !model.ProtSystemFault;
                    ushort uMaxChg = (ushort)(canCharge ? Math.Max(0, Math.Min(65535, Math.Round(model.MaxChargeCurrent * 100))) : 0);
                    // 4. 最大放电电流 (10mA: 当禁止放电或处于欠压/低温/故障保护时强制输出 0A)
                    bool canDischarge = model.StatusDischargeEnable && !model.ProtUnderVolt && !model.ProtUnderTemp && !model.ProtSystemFault;
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
                    sb.Append(((ushort)Math.Max(0, Math.Min(65535, model.CycleCount))).ToString("X4")); // 循环次数
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

        public byte[] BuildMasterControlFrame(byte targetId, string commandType, object param, out string description)
        {
            description = "";
            if (commandType == "SHUTDOWN")
            {
                string cmd = string.Format("~20{0:X2}4664E00201", targetId);
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmd);
                ushort chk = PylonChecksum.Calculate(cmdBytes, 1, cmdBytes.Length - 1);
                string full = cmd + chk.ToString("X4") + "\r";
                description = string.Format("[PYLONTECH 主机下发] 64H 电池系统关机指令 (目标ID: {0:X2})", targetId);
                return Encoding.ASCII.GetBytes(full);
            }
            else
            {
                description = "[PYLONTECH] 不支持的主机控制指令: " + commandType;
                return null;
            }
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
                            byte currWarn = 0, voltWarn = 0, prot1 = 0;
                            byte.TryParse(info.Substring(statusOffset44, 2), System.Globalization.NumberStyles.HexNumber, null, out currWarn);
                            byte.TryParse(info.Substring(statusOffset44 + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out voltWarn);
                            if (info.Length >= statusOffset44 + 6)
                                byte.TryParse(info.Substring(statusOffset44 + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out prot1);

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
        Cvte
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
        private DetectedProtocolType _latchedProtocol = DetectedProtocolType.Unknown;
        private InverterModel _latchedModel = InverterModel.Unknown;
        private byte _latchedSlaveId = 0;
        private DateTime _lastDetectedTime = DateTime.MinValue;

        public DetectedProtocolType LatchedProtocol { get { return _latchedProtocol; } }
        public InverterModel LatchedModel { get { return _latchedModel; } }
        public byte LatchedSlaveId { get { return _latchedSlaveId; } }
        public DateTime LastDetectedTime { get { return _lastDetectedTime; } }

        public void Reset()
        {
            _latchedProtocol = DetectedProtocolType.Unknown;
            _latchedModel = InverterModel.Unknown;
            _latchedSlaveId = 0;
            _lastDetectedTime = DateTime.MinValue;
        }

        public DetectionResult InspectFrame(byte[] buffer, int offset, int length)
        {
            if (buffer == null || length < 4) return null;

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
                    _latchedProtocol = res.Protocol;
                    if (res.Model != InverterModel.Unknown) _latchedModel = res.Model;
                    _latchedSlaveId = slaveId;
                    _lastDetectedTime = res.Timestamp;
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
                            res.Model = InverterModel.INV_6KU;
                            res.IsSignatureConfirmed = true;
                        }
                        else if (cid2 == "61" || cid2 == "62" || cid2 == "63")
                        {
                            res.Model = _latchedModel != InverterModel.Unknown ? _latchedModel : InverterModel.Unknown;
                            res.IsSignatureConfirmed = (_latchedModel != InverterModel.Unknown);
                        }

                        _latchedProtocol = res.Protocol;
                        if (res.Model != InverterModel.Unknown) _latchedModel = res.Model;
                        _latchedSlaveId = slaveId;
                        _lastDetectedTime = res.Timestamp;
                        return res;
                    }
                }
            }

            return null;
        }
    }

    #endregion
}

