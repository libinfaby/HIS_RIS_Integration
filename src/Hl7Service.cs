using Microsoft.AspNetCore.SignalR.Protocol;
using System;
using System.Reflection.PortableExecutable;
using System.Text;

namespace HIS_RIS_Integration
{
    public class HL7Service
    {
        private readonly string _hospitalname;
        private readonly string _centerid;

        public HL7Service(IConfiguration configuration)
        {
            _hospitalname = configuration.GetValue<string>("Database:Hospital") ?? "";
            _centerid = configuration.GetValue<string>("Database:CenterID") ?? "";
        }

        private static string CleanHL7(string input) 
        { 
            if (string.IsNullOrEmpty(input)) 
                return input;

            var cleanMessage = input
                .Replace("\x0B", "")
                .Replace("\x1C", "")
                .Replace("\r\n", "\r")
                .Replace("\n", "\r");

            return cleanMessage;
        }

        public string GenerateOrderMessage(TestOrder order, TestOrderItem item)
        {
            // This function generates an HL7 ORM^O01 message for a radiology order

            var sb = new StringBuilder();
            var now = DateTime.Now.ToString("yyyyMMddHHmmss");
            var billStatus = (order.BillStatus == "CLD" || item.AfterRefund == 0) ? "CA" : "NW";
            var OPIP = order.AdmissionId > 0 ? "IP" : "OP";

            // MSH Segment
            sb.Append($"MSH|^~\\&|HIS|{_hospitalname}|HIS_RIS|RIS|{now}||ORM^O01|{Guid.NewGuid()}|P|2.3");
            sb.Append('\r');

            // PID Segment
            sb.Append($"PID|1||{order.PatientId}||{order.LastName}^{order.FirstName}^^^||{order.DateOfBirth:yyyyMMdd}|{order.Gender}|||||{order.Phone}^^");
            sb.Append('\r');

            // PV1 Segment (Visit)
            sb.Append($"PV1|1|||||||{order.PhysicianID}^{order.Physician}||||||||||{OPIP}||");
            sb.Append('\r');

            // ORC Segment
            sb.Append($"ORC|{billStatus}|||||||||||");
            sb.Append('\r');

            // OBR Segment
            sb.Append($"OBR|1|{item.OrderId}|{_centerid}|{item.TestId}^{item.TestName}|||||||||||||||||{item.AE_Title}|||{item.Modality}||||||||||||{now}|");
            sb.Append('\r');

            return sb.ToString();
            // MLLP framing will be added at the transmission layer(<VT>, <FS>, <CR>)
        }

        public TestReport? ParseIncomingHL7Message(string hl7Message)
        {
            if (string.IsNullOrWhiteSpace(hl7Message))
                return null;

            try
            {
                // Cleaning MLLP framing characters if present
                var cleanMessage = CleanHL7(hl7Message);

                // Split into segments using <CR>
                var segments = cleanMessage.Split('\r');

                var report = new TestReport();

                foreach (var segment in segments)
                {
                    if (string.IsNullOrWhiteSpace(segment))
                        continue;

                    var fields = segment.Split('|');
                    var segmentType = fields[0];

                    switch (segmentType)
                    {
                        case "PID": // Patient Identification
                            //ParsePIDSegment(fields, report);
                            break;
                        case "PV1": // Patient Visit
                            //ParsePV1Segment(fields, report);
                            break;
                        case "OBR": // Observation Request
                            ParseOBRSegment(fields, report);
                            break;
                        case "OBX": // Observation Request
                            ParseOBXSegment(fields, report);
                            break;
                    }
                }

                return report;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void ParseOBRSegment(string[] fields, TestReport report)
        {   
            // OBR-2: Placer Order Number (OrderId)
            if (fields.Length > 2 && !string.IsNullOrWhiteSpace(fields[2]))
            {
                if (int.TryParse(fields[2], out int orderId))
                    report.OrderId = orderId;
            }
        }

        private static void ParseOBXSegment(string[] fields, TestReport report)
        {
            var obxCount = string.Empty;

            if (fields.Length > 1 && !string.IsNullOrWhiteSpace(fields[1]))
            {
                obxCount = fields[1];
            }

            if (obxCount == "1") // OBX|1 is for Report, OBX|2 is for PACS URL
            {
                // OBX-5: Report 
                if (fields.Length > 5 && !string.IsNullOrWhiteSpace(fields[5]))
                {
                    var mllpMessage = fields[5].Replace("\\X0D\\", "\x0D").Replace("\\F\\", "|").Replace("\\S\\", "^").Replace("\\T\\", "&").Replace("\\R\\", "~").Replace("\\X0A\\", "\x0A");
                    report.Report = mllpMessage;
                }

                // OBX-11: Report Status
                if (fields.Length > 11 && !string.IsNullOrWhiteSpace(fields[11]))
                {
                    var reportStatus = fields[11];
                    switch (reportStatus)
                    {
                        case "I":
                            reportStatus = "COMPLETED";
                            break;
                        case "R":
                            reportStatus = "READ";
                            break;
                        case "F":
                            reportStatus = "FINAL";
                            break;
                        case "P":
                            reportStatus = "PRINT";
                            break;
                        default:
                            break;
                    }
                    report.ReportStatus = reportStatus;
                }
            }
            else if(obxCount == "2")
            {
                // OBX-5: PACS URL 
                if (fields.Length > 5 && !string.IsNullOrWhiteSpace(fields[5]))
                {
                    report.PACS_URL = fields[5];
                }
            }
        }

        public static string? ExtractOrderIdentifier(string hl7Message)
        {
            if (string.IsNullOrWhiteSpace(hl7Message))
                return null;

            // Cleaning MLLP framing characters if present
            var cleanMessage = CleanHL7(hl7Message);

            // Split into segments (CR is HL7 standard)
            var segments = cleanMessage.Split('\r');

            // Find OBR segment
            var obr = segments.FirstOrDefault(s => s.StartsWith("OBR|") || s.StartsWith("MSA|"));
            if (obr == null)
                return null;

            // Split OBR fields
            var fields = obr.Split('|');

            // OBR-3 = Order ID (0-based index = 2)
            if (fields.Length > 2)
            {
                return fields[2];
            }
            return null;
        }

        public static string? ExtractMessageControlIdFromHl7(string hl7Message)
        {
            if (string.IsNullOrWhiteSpace(hl7Message))
                return null;

            // Cleaning MLLP framing characters if present
            var cleanMessage = CleanHL7(hl7Message);

            // Split into segments (CR is HL7 standard)
            var segments = cleanMessage.Split('\r');

            // Find MSH segment
            var msh = segments
                .Select(s => s.Replace("\v", "").Trim())
                .FirstOrDefault(s => s.StartsWith("MSH"));
            if (msh == null)
                return null;

            // Split MSH fields
            var fields = msh.Split('|');

            // MSH-10 = Message Control ID (0-based index = 9)
            if (fields.Length > 9)
            {
                var messageControlId = fields[9];
                return string.IsNullOrWhiteSpace(messageControlId)
                    ? null
                    : messageControlId;
            }

            return null;
        }

        public static string? ExtractMessageTypeFromHl7(string hl7Message)
        {
            if (string.IsNullOrWhiteSpace(hl7Message))
                return null;

            // Cleaning MLLP framing characters if present
            var cleanMessage = CleanHL7(hl7Message);

            // Split into segments (CR is HL7 standard)
            var segments = cleanMessage.Split('\r');

            var msh = segments.FirstOrDefault(s => s.StartsWith("MSH"));
            if (msh == null)
                return null;

            // Split MSH fields
            var fields = msh.Split('|');

            // MSH-9 = Message Type (e.g. ORM^O01)
            if (fields.Length > 8)
            {
                var messageType = fields[8];
                return string.IsNullOrWhiteSpace(messageType)
                    ? null
                    : messageType;
            }
            return null;
        }
    }
}
