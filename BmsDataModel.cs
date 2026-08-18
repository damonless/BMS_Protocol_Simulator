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
        PYLONTECH_ASCII
    }

    public enum WorkMode
    {
        SlaveSimulator,     // 模拟电池包响应逆变器查询
        MasterPoller        // 模拟逆变器主动轮询电池包
    }

    public class BmsParameterProfile
    {
        public string ProfileName { get; set; }
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

        public BmsParameterProfile(string name = "3KU")
        {
            ProfileName = name;
            if (name == "6KU")
            {
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
            else // 默认 3KU (24V 体系)
            {
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
        }

        public BmsParameterProfile Clone()
        {
            return new BmsParameterProfile(ProfileName)
            {
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

    public class BmsDefaultConfig
    {
        public string ActiveProfileName { get; set; }
        public BmsParameterProfile Profile3KU { get; set; }
        public BmsParameterProfile Profile6KU { get; set; }

        public BmsParameterProfile ActiveProfile
        {
            get
            {
                if (ActiveProfileName == "6KU") return Profile6KU;
                return Profile3KU;
            }
        }

        public BmsDefaultConfig()
        {
            ActiveProfileName = "3KU";
            Profile3KU = new BmsParameterProfile("3KU");
            Profile6KU = new BmsParameterProfile("6KU");
        }

        public static string GetConfigFilePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bms_default_config.ini");
        }

        public static BmsDefaultConfig Load()
        {
            BmsDefaultConfig config = new BmsDefaultConfig();
            string path = GetConfigFilePath();
            if (!File.Exists(path))
            {
                config.Save();
                return config;
            }

            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                string currentSection = "";
                foreach (string line in lines)
                {
                    string trimLine = line.Trim();
                    if (string.IsNullOrEmpty(trimLine) || trimLine.StartsWith("#") || trimLine.StartsWith(";"))
                        continue;

                    if (trimLine.StartsWith("[") && trimLine.EndsWith("]"))
                    {
                        currentSection = trimLine.Substring(1, trimLine.Length - 2).Trim();
                        continue;
                    }

                    if (!trimLine.Contains("="))
                        continue;

                    string[] parts = trimLine.Split(new char[] { '=' }, 2);
                    string key = parts[0].Trim();
                    string val = parts[1].Trim();

                    if (key.Equals("ActiveProfile", StringComparison.OrdinalIgnoreCase))
                    {
                        config.ActiveProfileName = val.ToUpper().Contains("6KU") ? "6KU" : "3KU";
                        continue;
                    }

                    BmsParameterProfile targetProfile = currentSection.Equals("Profile_6KU", StringComparison.OrdinalIgnoreCase)
                        ? config.Profile6KU
                        : config.Profile3KU;

                    double dVal;
                    if (double.TryParse(val, out dVal))
                    {
                        if (key.Equals("Voltage", StringComparison.OrdinalIgnoreCase)) targetProfile.Voltage = dVal;
                        else if (key.Equals("Current", StringComparison.OrdinalIgnoreCase)) targetProfile.Current = dVal;
                        else if (key.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) targetProfile.Temperature = dVal;
                        else if (key.Equals("SOC", StringComparison.OrdinalIgnoreCase)) targetProfile.SOC = dVal;
                        else if (key.Equals("SOH", StringComparison.OrdinalIgnoreCase)) targetProfile.SOH = dVal;
                        else if (key.Equals("RemainingCapacity", StringComparison.OrdinalIgnoreCase)) targetProfile.RemainingCapacity = dVal;
                        else if (key.Equals("FullCapacity", StringComparison.OrdinalIgnoreCase)) targetProfile.FullCapacity = dVal;
                        else if (key.Equals("MaxChargeCurrent", StringComparison.OrdinalIgnoreCase)) targetProfile.MaxChargeCurrent = dVal;
                        else if (key.Equals("MaxDischargeCurrent", StringComparison.OrdinalIgnoreCase)) targetProfile.MaxDischargeCurrent = dVal;
                        else if (key.Equals("CVVoltage", StringComparison.OrdinalIgnoreCase)) targetProfile.CVVoltage = dVal;
                        else if (key.Equals("CutoffVoltage", StringComparison.OrdinalIgnoreCase)) targetProfile.CutoffVoltage = dVal;
                        else if (key.Equals("CycleCount", StringComparison.OrdinalIgnoreCase)) targetProfile.CycleCount = (int)Math.Round(dVal);
                    }
                }
            }
            catch { }

            return config;
        }

        public void Save()
        {
            try
            {
                string path = GetConfigFilePath();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# BMS Simulator Multi-Profile Default Parameters Config");
                sb.AppendLine("ActiveProfile=" + ActiveProfileName);
                sb.AppendLine();

                sb.AppendLine("[Profile_3KU]");
                sb.AppendLine(string.Format("Voltage={0:F2}", Profile3KU.Voltage));
                sb.AppendLine(string.Format("Current={0:F2}", Profile3KU.Current));
                sb.AppendLine(string.Format("Temperature={0:F1}", Profile3KU.Temperature));
                sb.AppendLine(string.Format("SOC={0:F1}", Profile3KU.SOC));
                sb.AppendLine(string.Format("SOH={0:F0}", Profile3KU.SOH));
                sb.AppendLine(string.Format("RemainingCapacity={0:F1}", Profile3KU.RemainingCapacity));
                sb.AppendLine(string.Format("FullCapacity={0:F1}", Profile3KU.FullCapacity));
                sb.AppendLine(string.Format("MaxChargeCurrent={0:F1}", Profile3KU.MaxChargeCurrent));
                sb.AppendLine(string.Format("MaxDischargeCurrent={0:F1}", Profile3KU.MaxDischargeCurrent));
                sb.AppendLine(string.Format("CVVoltage={0:F2}", Profile3KU.CVVoltage));
                sb.AppendLine(string.Format("CutoffVoltage={0:F2}", Profile3KU.CutoffVoltage));
                sb.AppendLine(string.Format("CycleCount={0}", Profile3KU.CycleCount));
                sb.AppendLine();

                sb.AppendLine("[Profile_6KU]");
                sb.AppendLine(string.Format("Voltage={0:F2}", Profile6KU.Voltage));
                sb.AppendLine(string.Format("Current={0:F2}", Profile6KU.Current));
                sb.AppendLine(string.Format("Temperature={0:F1}", Profile6KU.Temperature));
                sb.AppendLine(string.Format("SOC={0:F1}", Profile6KU.SOC));
                sb.AppendLine(string.Format("SOH={0:F0}", Profile6KU.SOH));
                sb.AppendLine(string.Format("RemainingCapacity={0:F1}", Profile6KU.RemainingCapacity));
                sb.AppendLine(string.Format("FullCapacity={0:F1}", Profile6KU.FullCapacity));
                sb.AppendLine(string.Format("MaxChargeCurrent={0:F1}", Profile6KU.MaxChargeCurrent));
                sb.AppendLine(string.Format("MaxDischargeCurrent={0:F1}", Profile6KU.MaxDischargeCurrent));
                sb.AppendLine(string.Format("CVVoltage={0:F2}", Profile6KU.CVVoltage));
                sb.AppendLine(string.Format("CutoffVoltage={0:F2}", Profile6KU.CutoffVoltage));
                sb.AppendLine(string.Format("CycleCount={0}", Profile6KU.CycleCount));

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
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

        public object SyncRoot
        {
            get { return _lock; }
        }

        public BmsDataModel()
        {
            ApplyDefaultConfig(BmsDefaultConfig.Load().ActiveProfile);
        }

        public void ApplyDefaultConfig(BmsDefaultConfig config)
        {
            if (config != null)
            {
                ApplyDefaultConfig(config.ActiveProfile);
            }
        }

        public void ApplyDefaultConfig(BmsParameterProfile profile)
        {
            if (profile == null) return;
            lock (_lock)
            {
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

                StatusChargeEnable = true;
                StatusDischargeEnable = true;
                StatusForceCharge = false;
                StatusFullCharge = false;
                StatusBalancing = false;
                StatusSleep = false;
            }
        }
    }
}
