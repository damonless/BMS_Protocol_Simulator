using System;
using System.Collections.Generic;
using System.Text;

namespace BMS_Protocol_Simulator
{
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
            Test0x351EncodingAndDecoding();
            Test0x355EncodingAndDecoding();
            Test0x356EncodingAndDecoding();
            Test0x359EncodingAndDecoding();
            Test0x35CEncodingAndDecoding();
            Test0x35EEncodingAndDecoding();
            TestNegativeCurrentAndTemp();
            TestSlcanFormattingAndParsing();
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

        static void Test0x351EncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 2] 0x351 Charge/Discharge Management Parameters");
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

        static void Test0x355EncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 3] 0x355 SOC / SOH / Cell Voltage");
            BmsDataModel model = new BmsDataModel();
            model.SOC = 85.0; // 85 = 0x0055 -> 55 00
            model.SOH = 98.0; // 98 = 0x0062 -> 62 00
            model.Voltage = 51.2;

            string summary;
            CanFrame frame = HanchuCanProtocol.Build0x355Frame(model, out summary);
            Assert(frame.Id == 0x355, "0x355 ID check");
            Assert(frame.Data[0] == 85 && frame.Data[1] == 0, "0x355 SOC LE encoded");
            Assert(frame.Data[2] == 98 && frame.Data[3] == 0, "0x355 SOH LE encoded");

            BmsDataModel rxModel = new BmsDataModel();
            string decSummary;
            HanchuCanProtocol.TryDecodeFrame(frame, rxModel, out decSummary);
            Assert(Math.Abs(rxModel.SOC - 85.0) < 0.01, "0x355 Decoded SOC == 85%");
            Assert(Math.Abs(rxModel.SOH - 98.0) < 0.01, "0x355 Decoded SOH == 98%");
        }

        static void Test0x356EncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 4] 0x356 Pack Voltage / Current / Temperatures");
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

        static void Test0x359EncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 5] 0x359 Protection & Alarm Bitfields");
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

        static void Test0x35CEncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 6] 0x35C Request Flags & Cycle Count");
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
        }

        static void Test0x35EEncodingAndDecoding()
        {
            Console.WriteLine("\n[Test 7] 0x35E Manufacturer Name");
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
            Console.WriteLine("\n[Test 8] Negative Current (Discharge) and Negative Temperature");
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

        static void TestSlcanFormattingAndParsing()
        {
            Console.WriteLine("\n[Test 9] SLCAN Protocol Command Formatting");
            CanFrame frame = new CanFrame(0x351, new byte[] { 0x20, 0x01, 0xE8, 0x03, 0xE8, 0x03, 0xD2, 0x00 });
            string hex = frame.ToHexString();
            Assert(hex == "20 01 E8 03 E8 03 D2 00", "Frame ToHexString output");

            // Format SLCAN standard frame
            string slcanCmd = string.Format("t{0:X3}{1:X1}{2}\r", frame.Id & 0x7FF, frame.Dlc, hex.Replace(" ", ""));
            Assert(slcanCmd == "t35182001E803E803D200\r", "SLCAN standard frame encoding");

            // Parse back
            string line = slcanCmd.Trim();
            uint parsedId = Convert.ToUInt32(line.Substring(1, 3), 16);
            byte parsedDlc = Convert.ToByte(line.Substring(4, 1), 16);
            byte[] parsedData = new byte[parsedDlc];
            for (int i = 0; i < parsedDlc; i++) parsedData[i] = Convert.ToByte(line.Substring(5 + i * 2, 2), 16);
            CanFrame parsedFrame = new CanFrame(parsedId, parsedData, false, false);
            Assert(parsedFrame.Id == 0x351, "Parsed SLCAN ID == 0x351");
            Assert(parsedFrame.Dlc == 8, "Parsed SLCAN DLC == 8");
            Assert(parsedFrame.Data[0] == 0x20 && parsedFrame.Data[1] == 0x01, "Parsed SLCAN data match");
        }

        static void TestControlCanDependencyCheck()
        {
            Console.WriteLine("\n[Test 10] ControlCAN Dependency & Safety Check");
            string errorReason;
            bool ok = ControlCanDriver.CheckDllAvailability(out errorReason);
            // If ControlCAN.dll is not present, it must return false with a clear explanation rather than crashing!
            if (!ok)
            {
                Assert(!string.IsNullOrEmpty(errorReason), "Safety check returns helpful error reason when DLL absent");
                Assert(errorReason.Contains("ControlCAN.dll") || errorReason.Contains("SLCAN"), "Error reason guides user towards SLCAN");
            }
            else
            {
                Assert(ok, "ControlCAN.dll is present and successfully loadable");
            }
        }
    }
}
