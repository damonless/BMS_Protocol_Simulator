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

    public class BmsDefaultConfig
    {
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

        public BmsDefaultConfig()
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
                return config;
            }

            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") || !line.Contains("="))
                        continue;

                    string[] parts = line.Split(new char[] { '=' }, 2);
                    string key = parts[0].Trim();
                    string val = parts[1].Trim();
                    double dVal;

                    if (double.TryParse(val, out dVal))
                    {
                        if (key.Equals("Voltage", StringComparison.OrdinalIgnoreCase)) config.Voltage = dVal;
                        else if (key.Equals("Current", StringComparison.OrdinalIgnoreCase)) config.Current = dVal;
                        else if (key.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) config.Temperature = dVal;
                        else if (key.Equals("SOC", StringComparison.OrdinalIgnoreCase)) config.SOC = dVal;
                        else if (key.Equals("SOH", StringComparison.OrdinalIgnoreCase)) config.SOH = dVal;
                        else if (key.Equals("RemainingCapacity", StringComparison.OrdinalIgnoreCase)) config.RemainingCapacity = dVal;
                        else if (key.Equals("FullCapacity", StringComparison.OrdinalIgnoreCase)) config.FullCapacity = dVal;
                        else if (key.Equals("MaxChargeCurrent", StringComparison.OrdinalIgnoreCase)) config.MaxChargeCurrent = dVal;
                        else if (key.Equals("MaxDischargeCurrent", StringComparison.OrdinalIgnoreCase)) config.MaxDischargeCurrent = dVal;
                        else if (key.Equals("CVVoltage", StringComparison.OrdinalIgnoreCase)) config.CVVoltage = dVal;
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
                sb.AppendLine("# BMS Simulator Default Parameters Config");
                sb.AppendLine(string.Format("Voltage={0:F2}", Voltage));
                sb.AppendLine(string.Format("Current={0:F2}", Current));
                sb.AppendLine(string.Format("Temperature={0:F1}", Temperature));
                sb.AppendLine(string.Format("SOC={0:F1}", SOC));
                sb.AppendLine(string.Format("SOH={0:F0}", SOH));
                sb.AppendLine(string.Format("RemainingCapacity={0:F1}", RemainingCapacity));
                sb.AppendLine(string.Format("FullCapacity={0:F1}", FullCapacity));
                sb.AppendLine(string.Format("MaxChargeCurrent={0:F1}", MaxChargeCurrent));
                sb.AppendLine(string.Format("MaxDischargeCurrent={0:F1}", MaxDischargeCurrent));
                sb.AppendLine(string.Format("CVVoltage={0:F2}", CVVoltage));

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

        // ── 系统状态位 ──
        public bool StatusChargeEnable { get; set; }    // 充电使能
        public bool StatusDischargeEnable { get; set; } // 放电使能
        public bool StatusBalancing { get; set; }      // 均衡状态
        public bool StatusSleep { get; set; }          // 休眠状态
        public bool StatusForceCharge { get; set; }    // 强制充电请求

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
            ApplyDefaultConfig(BmsDefaultConfig.Load());
        }

        public void ApplyDefaultConfig(BmsDefaultConfig config)
        {
            lock (_lock)
            {
                Voltage = config.Voltage;
                Current = config.Current;
                Temperature = config.Temperature;
                SOC = config.SOC;
                SOH = config.SOH;
                RemainingCapacity = config.RemainingCapacity;
                FullCapacity = config.FullCapacity;
                MaxChargeCurrent = config.MaxChargeCurrent;
                MaxDischargeCurrent = config.MaxDischargeCurrent;
                CVVoltage = config.CVVoltage;

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
                StatusBalancing = false;
                StatusSleep = false;
                StatusForceCharge = false;
            }
        }
    }
}
