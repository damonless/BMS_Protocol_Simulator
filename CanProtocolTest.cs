using System;
using System.Collections.Generic;
using System.Text;

namespace BMS_Protocol_Simulator
{
    class MockCanDriver : ICanDriver
    {
        public List<CanFrame> SentFrames = new List<CanFrame>();
        public string DriverName { get { return "MockDriver"; } }
        public bool IsOpen { get; private set; }

        public event EventHandler<CanFrame> OnFrameReceived;
#pragma warning disable 0067
        public event EventHandler<string> OnError;
        public event EventHandler<string> OnStatusChanged;
#pragma warning restore 0067

        public bool Open() { IsOpen = true; return true; }
        public void Close() { IsOpen = false; }
        public bool SendFrame(CanFrame frame)
        {
            if (!IsOpen) return false;
            SentFrames.Add(frame);
            return true;
        }
        public void SimulateReceive(CanFrame frame)
        {
            if (OnFrameReceived != null) OnFrameReceived(this, frame);
        }
        public void Dispose() { Close(); }
    }

    class CanProtocolTest
    {
        static int _passed = 0;
        static int _failed = 0;

        static void Assert(bool condition, string testName, string detail = "")
        {
            if (condition)
            {
                Console.WriteLine("  [PASS] " + testName);
                _passed++;
            }
            else
            {
                Console.WriteLine("  [FAIL] " + testName + (string.IsNullOrEmpty(detail) ? "" : (" (" + detail + ")")));
                _failed++;
            }
        }

        static void Main(string[] args)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  Running Comprehensive CAN Protocol & Driver Tests");
            Console.WriteLine("==================================================");

            Test0x305Handshake();
            Test0x305SlaveResponse();
            Test0x351EncodingAndDecoding();
            Test0x351ProtectionLinkageAndHighCurrent();
            Test0x355EncodingAndDecoding();
            Test0x356EncodingAndDecoding();
            Test0x356HighVoltageNoOverflow();
            Test0x359EncodingAndDecoding();
            Test0x359OvercurrentDirectionalIsolation();
            Test0x35CEncodingAndDecoding();
            Test0x35EEncodingAndDecoding();
            TestNegativeCurrentAndTemp();
            TestSlcanStandardExtendedAndRemoteFrames();
            TestCanFrameBufferSafety();
            TestControlCanDependencyCheck();

            Console.WriteLine("==================================================");
            Console.WriteLine(string.Format("Test Results: {0} Passed, {1} Failed", _passed, _failed));
            Console.WriteLine("==================================================");

            if (_failed > 0) Environment.Exit(1);
        }

        static void Test0x305Handshake()
        {
            Console.WriteLine("\n[Test 1] 0x305 Inverter Handshake Frame");
            CanFrame frame = HanchuCanProtocol.BuildInverterHandshakeFrame();
            Assert(frame.Id == 0x305, "Handshake frame ID is 0x305");
            Assert(frame.Dlc == 8, "Handshake frame DLC is 8");
            bool allZeros = true;
            for (int i = 0; i < 8; i++) if (frame.Data[i] != 0) allZeros = false;
            Assert(allZeros, "Handshake frame all 8 bytes are 0x00");

            BmsDataModel model = new BmsDataModel();
            string summary;
            bool decoded = HanchuCanProtocol.TryDecodeFrame(frame, model, out summary);
            Assert(decoded && summary.Contains("0x305"), "Decode 0x305 handshake recognized");
        }

        static void Test0x305SlaveResponse()
        {
            Console.WriteLine("\n[Test 2] 0x305 Inverter Handshake Response in Slave Mode");
            BmsDataModel model = new BmsDataModel();
            CanCommManager mgr = new CanCommManager(model);
            mgr.CurrentWorkMode = WorkMode.SlaveSimulator;
            MockCanDriver mockDriver = new MockCanDriver();
            bool opened = mgr.Open(mockDriver);
            Assert(opened, "CanCommManager opened with MockDriver");

            // Inverter sends 0x305 handshake
            CanFrame handshake = HanchuCanProtocol.BuildInverterHandshakeFrame();
            mockDriver.SimulateReceive(handshake);

            // BMS should immediately respond with 0x305
            Assert(mockDriver.SentFrames.Count > 0, "BMS slave sent response to 0x305 handshake");
            CanFrame lastSent = mockDriver.SentFrames[mockDriver.SentFrames.Count - 1];
            Assert(lastSent.Id == 0x305, "BMS response frame ID is 0x305");
            Assert(lastSent.Dlc == 8, "BMS response frame DLC is 8");
            Assert(lastSent.Data[0] == 0 && lastSent.Data[7] == 0, "BMS response frame bytes are 0x00");

            mgr.Close();
        }

        static void Test0x351EncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 3] 0x351 Charge/Discharge Management Parameters");
            BmsDataModel model = new BmsDataModel();
            model.CVVoltage = 56.0;         // 560 = 0x0230 -> 30 02
            model.MaxChargeCurrent = 100.0; // 1000 = 0x03E8 -> E8 03
            model.MaxDischargeCurrent = 80.0;// 800 = 0x0320 -> 20 03
            model.CutoffVoltage = 42.0;     // 420 = 0x01A4 -> A4 01
            model.StatusChargeEnable = true;
            model.StatusDischargeEnable = true;

            string summary;
            CanFrame frame = HanchuCanProtocol.Build0x351Frame(model, out summary);
            Assert(frame.Id == 0x351, "0x351 ID check");
            Assert(frame.Dlc == 8, "0x351 DLC check");

            // Check Little Endian encoding
            Assert(frame.Data[0] == 0x30 && frame.Data[1] == 0x02, "0x351 CVVoltage LE (0x0230)");
            Assert(frame.Data[2] == 0xE8 && frame.Data[3] == 0x03, "0x351 MaxChargeCurrent LE (0x03E8)");
            Assert(frame.Data[4] == 0x20 && frame.Data[5] == 0x03, "0x351 MaxDischargeCurrent LE (0x0320)");
            Assert(frame.Data[6] == 0xA4 && frame.Data[7] == 0x01, "0x351 CutoffVoltage LE (0x01A4)");

            // Test decoding into fresh model
            BmsDataModel rxModel = new BmsDataModel();
            string decSummary;
            bool ok = HanchuCanProtocol.TryDecodeFrame(frame, rxModel, out decSummary);
            Assert(ok, "0x351 Decode success");
            Assert(Math.Abs(rxModel.CVVoltage - 56.0) < 0.01, "0x351 Decoded CVVoltage == 56.0V");
            Assert(Math.Abs(rxModel.MaxChargeCurrent - 100.0) < 0.01, "0x351 Decoded MaxChargeCurrent == 100.0A");
            Assert(Math.Abs(rxModel.MaxDischargeCurrent - 80.0) < 0.01, "0x351 Decoded MaxDischargeCurrent == 80.0A");
            Assert(Math.Abs(rxModel.CutoffVoltage - 42.0) < 0.01, "0x351 Decoded CutoffVoltage == 42.0V");
        }

        static void Test0x351ProtectionLinkageAndHighCurrent()
        {
            Console.WriteLine("\n[Test 4] 0x351 Protection Linkage & High Current Scale");
            BmsDataModel model = new BmsDataModel();
            model.CVVoltage = 57.6;
            model.MaxChargeCurrent = 150.0;
            model.MaxDischargeCurrent = 200.0;
            model.CutoffVoltage = 40.0;
            model.StatusChargeEnable = true;
            model.StatusDischargeEnable = true;
            model.ProtOverVolt = true; // Overvoltage trip! Charge limit MUST be 0

            string summary;
            CanFrame frame = HanchuCanProtocol.Build0x351Frame(model, out summary);
            ushort chgIRaw = (ushort)(frame.Data[2] | (frame.Data[3] << 8));
            ushort disIRaw = (ushort)(frame.Data[4] | (frame.Data[5] << 8));
            Assert(chgIRaw == 0, "0x351 CCL is 0A when ProtOverVolt is active");
            Assert(disIRaw == 2000, "0x351 DCL remains 200.0A (2000) when only ProtOverVolt is active");

            // Test high current limits without signed overflow (e.g. 400.0A -> 4000 = 0x0FA0)
            model.ProtOverVolt = false;
            model.MaxChargeCurrent = 400.0;
            CanFrame frameHigh = HanchuCanProtocol.Build0x351Frame(model, out summary);
            BmsDataModel rxModel = new BmsDataModel();
            string decSummary;
            HanchuCanProtocol.TryDecodeFrame(frameHigh, rxModel, out decSummary);
            Assert(Math.Abs(rxModel.MaxChargeCurrent - 400.0) < 0.01, "0x351 High CCL 400.0A encodes and decodes properly");
        }

        static void Test0x355EncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 5] 0x355 SOC / SOH / Cell Voltages");
            BmsDataModel model = new BmsDataModel();
            model.SOC = 85.0; // 85 = 0x0055 -> 55 00
            model.SOH = 98.0; // 98 = 0x0062 -> 62 00
            model.Voltage = 51.2; // 16S: avg = 3200mV

            string summary;
            CanFrame frame = HanchuCanProtocol.Build0x355Frame(model, out summary);
            Assert(frame.Id == 0x355, "0x355 ID check");
            Assert(frame.Data[0] == 85 && frame.Data[1] == 0, "0x355 SOC LE encoded");
            Assert(frame.Data[2] == 98 && frame.Data[3] == 0, "0x355 SOH LE encoded");

            // Verify cell voltages LE
            ushort maxCell = (ushort)(frame.Data[4] | (frame.Data[5] << 8));
            ushort minCell = (ushort)(frame.Data[6] | (frame.Data[7] << 8));
            Assert(maxCell >= 3000 && maxCell <= 4000, "0x355 Max Cell Voltage in valid range");
            Assert(minCell >= 3000 && minCell <= maxCell, "0x355 Min Cell Voltage in valid range and <= Max Cell");

            BmsDataModel rxModel = new BmsDataModel();
            string decSummary;
            HanchuCanProtocol.TryDecodeFrame(frame, rxModel, out decSummary);
            Assert(Math.Abs(rxModel.SOC - 85.0) < 0.01, "0x355 Decoded SOC == 85%");
            Assert(Math.Abs(rxModel.SOH - 98.0) < 0.01, "0x355 Decoded SOH == 98%");
            Assert(decSummary.Contains("最高单体:") && decSummary.Contains("最低单体:"), "0x355 Decoded summary includes cell voltages");
        }

        static void Test0x356EncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 6] 0x356 Pack Voltage / Current / Temperatures");
            BmsDataModel model = new BmsDataModel();
            model.Voltage = 53.25;      // 5325 = 0x14CD -> CD 14
            model.Current = 25.4;       // 254 = 0x00FE -> FE 00
            model.Temperature = 28.5;   // 285 = 0x011D -> 1D 01

            string summary;
            CanFrame frame = HanchuCanProtocol.Build0x356Frame(model, out summary);
            Assert(frame.Id == 0x356, "0x356 ID check");
            Assert(frame.Data[0] == 0xCD && frame.Data[1] == 0x14, "0x356 Voltage 0.01V LE (5325)");
            Assert(frame.Data[2] == 0xFE && frame.Data[3] == 0x00, "0x356 Current 0.1A LE (254)");

            BmsDataModel rxModel = new BmsDataModel();
            string decSummary;
            HanchuCanProtocol.TryDecodeFrame(frame, rxModel, out decSummary);
            Assert(Math.Abs(rxModel.Voltage - 53.25) < 0.01, "0x356 Decoded Voltage == 53.25V");
            Assert(Math.Abs(rxModel.Current - 25.4) < 0.01, "0x356 Decoded Current == 25.4A");
            Assert(Math.Abs(rxModel.Temperature - 28.5) < 0.01, "0x356 Decoded Temperature == 28.5℃");
        }

        static void Test0x356HighVoltageNoOverflow()
        {
            Console.WriteLine("\n[Test 7] 0x356 High Voltage Support (No Overflow)");
            // 400.00V -> 40000 = 0x9C40 -> LE: 40 9C (would be negative under short!)
            BmsDataModel model = new BmsDataModel();
            model.Voltage = 400.00;
            model.Current = 10.0;
            model.Temperature = 25.0;

            string summary;
            CanFrame frame = HanchuCanProtocol.Build0x356Frame(model, out summary);
            Assert(frame.Data[0] == 0x40 && frame.Data[1] == 0x9C, "0x356 400V LE encoded (0x9C40)");

            BmsDataModel rxModel = new BmsDataModel();
            string decSummary;
            bool ok = HanchuCanProtocol.TryDecodeFrame(frame, rxModel, out decSummary);
            Assert(ok, "0x356 High Voltage decoded successfully");
            Assert(Math.Abs(rxModel.Voltage - 400.00) < 0.01, "0x356 Decoded 400.00V without negative overflow");

            // 512.00V -> 51200 = 0xC800 -> LE: 00 C8
            model.Voltage = 512.00;
            CanFrame frame512 = HanchuCanProtocol.Build0x356Frame(model, out summary);
            Assert(frame512.Data[0] == 0x00 && frame512.Data[1] == 0xC8, "0x356 512V LE encoded (0xC800)");
            HanchuCanProtocol.TryDecodeFrame(frame512, rxModel, out decSummary);
            Assert(Math.Abs(rxModel.Voltage - 512.00) < 0.01, "0x356 Decoded 512.00V properly");
        }

        static void Test0x359EncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 8] 0x359 Protection & Alarm Bitfields");
            BmsDataModel model = new BmsDataModel();
            model.ProtOverVolt = true;
            model.ProtHighTemp = true;
            model.WarnVoltDiff = true;
            model.WarnLowCapacity = true;
            model.RemainingCapacity = 100.0;

            string summary;
            CanFrame frame = HanchuCanProtocol.Build0x359Frame(model, out summary);
            Assert(frame.Id == 0x359, "0x359 ID check");
            // Protection 1: OverVolt is bit 3 (0x08), HighTemp is bit 0 (0x01) -> 0x09
            Assert((frame.Data[0] & 0x08) != 0, "0x359 ProtOverVolt bit set");
            Assert((frame.Data[0] & 0x01) != 0, "0x359 ProtHighTemp bit set");
            // Alarm 1: WarnVoltDiff is bit 5 (0x20)
            Assert((frame.Data[2] & 0x20) != 0, "0x359 WarnVoltDiff bit set");
            // Alarm 2: WarnLowCapacity is bit 2 (0x04)
            Assert((frame.Data[3] & 0x04) != 0, "0x359 WarnLowCapacity bit set");

            BmsDataModel rxModel = new BmsDataModel();
            string decSummary;
            HanchuCanProtocol.TryDecodeFrame(frame, rxModel, out decSummary);
            Assert(rxModel.ProtOverVolt, "0x359 Decoded ProtOverVolt");
            Assert(rxModel.ProtHighTemp, "0x359 Decoded ProtHighTemp");
            Assert(rxModel.WarnVoltDiff, "0x359 Decoded WarnVoltDiff");
            Assert(rxModel.WarnLowCapacity, "0x359 Decoded WarnLowCapacity");
        }

        static void Test0x359OvercurrentDirectionalIsolation()
        {
            Console.WriteLine("\n[Test 9] 0x359 Overcurrent & Warning Directional Isolation");
            
            // Case A: Discharging overcurrent (Current < 0)
            BmsDataModel modelDis = new BmsDataModel();
            modelDis.Current = -50.0; // Discharging
            modelDis.ProtOverCurrent = true;
            modelDis.WarnOverCurrent = true;

            string sDis;
            CanFrame frameDis = HanchuCanProtocol.Build0x359Frame(modelDis, out sDis);
            byte p1Dis = frameDis.Data[0];
            byte a1Dis = frameDis.Data[2];

            // Bit 4 is Discharge Overcurrent, Bit 7 is Charge Overcurrent
            Assert((p1Dis & (1 << 4)) != 0, "Discharge Overcurrent Prot Bit 4 is SET");
            Assert((p1Dis & (1 << 7)) == 0, "Charge Overcurrent Prot Bit 7 is CLEARED when discharging");
            Assert((a1Dis & (1 << 4)) != 0, "Discharge Overcurrent Warn Bit 4 is SET");
            Assert((a1Dis & (1 << 7)) == 0, "Charge Overcurrent Warn Bit 7 is CLEARED when discharging");

            // Case B: Charging overcurrent (Current > 0)
            BmsDataModel modelChg = new BmsDataModel();
            modelChg.Current = 50.0; // Charging
            modelChg.ProtOverCurrent = true;
            modelChg.WarnOverCurrent = true;

            string sChg;
            CanFrame frameChg = HanchuCanProtocol.Build0x359Frame(modelChg, out sChg);
            byte p1Chg = frameChg.Data[0];
            byte a1Chg = frameChg.Data[2];

            Assert((p1Chg & (1 << 7)) != 0, "Charge Overcurrent Prot Bit 7 is SET");
            Assert((p1Chg & (1 << 4)) == 0, "Discharge Overcurrent Prot Bit 4 is CLEARED when charging");
            Assert((a1Chg & (1 << 7)) != 0, "Charge Overcurrent Warn Bit 7 is SET");
            Assert((a1Chg & (1 << 4)) == 0, "Discharge Overcurrent Warn Bit 4 is CLEARED when charging");

            // Case C: Single-cell undervolt vs Global undervolt isolation
            BmsDataModel modelVolt = new BmsDataModel();
            modelVolt.WarnSingleUnderVolt = true;
            modelVolt.WarnGlobalUnderVolt = false;
            string sVolt;
            CanFrame frameVolt = HanchuCanProtocol.Build0x359Frame(modelVolt, out sVolt);
            Assert((frameVolt.Data[2] & 0x04) != 0, "Alarm 1 Bit 2 set for WarnSingleUnderVolt");
            Assert((frameVolt.Data[3] & 0x08) == 0, "Alarm 2 Bit 3 cleared when not WarnGlobalUnderVolt");
        }

        static void Test0x35CEncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 10] 0x35C Request Flags & Cycle Count");
            BmsDataModel model = new BmsDataModel();
            model.StatusChargeEnable = true;    // bit 7 (0x80)
            model.StatusDischargeEnable = true; // bit 6 (0x40)
            model.StatusForceCharge = true;     // bit 5/4 (0x30)
            model.StatusFullCharge = true;      // bit 3 (0x08)
            model.CycleCount = 125;             // 0x007D -> 7D 00

            string summary;
            CanFrame frame = HanchuCanProtocol.Build0x35CFrame(model, out summary);
            Assert(frame.Id == 0x35C, "0x35C ID check");
            byte b0 = frame.Data[0];
            Assert((b0 & 0x80) != 0, "0x35C Charge Enable bit set");
            Assert((b0 & 0x40) != 0, "0x35C Discharge Enable bit set");
            Assert((b0 & 0x30) != 0, "0x35C Force Charge bits set");
            Assert((b0 & 0x08) != 0, "0x35C Full Charge bit set");
            Assert(frame.Data[2] == 0x7D && frame.Data[3] == 0x00, "0x35C Cycle count 125 LE");

            BmsDataModel rxModel = new BmsDataModel();
            string decSummary;
            HanchuCanProtocol.TryDecodeFrame(frame, rxModel, out decSummary);
            Assert(rxModel.StatusChargeEnable, "0x35C Decoded Charge Enable");
            Assert(rxModel.StatusDischargeEnable, "0x35C Decoded Discharge Enable");
            Assert(rxModel.StatusForceCharge, "0x35C Decoded Force Charge");
            Assert(rxModel.StatusFullCharge, "0x35C Decoded Full Charge");
            Assert(rxModel.CycleCount == 125, "0x35C Decoded Cycle Count == 125");

            // Test protection linkage disables charge enable bit in 0x35C
            model.ProtOverVolt = true;
            CanFrame frameProt = HanchuCanProtocol.Build0x35CFrame(model, out summary);
            Assert((frameProt.Data[0] & 0x80) == 0, "0x35C Charge Enable bit cleared when ProtOverVolt active");
            Assert((frameProt.Data[0] & 0x40) != 0, "0x35C Discharge Enable bit remains active");
        }

        static void Test0x35EEncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 11] 0x35E Manufacturer Name");
            string summary;
            CanFrame frame = HanchuCanProtocol.Build0x35EFrame(out summary);
            Assert(frame.Id == 0x35E, "0x35E ID check");
            string ascii = Encoding.ASCII.GetString(frame.Data, 0, 6);
            Assert(ascii == "HANCHU", "0x35E ASCII is 'HANCHU'");

            BmsDataModel rxModel = new BmsDataModel();
            string decSummary;
            HanchuCanProtocol.TryDecodeFrame(frame, rxModel, out decSummary);
            Assert(decSummary.Contains("HANCHU"), "0x35E Decoded summary contains HANCHU");
        }

        static void TestNegativeCurrentAndTemp()
        {
            Console.WriteLine("\n[Test 12] Negative Current (Discharge) and Negative Temperature");
            BmsDataModel model = new BmsDataModel();
            model.Voltage = 48.0;
            model.Current = -45.5;      // Discharging 45.5A -> -455 = 0xFE39
            model.Temperature = -12.3;  // Subzero temp -12.3℃ -> -123 = 0xFF85

            string summary;
            CanFrame frame = HanchuCanProtocol.Build0x356Frame(model, out summary);
            BmsDataModel rxModel = new BmsDataModel();
            string decSummary;
            HanchuCanProtocol.TryDecodeFrame(frame, rxModel, out decSummary);

            Assert(Math.Abs(rxModel.Current - (-45.5)) < 0.01, "Negative current decoded correctly (-45.5A)");
            Assert(Math.Abs(rxModel.Temperature - (-12.3)) < 0.01, "Negative temperature decoded correctly (-12.3℃)");
        }

        static void TestSlcanStandardExtendedAndRemoteFrames()
        {
            Console.WriteLine("\n[Test 13] SLCAN Standard, Extended & Remote Frame Support");
            
            // Standard data frame
            CanFrame fStd = new CanFrame(0x351, new byte[] { 0x20, 0x01, 0xE8, 0x03, 0xE8, 0x03, 0xD2, 0x00 });
            Assert(fStd.ToHexString() == "20 01 E8 03 E8 03 D2 00", "Frame ToHexString standard");

            // Extended data frame
            CanFrame fExt = new CanFrame(0x18010203, new byte[] { 0x11, 0x22, 0x33, 0x44 }, true, false);
            Assert(fExt.IsExtended, "fExt is extended");
            Assert(fExt.Dlc == 4, "fExt DLC is 4");

            // Standard remote frame (r3518)
            CanFrame fRtrStd = new CanFrame(0x351, null, false, true);
            fRtrStd.Dlc = 8;
            Assert(fRtrStd.IsRemote, "fRtrStd is remote");

            // Extended remote frame (R180102038)
            CanFrame fRtrExt = new CanFrame(0x18010203, null, true, true);
            fRtrExt.Dlc = 8;
            Assert(fRtrExt.IsRemote && fRtrExt.IsExtended, "fRtrExt is extended remote");

            // Test parsing SLCAN line types
            string slcanRtrStd = "r3518";
            uint idRtrStd = Convert.ToUInt32(slcanRtrStd.Substring(1, 3), 16);
            byte dlcRtrStd = Convert.ToByte(slcanRtrStd.Substring(4, 1), 16);
            CanFrame parsedRtrStd = new CanFrame(idRtrStd, null, false, true);
            parsedRtrStd.Dlc = dlcRtrStd;
            Assert(parsedRtrStd.Id == 0x351 && parsedRtrStd.IsRemote && parsedRtrStd.Dlc == 8, "Parsed SLCAN standard RTR");

            string slcanExt = "T18010203411223344";
            uint idExt = Convert.ToUInt32(slcanExt.Substring(1, 8), 16);
            byte dlcExt = Convert.ToByte(slcanExt.Substring(9, 1), 16);
            byte[] dataExt = new byte[dlcExt];
            for (int i = 0; i < dlcExt; i++) dataExt[i] = Convert.ToByte(slcanExt.Substring(10 + i * 2, 2), 16);
            CanFrame parsedExt = new CanFrame(idExt, dataExt, true, false);
            Assert(parsedExt.Id == 0x18010203 && parsedExt.IsExtended && parsedExt.Data[0] == 0x11, "Parsed SLCAN extended data frame");
        }

        static void TestCanFrameBufferSafety()
        {
            Console.WriteLine("\n[Test 14] CanFrame Bounds & Safety Checks");
            CanFrame frame = new CanFrame();
            frame.Dlc = 250; // Attempt overflow
            Assert(frame.Dlc == 8, "CanFrame Dlc clamped to 8");

            byte[] raw = frame.GetRawData();
            Assert(raw.Length == 8, "GetRawData returns valid length without exception");

            CanFrame emptyFrame = new CanFrame(0x123, null);
            Assert(emptyFrame.Dlc == 0, "Null data initialized Dlc to 0");
            Assert(emptyFrame.GetRawData().Length == 0, "Empty GetRawData returns empty array");
            Assert(emptyFrame.ToHexString() == "", "Empty ToHexString returns empty string");
        }

        static void TestControlCanDependencyCheck()
        {
            Console.WriteLine("\n[Test 15] ControlCAN Dependency & Safety Check");
            string errorReason;
            bool ok = ControlCanDriver.CheckDllAvailability(out errorReason);
            if (!ok)
            {
                Assert(!string.IsNullOrEmpty(errorReason), "Safety check returns helpful error reason when DLL absent");
                Assert(errorReason.Contains("ControlCAN.dll") || errorReason.Contains("SLCAN"), "Error reason guides user towards SLCAN");
            }
            else
            {
                Assert(ok, "ControlCAN.dll is present and successfully loadable");
            }

            // Close should never throw even if unopened
            ControlCanDriver driver = new ControlCanDriver();
            driver.Close();
            Assert(!driver.IsOpen, "ControlCanDriver safely closed when unopened");
        }
    }
}
