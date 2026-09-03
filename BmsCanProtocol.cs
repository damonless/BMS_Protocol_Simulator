using System;
using System.Collections.Generic;
using System.Text;

namespace BMS_Protocol_Simulator
{
    /// <summary>
    /// CAN 2.0A/2.0B 标准/扩展数据帧
    /// </summary>
    public class CanFrame
    {
        public uint Id { get; set; }
        public bool IsExtended { get; set; }
        public bool IsRemote { get; set; }
        public byte Dlc { get; set; }
        public byte[] Data { get; set; }
        public DateTime Timestamp { get; set; }

        public CanFrame()
        {
            Data = new byte[8];
            Timestamp = DateTime.Now;
        }

        public CanFrame(uint id, byte[] data, bool isExtended = false, bool isRemote = false)
        {
            Id = id;
            IsExtended = isExtended;
            IsRemote = isRemote;
            Dlc = (byte)(data != null ? Math.Min(data.Length, 8) : 0);
            Data = new byte[8];
            if (data != null && Dlc > 0)
            {
                Array.Copy(data, Data, Dlc);
            }
            Timestamp = DateTime.Now;
        }

        public string ToHexString()
        {
            if (Data == null || Dlc == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Dlc; i++)
            {
                if (i > 0) sb.Append(" ");
                sb.Append(Data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        public byte[] GetRawData()
        {
            byte[] res = new byte[Dlc];
            if (Data != null && Dlc > 0)
            {
                Array.Copy(Data, res, Dlc);
            }
            return res;
        }
    }

    /// <summary>
    /// 江苏汉储 (Jiangsu Hanchu Energy Technology Co., LTD)
    /// Battery Management System (BMS) CAN BUS Protocol Version: V1.0.1
    /// 标准帧 (11位ID), 500kbps, 小端序 (Little Endian), 自动发送周期 1s
    /// </summary>
    public static class HanchuCanProtocol
    {
        public const uint ID_INVERTER_HANDSHAKE = 0x305;
        public const uint ID_CHARGE_DISCHARGE_MGMT = 0x351;
        public const uint ID_SOC_SOH_CELL_VOLT = 0x355;
        public const uint ID_PACK_VOLT_CURR_TEMP = 0x356;
        public const uint ID_ALARM_PROTECTION = 0x359;
        public const uint ID_REQUEST_FLAGS_STATUS = 0x35C;
        public const uint ID_MANUFACTURER_NAME = 0x35E;

        /// <summary>
        /// 逆变器握手帧: ID 0x305, 8字节全为 00
        /// </summary>
        public static CanFrame BuildInverterHandshakeFrame()
        {
            byte[] data = new byte[8]; // 全 00
            return new CanFrame(ID_INVERTER_HANDSHAKE, data, false, false);
        }

        /// <summary>
        /// 0x351: 充放电管理参数
        /// Byte 0-1: 充电推荐电压 (0.1V, ushort, LE)
        /// Byte 2-3: 最大充电电流限制 (0.1A, short, LE)
        /// Byte 4-5: 最大放电电流限制 (0.1A, short, LE)
        /// Byte 6-7: 放电截止电压 (0.1V, ushort, LE)
        /// </summary>
        public static CanFrame Build0x351Frame(BmsDataModel model, out string summary)
        {
            byte[] data = new byte[8];
            ushort cv = 0;
            short maxChgI = 0;
            short maxDisI = 0;
            ushort cutoff = 0;

            lock (model.SyncRoot)
            {
                cv = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.CVVoltage * 10.0)));
                // 若充放电禁止或保护跳闸，限流归零
                double effChgI = model.StatusChargeEnable ? model.MaxChargeCurrent : 0.0;
                double effDisI = model.StatusDischargeEnable ? model.MaxDischargeCurrent : 0.0;

                maxChgI = (short)Math.Max(0, Math.Min(32767, Math.Round(effChgI * 10.0)));
                maxDisI = (short)Math.Max(0, Math.Min(32767, Math.Round(effDisI * 10.0)));
                cutoff = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.CutoffVoltage * 10.0)));

                data[0] = (byte)(cv & 0xFF);
                data[1] = (byte)((cv >> 8) & 0xFF);
                data[2] = (byte)(maxChgI & 0xFF);
                data[3] = (byte)((maxChgI >> 8) & 0xFF);
                data[4] = (byte)(maxDisI & 0xFF);
                data[5] = (byte)((maxDisI >> 8) & 0xFF);
                data[6] = (byte)(cutoff & 0xFF);
                data[7] = (byte)((cutoff >> 8) & 0xFF);
            }

            summary = string.Format("[HANCHU 0x351] 充放电管理 -> 推荐充电:{0:F1}V, 最大充电:{1:F1}A, 最大放电:{2:F1}A, 放电截止:{3:F1}V",
                cv * 0.1, maxChgI * 0.1, maxDisI * 0.1, cutoff * 0.1);

            return new CanFrame(ID_CHARGE_DISCHARGE_MGMT, data, false, false);
        }

        /// <summary>
        /// 0x355: SOC (% 0-100), SOH (% 0-100), Max Cell voltage (1mV), Min Cell voltage (1mV)
        /// Byte 0-1: SOC (% 0-100, ushort, LE)
        /// Byte 2-3: SOH (% 0-100, ushort, LE)
        /// Byte 4-5: Max Cell voltage (1mV, ushort, LE)
        /// Byte 6-7: Min Cell voltage (1mV, ushort, LE)
        /// </summary>
        public static CanFrame Build0x355Frame(BmsDataModel model, out string summary)
        {
            byte[] data = new byte[8];
            ushort soc = 0;
            ushort soh = 0;
            ushort maxCellMv = 0;
            ushort minCellMv = 0;

            lock (model.SyncRoot)
            {
                soc = (ushort)Math.Max(0, Math.Min(100, Math.Round(model.SOC)));
                soh = (ushort)Math.Max(0, Math.Min(100, Math.Round(model.SOH)));

                int cellCount = (model.Voltage > 35.0) ? 16 : 8;
                double avgMv = (model.Voltage / cellCount) * 1000.0;
                double diff = model.WarnVoltDiff ? 160.0 : 8.0;

                maxCellMv = (ushort)Math.Max(500, Math.Min(5000, Math.Round(avgMv + diff)));
                minCellMv = (ushort)Math.Max(500, Math.Min(5000, Math.Round(avgMv - diff)));

                data[0] = (byte)(soc & 0xFF);
                data[1] = (byte)((soc >> 8) & 0xFF);
                data[2] = (byte)(soh & 0xFF);
                data[3] = (byte)((soh >> 8) & 0xFF);
                data[4] = (byte)(maxCellMv & 0xFF);
                data[5] = (byte)((maxCellMv >> 8) & 0xFF);
                data[6] = (byte)(minCellMv & 0xFF);
                data[7] = (byte)((minCellMv >> 8) & 0xFF);
            }

            summary = string.Format("[HANCHU 0x355] 电量状态 -> SOC:{0}%, SOH:{1}%, 最高单体:{2}mV, 最低单体:{3}mV",
                soc, soh, maxCellMv, minCellMv);

            return new CanFrame(ID_SOC_SOH_CELL_VOLT, data, false, false);
        }

        /// <summary>
        /// 0x356: Pack voltage (0.01V), Pack current (0.1A, signed), Max Cell temp (0.1℃, signed), Min Cell temp (0.1℃, signed)
        /// Byte 0-1: Pack voltage (0.01V, short, LE)
        /// Byte 2-3: Pack current (0.1A, signed short, LE)
        /// Byte 4-5: Max Cell temp (0.1℃, signed short, LE)
        /// Byte 6-7: Min Cell temp (0.1℃, signed short, LE)
        /// </summary>
        public static CanFrame Build0x356Frame(BmsDataModel model, out string summary)
        {
            byte[] data = new byte[8];
            short voltRaw = 0;
            short currRaw = 0;
            short maxTempRaw = 0;
            short minTempRaw = 0;

            lock (model.SyncRoot)
            {
                voltRaw = (short)Math.Max(-32768, Math.Min(32767, Math.Round(model.Voltage * 100.0)));
                currRaw = (short)Math.Max(-32768, Math.Min(32767, Math.Round(model.Current * 10.0)));
                maxTempRaw = (short)Math.Max(-32768, Math.Min(32767, Math.Round(model.Temperature * 10.0)));
                minTempRaw = (short)Math.Max(-32768, Math.Min(32767, Math.Round((model.Temperature - 0.6) * 10.0)));

                data[0] = (byte)(voltRaw & 0xFF);
                data[1] = (byte)((voltRaw >> 8) & 0xFF);
                data[2] = (byte)(currRaw & 0xFF);
                data[3] = (byte)((currRaw >> 8) & 0xFF);
                data[4] = (byte)(maxTempRaw & 0xFF);
                data[5] = (byte)((maxTempRaw >> 8) & 0xFF);
                data[6] = (byte)(minTempRaw & 0xFF);
                data[7] = (byte)((minTempRaw >> 8) & 0xFF);
            }

            summary = string.Format("[HANCHU 0x356] 遥测数据 -> 总压:{0:F2}V, 电流:{1:F1}A, 最高温:{2:F1}℃, 最低温:{3:F1}℃",
                voltRaw * 0.01, currRaw * 0.1, maxTempRaw * 0.1, minTempRaw * 0.1);

            return new CanFrame(ID_PACK_VOLT_CURR_TEMP, data, false, false);
        }

        /// <summary>
        /// 0x359: 故障保护与告警字
        /// Byte 0: Protection 1 (过温、欠温、欠压、过压、放电过流、系统故障、短路、充电过流)
        /// Byte 1: Protection 2 (充电过温、充电欠温、组端欠压、组端过压、软起动故障)
        /// Byte 2: Alarm 1 (高温、低温、欠压、过压、放电过流、压差、充电过流)
        /// Byte 3: Alarm 2 (组端高温、组端低温、低电量、组端欠压、组端过压)
        /// Byte 4: Module number (模块/电芯数量, 默认 0x01)
        /// Byte 5: Reserved (0x00)
        /// Byte 6: Capacity Ah (0.1Ah 或 1Ah, LE)
        /// Byte 7: Capacity High Byte (0x00)
        /// </summary>
        public static CanFrame Build0x359Frame(BmsDataModel model, out string summary)
        {
            byte[] data = new byte[8];
            string statusTag = "";

            lock (model.SyncRoot)
            {
                byte p1 = 0;
                if (model.ProtHighTemp) p1 |= (1 << 0);
                if (model.ProtUnderTemp) p1 |= (1 << 1);
                if (model.ProtUnderVolt) p1 |= (1 << 2);
                if (model.ProtOverVolt) p1 |= (1 << 3);
                if (model.ProtOverCurrent && model.Current < 0) p1 |= (1 << 4);
                if (model.ProtSystemFault) p1 |= (1 << 5);
                if (model.ProtShortCircuit) p1 |= (1 << 6);
                if (model.ProtOverCurrent && model.Current >= 0) p1 |= (1 << 7);
                else if (model.ProtOverCurrent) p1 |= (1 << 7);

                byte p2 = 0;
                if (model.ProtHighTemp) p2 |= (1 << 0);
                if (model.ProtUnderTemp) p2 |= (1 << 1);
                if (model.ProtUnderVolt) p2 |= (1 << 2);
                if (model.ProtOverVolt) p2 |= (1 << 3);
                if (model.ProtSoftStart) p2 |= (1 << 4);

                byte a1 = 0;
                if (model.WarnHighTemp) a1 |= (1 << 0);
                if (model.WarnLowTemp) a1 |= (1 << 1);
                if (model.WarnSingleUnderVolt || model.WarnGlobalUnderVolt) a1 |= (1 << 2);
                if (model.WarnSingleOverVolt || model.WarnGlobalOverVolt) a1 |= (1 << 3);
                if (model.WarnOverCurrent && model.Current < 0) a1 |= (1 << 4);
                if (model.WarnVoltDiff) a1 |= (1 << 5);
                if (model.WarnOverCurrent && model.Current >= 0) a1 |= (1 << 7);
                else if (model.WarnOverCurrent) a1 |= (1 << 7);

                byte a2 = 0;
                if (model.WarnHighTemp) a2 |= (1 << 0);
                if (model.WarnLowTemp) a2 |= (1 << 1);
                if (model.WarnLowCapacity) a2 |= (1 << 2);
                if (model.WarnGlobalUnderVolt) a2 |= (1 << 3);
                if (model.WarnGlobalOverVolt) a2 |= (1 << 4);

                byte modNum = 1;
                ushort capAh = (ushort)Math.Max(0, Math.Min(65535, Math.Round(model.RemainingCapacity)));

                data[0] = p1;
                data[1] = p2;
                data[2] = a1;
                data[3] = a2;
                data[4] = modNum;
                data[5] = 0x00;
                data[6] = (byte)(capAh & 0xFF);
                data[7] = (byte)((capAh >> 8) & 0xFF);

                if (model.HasProtection) statusTag += string.Format(" [保护跳闸: {0}]", model.GetActiveProtectionsSummary());
                if (model.HasWarning) statusTag += string.Format(" [告警提示: {0}]", model.GetActiveWarningsSummary());

                summary = string.Format("[HANCHU 0x359] 告警保护 -> 保护字:0x{0:X2}{1:X2}, 告警字:0x{2:X2}{3:X2}, 容量:{4:F1}Ah{5}",
                    p1, p2, a1, a2, model.RemainingCapacity, statusTag);
            }

            return new CanFrame(ID_ALARM_PROTECTION, data, false, false);
        }

        /// <summary>
        /// 0x35C: 请求标志与状态
        /// Byte 0: Bit 7 充使能, Bit 6 放使能, Bit 5/4 强充请求, Bit 3 满充校准请求
        /// Byte 1: 0x00
        /// Byte 2-3: 循环次数 (ushort, LE)
        /// Byte 4-7: 00 00 00 00
        /// </summary>
        public static CanFrame Build0x35CFrame(BmsDataModel model, out string summary)
        {
            byte[] data = new byte[8];
            ushort cycles = 0;
            bool chgEn = false, disEn = false, forceChg = false, fullChg = false;

            lock (model.SyncRoot)
            {
                chgEn = model.StatusChargeEnable;
                disEn = model.StatusDischargeEnable;
                forceChg = model.StatusForceCharge;
                fullChg = model.StatusFullCharge;
                cycles = (ushort)Math.Max(0, Math.Min(65535, model.CycleCount));

                byte b0 = 0;
                if (chgEn) b0 |= (1 << 7);
                if (disEn) b0 |= (1 << 6);
                if (forceChg) b0 |= (3 << 4); // Bit 5 & Bit 4 强充请求
                if (fullChg) b0 |= (1 << 3);  // Bit 3 满充校准请求

                data[0] = b0;
                data[1] = 0x00;
                data[2] = (byte)(cycles & 0xFF);
                data[3] = (byte)((cycles >> 8) & 0xFF);
                data[4] = 0x00;
                data[5] = 0x00;
                data[6] = 0x00;
                data[7] = 0x00;
            }

            summary = string.Format("[HANCHU 0x35C] 状态请求 -> 充使能:{0}, 放使能:{1}, 强充:{2}, 满充校准:{3}, 循环:{4}次",
                chgEn ? "ON" : "OFF", disEn ? "ON" : "OFF", forceChg ? "ON" : "OFF", fullChg ? "ON" : "OFF", cycles);

            return new CanFrame(ID_REQUEST_FLAGS_STATUS, data, false, false);
        }

        /// <summary>
        /// 0x35E: 厂商名称 ASCII "HANCHU" (8字节: 'H','A','N','C','H','U',0,0)
        /// </summary>
        public static CanFrame Build0x35EFrame(out string summary)
        {
            byte[] data = new byte[] { (byte)'H', (byte)'A', (byte)'N', (byte)'C', (byte)'H', (byte)'U', 0x00, 0x00 };
            summary = "[HANCHU 0x35E] 厂商标识 -> \"HANCHU\"";
            return new CanFrame(ID_MANUFACTURER_NAME, data, false, false);
        }

        /// <summary>
        /// 从机广播模式：获取全部 6 帧周期广播数据包
        /// </summary>
        public static List<KeyValuePair<CanFrame, string>> BuildSlaveBroadcastFrames(BmsDataModel model)
        {
            List<KeyValuePair<CanFrame, string>> list = new List<KeyValuePair<CanFrame, string>>();
            string s1, s2, s3, s4, s5, s6;

            list.Add(new KeyValuePair<CanFrame, string>(Build0x351Frame(model, out s1), s1));
            list.Add(new KeyValuePair<CanFrame, string>(Build0x355Frame(model, out s2), s2));
            list.Add(new KeyValuePair<CanFrame, string>(Build0x356Frame(model, out s3), s3));
            list.Add(new KeyValuePair<CanFrame, string>(Build0x359Frame(model, out s4), s4));
            list.Add(new KeyValuePair<CanFrame, string>(Build0x35CFrame(model, out s5), s5));
            list.Add(new KeyValuePair<CanFrame, string>(Build0x35EFrame(out s6), s6));

            return list;
        }

        /// <summary>
        /// 主机/从机通用：解析接收到的 CAN 报文，并更新至 model
        /// </summary>
        public static bool TryDecodeFrame(CanFrame frame, BmsDataModel model, out string decodedSummary)
        {
            decodedSummary = "";
            if (frame == null) return false;

            if (frame.Id == ID_INVERTER_HANDSHAKE)
            {
                decodedSummary = "[HANCHU 0x305] 逆变器握手/保活响应帧 (8字节全00)";
                return true;
            }

            if (frame.Id == ID_CHARGE_DISCHARGE_MGMT && frame.Dlc >= 8)
            {
                ushort cvRaw = (ushort)(frame.Data[0] | (frame.Data[1] << 8));
                short chgIRaw = (short)(frame.Data[2] | (frame.Data[3] << 8));
                short disIRaw = (short)(frame.Data[4] | (frame.Data[5] << 8));
                ushort cutoffRaw = (ushort)(frame.Data[6] | (frame.Data[7] << 8));

                lock (model.SyncRoot)
                {
                    model.CVVoltage = cvRaw * 0.1;
                    model.MaxChargeCurrent = chgIRaw * 0.1;
                    model.MaxDischargeCurrent = disIRaw * 0.1;
                    model.CutoffVoltage = cutoffRaw * 0.1;
                }

                decodedSummary = string.Format("[HANCHU 0x351] 充放电管理 -> CV:{0:F1}V, 最大充电:{1:F1}A, 最大放电:{2:F1}A, 截止:{3:F1}V",
                    cvRaw * 0.1, chgIRaw * 0.1, disIRaw * 0.1, cutoffRaw * 0.1);
                return true;
            }

            if (frame.Id == ID_SOC_SOH_CELL_VOLT && frame.Dlc >= 8)
            {
                ushort soc = (ushort)(frame.Data[0] | (frame.Data[1] << 8));
                ushort soh = (ushort)(frame.Data[2] | (frame.Data[3] << 8));
                ushort maxCell = (ushort)(frame.Data[4] | (frame.Data[5] << 8));
                ushort minCell = (ushort)(frame.Data[6] | (frame.Data[7] << 8));

                lock (model.SyncRoot)
                {
                    model.SOC = soc;
                    model.SOH = soh;
                }

                decodedSummary = string.Format("[HANCHU 0x355] 电量状态 -> SOC:{0}%, SOH:{1}%, 最高单体:{2}mV, 最低单体:{3}mV",
                    soc, soh, maxCell, minCell);
                return true;
            }

            if (frame.Id == ID_PACK_VOLT_CURR_TEMP && frame.Dlc >= 8)
            {
                short voltRaw = (short)(frame.Data[0] | (frame.Data[1] << 8));
                short currRaw = (short)(frame.Data[2] | (frame.Data[3] << 8));
                short maxTempRaw = (short)(frame.Data[4] | (frame.Data[5] << 8));
                short minTempRaw = (short)(frame.Data[6] | (frame.Data[7] << 8));

                lock (model.SyncRoot)
                {
                    model.Voltage = voltRaw * 0.01;
                    model.Current = currRaw * 0.1;
                    model.Temperature = maxTempRaw * 0.1;
                }

                decodedSummary = string.Format("[HANCHU 0x356] 遥测数据 -> 总压:{0:F2}V, 电流:{1:F1}A, 最高温:{2:F1}℃, 最低温:{3:F1}℃",
                    voltRaw * 0.01, currRaw * 0.1, maxTempRaw * 0.1, minTempRaw * 0.1);
                return true;
            }

            if (frame.Id == ID_ALARM_PROTECTION && frame.Dlc >= 7)
            {
                byte p1 = frame.Data[0];
                byte p2 = frame.Data[1];
                byte a1 = frame.Data[2];
                byte a2 = frame.Data[3];
                byte modNum = frame.Data[4];
                double cap = frame.Data[6] | (frame.Dlc >= 8 ? (frame.Data[7] << 8) : 0);

                lock (model.SyncRoot)
                {
                    model.ProtHighTemp = (p1 & 0x01) != 0 || (p2 & 0x01) != 0;
                    model.ProtUnderTemp = (p1 & 0x02) != 0 || (p2 & 0x02) != 0;
                    model.ProtUnderVolt = (p1 & 0x04) != 0 || (p2 & 0x04) != 0;
                    model.ProtOverVolt = (p1 & 0x08) != 0 || (p2 & 0x08) != 0;
                    model.ProtOverCurrent = (p1 & 0x10) != 0 || (p1 & 0x80) != 0;
                    model.ProtSystemFault = (p1 & 0x20) != 0;
                    model.ProtShortCircuit = (p1 & 0x40) != 0;
                    model.ProtSoftStart = (p2 & 0x10) != 0;

                    model.WarnHighTemp = (a1 & 0x01) != 0 || (a2 & 0x01) != 0;
                    model.WarnLowTemp = (a1 & 0x02) != 0 || (a2 & 0x02) != 0;
                    model.WarnSingleUnderVolt = (a1 & 0x04) != 0;
                    model.WarnSingleOverVolt = (a1 & 0x08) != 0;
                    model.WarnGlobalUnderVolt = (a2 & 0x08) != 0;
                    model.WarnGlobalOverVolt = (a2 & 0x10) != 0;
                    model.WarnOverCurrent = (a1 & 0x10) != 0 || (a1 & 0x80) != 0;
                    model.WarnVoltDiff = (a1 & 0x20) != 0;
                    model.WarnLowCapacity = (a2 & 0x04) != 0;

                    if (cap > 0) model.RemainingCapacity = cap;
                }

                string statusTag = "";
                if (model.HasProtection) statusTag += string.Format(" [保护跳闸: {0}]", model.GetActiveProtectionsSummary());
                if (model.HasWarning) statusTag += string.Format(" [告警提示: {0}]", model.GetActiveWarningsSummary());

                decodedSummary = string.Format("[HANCHU 0x359] 告警保护 -> 保护字:0x{0:X2}{1:X2}, 告警字:0x{2:X2}{3:X2}, 模块数:{4}, 容量:{5:F1}Ah{6}",
                    p1, p2, a1, a2, modNum, model.RemainingCapacity, statusTag);
                return true;
            }

            if (frame.Id == ID_REQUEST_FLAGS_STATUS && frame.Dlc >= 4)
            {
                byte b0 = frame.Data[0];
                ushort cycles = (ushort)(frame.Data[2] | (frame.Data[3] << 8));

                lock (model.SyncRoot)
                {
                    model.StatusChargeEnable = (b0 & 0x80) != 0;
                    model.StatusDischargeEnable = (b0 & 0x40) != 0;
                    model.StatusForceCharge = (b0 & 0x30) != 0;
                    model.StatusFullCharge = (b0 & 0x08) != 0;
                    model.CycleCount = cycles;
                }

                decodedSummary = string.Format("[HANCHU 0x35C] 状态请求 -> 充使能:{0}, 放使能:{1}, 强充:{2}, 满充校准:{3}, 循环:{4}次",
                    model.StatusChargeEnable ? "ON" : "OFF", model.StatusDischargeEnable ? "ON" : "OFF",
                    model.StatusForceCharge ? "ON" : "OFF", model.StatusFullCharge ? "ON" : "OFF", cycles);
                return true;
            }

            if (frame.Id == ID_MANUFACTURER_NAME && frame.Dlc >= 6)
            {
                string vendor = Encoding.ASCII.GetString(frame.Data, 0, Math.Min((int)frame.Dlc, 8)).TrimEnd('\0', ' ');
                decodedSummary = string.Format("[HANCHU 0x35E] 厂商标识 -> \"{0}\"", vendor);
                return true;
            }

            decodedSummary = string.Format("[CAN 0x{0:X3}] 未知/扩展报文 数据:{1}", frame.Id, frame.ToHexString());
            return true;
        }
    }
}
