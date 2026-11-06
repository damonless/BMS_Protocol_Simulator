using System;
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


        // ── 扩展仿真参数：单芯压差拉偏 (mV) ──
        public int CellImbalanceMv { get; set; }       // 单体最高与最低电芯压差设定 (mV, 如 0 / 50 / 150 / 300)

        public ushort GetCellMillivolts(int cellIndexZeroBased, int cellCount, out ushort maxCellMv, out ushort minCellMv)
        {
            if (cellCount <= 0) cellCount = (Voltage < 36.0) ? 8 : 16;
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
            string target = (profileName != null && profileName.ToUpperInvariant().Contains("6KU")) ? "6KU" : "3KU";
            ActiveProfileName = target;
            ApplyDefaultConfig(new BmsParameterProfile(target));
        }

        public BmsDataModel()
        {
            ApplyProfile("3KU");
        }

        public void ApplyDefaultConfig(BmsParameterProfile profile)
        {
            if (profile == null) return;
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(profile.ProfileName)) ActiveProfileName = profile.ProfileName;

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
}
