using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace BMS_Protocol_Simulator
{
    public enum BmsProtocolType
    {
        CVTE,
        GROWATT_MODBUS,
        VOLTRONIC_MODBUS,
        PYLONTECH_ASCII,
        LIB_MODBUS,
        JBD
    }

    public enum WorkMode
    {
        SlaveSimulator,     // 模拟电池包响应逆变器查询
        MasterPoller        // 模拟逆变器主动轮询电池包
    }

    public enum SlaveFaultInjectionMode
    {
        None = 0,
        ShengyangInvalidNonCritical = 1, // 圣阳非关键回退字段置 0xFFFF (保留总压 0x1000 与 SOC 0x1008)
        ShengyangInvalidCritical = 2,    // 圣阳关键总压 0x1000 与 SOC 0x1008 置 0xFFFF (测试拒收保护)
        JbdForceUnit100mA = 3            // JBD 强制启用 FET Bit7=1 (100mA / 100mAh 分支)
    }

    public class BmsParameterProfile
    {
        public string ProfileName { get; set; }
        public int CellCount { get; set; }
        public double Voltage { get; set; }
        public double Current { get; set; }
        public double Temperature { get; set; }
        public double SOC { get; set; }
        public double SOH { get; set; }
        public double RemainingCapacity { get; set; }
        public double FullCapacity { get; set; }
        public double MaxChargeCurrent { get; set; }
        public double MaxDischargeCurrent { get; set; }
        public double CVVoltage { get; set; }
        public double CutoffVoltage { get; set; }
        public int CycleCount { get; set; }

        public BmsParameterProfile(string name = "6KU")
        {
            if (string.Equals(name, "3KU", StringComparison.OrdinalIgnoreCase))
            {
                ProfileName = "3KU";
                CellCount = 8;
                Voltage = 28.00;
                Current = 0.00;
                Temperature = 28.0;
                SOC = 100.0;
                SOH = 100.0;
                RemainingCapacity = 100.0;
                FullCapacity = 100.0;
                MaxChargeCurrent = 100.0;
                MaxDischargeCurrent = 100.0;
                CVVoltage = 28.80;
                CutoffVoltage = 21.00;
                CycleCount = 50;
            }
            else // 默认 6KU (48V 体系)
            {
                ProfileName = "6KU";
                CellCount = 16;
                Voltage = 53.00;
                Current = 0.00;
                Temperature = 28.0;
                SOC = 100.0;
                SOH = 100.0;
                RemainingCapacity = 100.0;
                FullCapacity = 100.0;
                MaxChargeCurrent = 100.0;
                MaxDischargeCurrent = 100.0;
                CVVoltage = 56.00;
                CutoffVoltage = 42.00;
                CycleCount = 50;
            }
        }

        public BmsParameterProfile Clone()
        {
            return new BmsParameterProfile(ProfileName)
            {
                CellCount = this.CellCount,
                Voltage = this.Voltage,
                Current = this.Current,
                Temperature = this.Temperature,
                SOC = this.SOC,
                SOH = this.SOH,
                RemainingCapacity = this.RemainingCapacity,
                FullCapacity = this.FullCapacity,
                MaxChargeCurrent = this.MaxChargeCurrent,
                MaxDischargeCurrent = this.MaxDischargeCurrent,
                CVVoltage = this.CVVoltage,
                CutoffVoltage = this.CutoffVoltage,
                CycleCount = this.CycleCount
            };
        }
    }

    public class BmsDataModel
    {
        private readonly object _lock = new object();

        // ── 基础模拟量 ──
        public double Voltage { get; set; }           // 电压 (V)
        public double Current { get; set; }            // 电流 (A, 正为充电, 负为放电)
        public double Temperature { get; set; }        // 温度 (°C)
        public double SOC { get; set; }               // SOC (0~100%)
        public double SOH { get; set; }               // SOH (0~100%)
        public double RemainingCapacity { get; set; } // 剩余容量 (Ah)
        public double FullCapacity { get; set; }      // 满充总容量 (Ah)
        public double MaxChargeCurrent { get; set; }  // 最大充电电流限制 (A)
        public double MaxDischargeCurrent { get; set; }// 最大放电电流限制 (A)
        public double CVVoltage { get; set; }         // 恒压充电 CV 点 (V)
        public double CutoffVoltage { get; set; }     // 放电截止电压点 (V)
        public int CycleCount { get; set; }           // 电池循环次数 (次)

        // ── 系统状态位 ──
        public bool StatusChargeEnable { get; set; }    // 充电使能 (Bit 7)
        public bool StatusDischargeEnable { get; set; } // 放电使能 (Bit 6)
        public bool StatusForceCharge { get; set; }    // 强制充电请求 (Bit 5)
        public bool StatusFullCharge { get; set; }     // 请求满充 (Bit 4)
        public bool StatusBalancing { get; set; }      // 均衡状态
        public bool StatusSleep { get; set; }          // 休眠状态

        // ── 警告位 (Warning Bits) ──
        public bool WarnSingleOverVolt { get; set; }   // 单体过压告警
        public bool WarnSingleUnderVolt { get; set; }  // 单体欠压告警
        public bool WarnGlobalOverVolt { get; set; }   // 组端过压告警
        public bool WarnGlobalUnderVolt { get; set; }  // 组端欠压告警
        public bool WarnOverCurrent { get; set; }      // 充放过流告警
        public bool WarnHighTemp { get; set; }         // 高温告警
        public bool WarnLowTemp { get; set; }          // 低温告警
        public bool WarnVoltDiff { get; set; }         // 压差告警
        public bool WarnLowCapacity { get; set; }      // 低电量告警

        // ── 故障保护位 (Error / Protection Bits) ──
        public bool ProtOverVolt { get; set; }         // 过压保护
        public bool ProtUnderVolt { get; set; }        // 欠压保护
        public bool ProtOverCurrent { get; set; }      // 过流保护
        public bool ProtShortCircuit { get; set; }     // 短路保护
        public bool ProtHighTemp { get; set; }         // 高温保护
        public bool ProtUnderTemp { get; set; }        // 低温保护
        public bool ProtSystemFault { get; set; }      // 系统内部故障
        public bool ProtSoftStart { get; set; }        // 软起动故障

        public bool HasProtection
        {
            get
            {
                return ProtOverVolt || ProtUnderVolt || ProtOverCurrent || ProtShortCircuit ||
                       ProtHighTemp || ProtUnderTemp || ProtSystemFault || ProtSoftStart;
            }
        }

        public bool CanCharge
        {
            get
            {
                return StatusChargeEnable && !ProtOverVolt && !ProtOverCurrent && !ProtShortCircuit &&
                       !ProtHighTemp && !ProtUnderTemp && !ProtSystemFault && !ProtSoftStart;
            }
        }

        public bool CanDischarge
        {
            get
            {
                return StatusDischargeEnable && !ProtUnderVolt && !ProtOverCurrent && !ProtShortCircuit &&
                       !ProtHighTemp && !ProtUnderTemp && !ProtSystemFault && !ProtSoftStart;
            }
        }

        public bool HasWarning
        {
            get
            {
                return WarnSingleOverVolt || WarnSingleUnderVolt || WarnGlobalOverVolt || WarnGlobalUnderVolt ||
                       WarnOverCurrent || WarnHighTemp || WarnLowTemp || WarnVoltDiff || WarnLowCapacity;
            }
        }

        public string GetActiveProtectionsSummary()
        {
            List<string> list = new List<string>();
            if (ProtOverVolt) list.Add("过压保护");
            if (ProtUnderVolt) list.Add("欠压保护");
            if (ProtOverCurrent) list.Add("过流保护");
            if (ProtShortCircuit) list.Add("短路保护");
            if (ProtHighTemp) list.Add("过温保护");
            if (ProtUnderTemp) list.Add("欠温保护");
            if (ProtSystemFault) list.Add("系统故障");
            if (ProtSoftStart) list.Add("软起动失败");
            return string.Join(",", list.ToArray());
        }

        public string GetActiveWarningsSummary()
        {
            List<string> list = new List<string>();
            if (WarnSingleOverVolt) list.Add("单体过压告警");
            if (WarnSingleUnderVolt) list.Add("单体欠压告警");
            if (WarnGlobalOverVolt) list.Add("总压过压告警");
            if (WarnGlobalUnderVolt) list.Add("总压欠压告警");
            if (WarnOverCurrent) list.Add("过流告警");
            if (WarnHighTemp) list.Add("高温告警");
            if (WarnLowTemp) list.Add("低温告警");
            if (WarnVoltDiff) list.Add("压差过大");
            if (WarnLowCapacity) list.Add("低电量告警");
            return string.Join(",", list.ToArray());
        }

        public object SyncRoot
        {
            get { return _lock; }
        }


        // ── 扩展仿真参数：单芯压差拉偏 (mV)、显式串数与从机容错注入模式 ──
        public int CellImbalanceMv { get; set; }       // 单体最高与最低电芯压差设定 (mV, 如 0 / 50 / 150 / 300)
        public int CellCount { get; set; }             // 显式电池串数 (8 / 15 / 16)
        public SlaveFaultInjectionMode FaultInjection { get; set; }

        public int GetEffectiveCellCount()
        {
            if (CellCount == 8 || CellCount == 15 || CellCount == 16) return CellCount;
            return string.Equals(ActiveProfileName, "3KU", StringComparison.OrdinalIgnoreCase) ? 8 : 16;
        }

        public ushort GetCellMillivolts(int cellIndexZeroBased, int cellCount, out ushort maxCellMv, out ushort minCellMv)
        {
            if (cellCount <= 0) cellCount = GetEffectiveCellCount();
            int avgMv = (int)Math.Round((Voltage * 1000.0) / cellCount);
            int effDelta = CellImbalanceMv;
            if (effDelta <= 0) effDelta = WarnVoltDiff ? 240 : 20; // 勾选压差告警时自动注入 240mV 极值压差，常态保持 20mV 自然微差
            int half = effDelta / 2;
            maxCellMv = (ushort)Math.Max(0, Math.Min(5000, avgMv + half));
            minCellMv = (ushort)Math.Max(0, Math.Min(5000, avgMv - half));
            if (cellIndexZeroBased == 0) return maxCellMv;
            if (cellIndexZeroBased == 1 && cellCount >= 2) return minCellMv;
            return (ushort)Math.Max(0, Math.Min(5000, avgMv));
        }

        public void ClearAllAlarmsAndProtections()
        {
            WarnSingleOverVolt = false;
            WarnSingleUnderVolt = false;
            WarnGlobalOverVolt = false;
            WarnGlobalUnderVolt = false;
            WarnOverCurrent = false;
            WarnHighTemp = false;
            WarnLowTemp = false;
            WarnVoltDiff = false;
            WarnLowCapacity = false;

            ProtOverVolt = false;
            ProtUnderVolt = false;
            ProtOverCurrent = false;
            ProtShortCircuit = false;
            ProtHighTemp = false;
            ProtUnderTemp = false;
            ProtSystemFault = false;
            ProtSoftStart = false;
        }

        public string ActiveProfileName { get; private set; }

        public void ApplyProfile(string profileName)
        {
            string target = (profileName != null && profileName.ToUpperInvariant().Contains("3KU")) ? "3KU" : "6KU";
            ApplyDefaultConfig(new BmsParameterProfile(target));
        }

        public BmsDataModel()
        {
            FaultInjection = SlaveFaultInjectionMode.None;
            ApplyProfile("6KU");
        }

        public void ApplyDefaultConfig(BmsParameterProfile profile)
        {
            if (profile == null) return;
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(profile.ProfileName)) ActiveProfileName = profile.ProfileName;
                CellCount = (profile.CellCount == 8 || profile.CellCount == 15 || profile.CellCount == 16)
                    ? profile.CellCount
                    : (string.Equals(ActiveProfileName, "3KU", StringComparison.OrdinalIgnoreCase) ? 8 : 16);

                Voltage = profile.Voltage;
                Current = profile.Current;
                Temperature = profile.Temperature;
                SOC = profile.SOC;
                SOH = profile.SOH;
                RemainingCapacity = profile.RemainingCapacity;
                FullCapacity = profile.FullCapacity;
                MaxChargeCurrent = profile.MaxChargeCurrent;
                MaxDischargeCurrent = profile.MaxDischargeCurrent;
                CVVoltage = profile.CVVoltage;
                CutoffVoltage = profile.CutoffVoltage;
                CycleCount = profile.CycleCount;

                ClearAllAlarmsAndProtections();

                StatusChargeEnable = true;
                StatusDischargeEnable = true;
                StatusForceCharge = false;
                StatusFullCharge = false;
                StatusBalancing = false;
                StatusSleep = false;
            }
        }
    }

    public class SimulatorDefaultsConfig
    {
        public string Protocol { get; set; }
        public string Profile { get; set; }
        public int CellCount { get; set; }
        public int BaudRate { get; set; }
        public bool AutoAdapt { get; set; }
        public bool ReplyAllIds { get; set; }
        public int SpecificId { get; set; }

        public SimulatorDefaultsConfig()
        {
            Protocol = "CVTE";
            Profile = "6KU";
            CellCount = 16;
            BaudRate = 9600;
            AutoAdapt = false;
            ReplyAllIds = true;
            SpecificId = 1;
        }

        public static string DefaultFilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bms_default_config.ini"); }
        }

        public static int IndexFromProtocolName(string protocolName)
        {
            if (string.IsNullOrEmpty(protocolName)) return 0;
            string p = protocolName.Trim().ToUpperInvariant();
            if (p == "GROWATT" || p == "GROWATT_MODBUS") return 1;
            if (p == "VOLTRONIC" || p == "VOLTRONIC_MODBUS") return 2;
            if (p == "PYLONTECH" || p == "PYLONTECH_ASCII") return 3;
            if (p == "SHENGYANG" || p == "LIB_MODBUS") return 4;
            if (p == "JBD") return 5;
            return 0;
        }

        public static string ProtocolNameFromIndex(int index)
        {
            switch (index)
            {
                case 1: return "GROWATT";
                case 2: return "VOLTRONIC";
                case 3: return "PYLONTECH";
                case 4: return "SHENGYANG";
                case 5: return "JBD";
                default: return "CVTE";
            }
        }

        private static bool IsValidBaud(int baud)
        {
            return baud == 2400 || baud == 4800 || baud == 9600 || baud == 19200 ||
                   baud == 38400 || baud == 57600 || baud == 115200;
        }

        public static SimulatorDefaultsConfig Load(string filePath)
        {
            SimulatorDefaultsConfig cfg = new SimulatorDefaultsConfig();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return cfg;

            try
            {
                string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = (lines[i] ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim().ToUpperInvariant();
                    string val = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "PROTOCOL":
                            cfg.Protocol = ProtocolNameFromIndex(IndexFromProtocolName(val));
                            break;
                        case "PROFILE":
                            cfg.Profile = val.ToUpperInvariant().Contains("3KU") ? "3KU" : "6KU";
                            break;
                        case "CELLCOUNT":
                            int cells;
                            if (int.TryParse(val, out cells) && (cells == 8 || cells == 15 || cells == 16))
                                cfg.CellCount = cells;
                            else
                                cfg.CellCount = -1;
                            break;
                        case "BAUDRATE":
                            int baud;
                            if (int.TryParse(val, out baud) && IsValidBaud(baud))
                                cfg.BaudRate = baud;
                            break;
                        case "AUTOADAPT":
                            bool aa;
                            if (bool.TryParse(val, out aa)) cfg.AutoAdapt = aa;
                            break;
                        case "REPLYALLIDS":
                            bool ra;
                            if (bool.TryParse(val, out ra)) cfg.ReplyAllIds = ra;
                            break;
                        case "SPECIFICID":
                            int sid;
                            if (int.TryParse(val, out sid) && sid >= 0 && sid <= 15)
                                cfg.SpecificId = sid;
                            break;
                    }
                }
            }
            catch
            {
                return new SimulatorDefaultsConfig();
            }

            if (cfg.CellCount != 8 && cfg.CellCount != 15 && cfg.CellCount != 16)
            {
                cfg.CellCount = string.Equals(cfg.Profile, "3KU", StringComparison.OrdinalIgnoreCase) ? 8 : 16;
            }
            return cfg;
        }

        public static void Save(string filePath, SimulatorDefaultsConfig cfg)
        {
            if (string.IsNullOrEmpty(filePath) || cfg == null) return;
            try
            {
                int safeCells = (cfg.CellCount == 8 || cfg.CellCount == 15 || cfg.CellCount == 16)
                    ? cfg.CellCount
                    : (string.Equals(cfg.Profile, "3KU", StringComparison.OrdinalIgnoreCase) ? 8 : 16);
                int safeBaud = IsValidBaud(cfg.BaudRate) ? cfg.BaudRate : 9600;
                int safeId = Math.Max(0, Math.Min(15, cfg.SpecificId));
                string safeProfile = string.Equals(cfg.Profile, "3KU", StringComparison.OrdinalIgnoreCase) ? "3KU" : "6KU";
                string safeProtocol = ProtocolNameFromIndex(IndexFromProtocolName(cfg.Protocol));

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("[Defaults]");
                sb.AppendLine("Protocol=" + safeProtocol);
                sb.AppendLine("Profile=" + safeProfile);
                sb.AppendLine("CellCount=" + safeCells);
                sb.AppendLine("BaudRate=" + safeBaud);
                sb.AppendLine("AutoAdapt=" + (cfg.AutoAdapt ? "true" : "false"));
                sb.AppendLine("ReplyAllIds=" + (cfg.ReplyAllIds ? "true" : "false"));
                sb.AppendLine("SpecificId=" + safeId);

                File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }
}
