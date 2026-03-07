using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace HIS_RIS_Integration
{
    public class OrderRepository
    {
        private readonly string _connectionString;
        private readonly ILogger<OrderRepository> _logger;

        public static class EventTypes
        {
            // Bill Event Types
            public const string BillGenerated = "BILL_GENERATED";
            public const string BillCancelled = "BILL_CANCELLED";
            // HL7 Event Types
            public const string Pending = "PENDING"; 
            public const string Sent = "SENT";
            public const string Received = "RECEIVED";
            public const string Failed = "FAILED";
            public const string ACKOk = "ACK_OK";
            public const string ACKError = "ACK_ERROR";
        }

        public OrderRepository(IConfiguration configuration, ILogger<OrderRepository> logger)
        {
            _logger = logger;
            var encryptedConnString = configuration.GetSection("Database:ConnectionString").Value;
            if (string.IsNullOrEmpty(encryptedConnString))
            {
                throw new ArgumentNullException("Database connection string is missing.");
            }
            _connectionString = EncryptionHelper.Decrypt(encryptedConnString);
        }

        public async Task<int> ExecuteNonQueryAsync(string query, params SqlParameter[] parameters)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(query, connection);

            if (parameters != null && parameters.Length > 0)
                command.Parameters.AddRange(parameters);

            return await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ExecuteScalarAsync<T>(string query, params SqlParameter[] parameters)
        {
            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddRange(parameters);

            await conn.OpenAsync();
            var result = await cmd.ExecuteScalarAsync();

            if (result == null || result == DBNull.Value)
                return default;

            return (T)Convert.ChangeType(result, typeof(T));
        }

        public async Task<TestOrder?> GetOrderDetailsAsync(int billId)
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                var query = @"
                    SELECT
	                    pd.Patient_ID,
	                    pd.First_Name,
	                    pd.Last_Name,
	                    pd.DOB,
	                    pd.Gender,
	                    pd.Telephone,
	                    dd.DocID,
	                    sb.Doct,
	                    sb.AdmissionID,
	                    sb.BillID,
                        sb.[Status],
                        sbd.DetID,
	                    sbd.TestID, 
	                    sbd.[Service],
	                    mm.AE_Title,
	                    mm.Modality,
                        ISNULL(sbd.Amt, 0) - ISNULL(br.Amount, 0) AS AfterRefund
                    FROM ServiceBillDetails sbd
                    INNER JOIN ServiceBill sb ON sbd.BillID=sb.BillID
                    INNER JOIN Patient_Details pd ON sb.Patient_ID=pd.Patient_ID
                    INNER JOIN HISRISMap mm ON sbd.Channel=mm.ServiceCat
                    LEFT JOIN Doctor_Details dd ON sb.Doct=dd.Doctor_ID
                    LEFT JOIN BillRefunds br ON sbd.RefundID=br.RefundID
                    WHERE sb.BillID=@BillId AND mm.IsActive=1;";

                using var command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@BillId", billId);

                using var reader = await command.ExecuteReaderAsync();

                TestOrder? order = null;

                while (await reader.ReadAsync())
                {
                    if (order == null)
                    {
                        // Create bill object once
                        order = new TestOrder
                        {
                            PatientId = reader.IsDBNull(0) ? null : reader.GetInt64(0),
                            FirstName = reader.IsDBNull(1) ? null : reader.GetString(1),
                            LastName = reader.IsDBNull(2) ? null : reader.GetString(2),
                            DateOfBirth = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                            Gender = reader.IsDBNull(4) ? null : reader.GetString(4),
                            Phone = reader.IsDBNull(5) ? null : reader.GetString(5),
                            PhysicianID = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                            Physician = reader.IsDBNull(7) ? null : reader.GetString(7),
                            AdmissionId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                            BillId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                            BillStatus = reader.IsDBNull(10) ? null : reader.GetString(10),
                            TestOrderItems = new List<TestOrderItem>()
                        };
                    }

                    // Add each order per row
                    order.TestOrderItems.Add(new TestOrderItem
                    {
                        OrderId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                        TestId = reader.IsDBNull(12) ? null : reader.GetInt64(12),
                        TestName = reader.IsDBNull(13) ? null : reader.GetString(13),
                        AE_Title = reader.IsDBNull(14) ? null : reader.GetString(14),
                        Modality = reader.IsDBNull(15) ? null : reader.GetString(15),
                        AfterRefund = reader.IsDBNull(16) ? null : reader.GetDouble(16)
                    });
                }
                return order;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching order details for BillId: {BillId}", billId);
                throw; 
            }
        }

        public async Task InsertReportAsync(TestReport report)
        {
            try
            {
                var query = @"
                    INSERT INTO HISRISReport (
                        EventId, OrderId, ReportTime, ReportStatus, Report, PACS_URL
                    ) VALUES (
                        @EventId, @OrderId, @ReportTime, @ReportStatus, @Report, @PACS_URL
                    )";

                int? eventId = null;
                if (report.OrderId.HasValue)
                {
                    eventId = await FindEventIdFromOrderId(report.OrderId.Value);
                }

                await ExecuteNonQueryAsync(query,
                    new SqlParameter("@EventId", eventId ?? (object)DBNull.Value),
                    new SqlParameter("@OrderId", report.OrderId ?? (object)DBNull.Value),
                    new SqlParameter("@ReportTime", DateTime.Now),
                    new SqlParameter("@ReportStatus", report.ReportStatus ?? (object)DBNull.Value),
                    new SqlParameter("@Report", report.Report ?? (object)DBNull.Value),
                    new SqlParameter("@PACS_URL", report.PACS_URL ?? (object)DBNull.Value)
                );

                _logger.LogInformation("Successfully inserted radiology report for OrderId: {OrderId}", report.OrderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inserting radiology report for OrderId: {OrderId}", report.OrderId);
                throw;
            }
        }

        public async Task<int> LogOrderEventToDB(int billId, string eventToLog)
        {
            try
            {
                var eventId = 0;
                switch (eventToLog)
                {
                    case EventTypes.BillGenerated:
                    case EventTypes.BillCancelled:
                        eventId = await ExecuteScalarAsync<int>(
                            @"INSERT INTO RISHead (EventType, EventTime, BillId)
                                    OUTPUT INSERTED.EventId
                                    VALUES (@EventType, @EventTime, @BillId);",
                            new SqlParameter("@EventType", eventToLog),
                            new SqlParameter("@EventTime", DateTime.Now),
                            new SqlParameter("@BillId", billId)
                        );
                        return eventId;

                    default:
                        _logger.LogWarning("Unknown event type: {EventType}", eventToLog);
                        return -1;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving order event log to DB.");
                throw;
            }
        }

        /// <summary>
        /// Inserts service generated HL7 messages to the db.
        /// </summary>
        public async Task<int> LogHL7InsertEventToDB(string hl7Message, int eventId, string eventToLog)
        {
            try
            {
                var messageID = 0;
                int? orderID = await GetOrderIdFromHl7Async(hl7Message);

                // Adding <VT>hl7Message<FS><CR>
                //hl7Message = $"\v{hl7Message}\u001C\r";

                switch (eventToLog)
                {
                    case EventTypes.Pending:
                        messageID = await ExecuteScalarAsync<int>(
                            @"INSERT INTO RISDetail (EventId, OrderId, MessageTime, MessageType, Status, HL7Payload)
                                    OUTPUT INSERTED.MessageId
                                    VALUES (@EventId, @OrderId, @MessageTime, @MessageType, @Status, @HL7Payload);",
                            new SqlParameter("@EventId", eventId),
                            new SqlParameter("@OrderId", orderID),
                            new SqlParameter("@MessageTime", DateTime.Now),
                            new SqlParameter("@MessageType", "ORM^O01"),
                            new SqlParameter("@Status", eventToLog),
                            new SqlParameter("@HL7Payload", hl7Message)
                        );
                        return messageID;

                    default:
                        _logger.LogWarning("Unknown event type: {EventType}", eventToLog);
                        return -1;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving HL7 insert event log to DB.");
                throw;
            }
        }

        /// <summary>
        /// Updates the status of service-generated HL7 messages based on
        /// whether the message was successfully delivered to the RIS.
        /// </summary>
        public async Task LogHL7UpdateEventToDB(int messageID, string eventToLog)
        {
            try
            {
                switch (eventToLog)
                {
                    case EventTypes.Sent:
                    case EventTypes.Failed:
                        await ExecuteNonQueryAsync(
                            "UPDATE RISDetail SET Status=@Status WHERE MessageId=@MessageId",
                            new SqlParameter("@Status", eventToLog),
                            new SqlParameter("@MessageId", messageID)
                        );
                        break;
                    default:
                        _logger.LogWarning("Unknown event type: {EventType}", eventToLog);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving HL7 update event log to DB.");
                throw;
            }
        }

        /// <summary>
        /// Logs incoming ACK and ORU HL7 messages from the RIS to the database.
        /// </summary>
        public async Task SaveIncomingHl7Async(string hl7Message)
        {
            int? orderId = await GetOrderIdFromHl7Async(hl7Message);
            string? messageType = HL7Service.ExtractMessageTypeFromHl7(hl7Message); 

            int eventId = 0;
            if (orderId.HasValue)
            {
                eventId = await ExecuteScalarAsync<int>(
                    "SELECT TOP 1 EventId FROM RISDetail WHERE OrderId = @OrderId ORDER BY 1 DESC",
                    new SqlParameter("@OrderId", orderId)
                );

                try
                {
                    await ExecuteNonQueryAsync(
                        @"INSERT INTO RISDetail (EventId, OrderId, MessageTime, MessageType, Status, HL7Payload)
                        VALUES (@EventId, @OrderId, @MessageTime, @MessageType, @Status, @HL7Payload);",
                        new SqlParameter("@EventId", eventId),
                        new SqlParameter("@OrderId", orderId),
                        new SqlParameter("@MessageTime", DateTime.Now),
                        new SqlParameter("@MessageType", messageType),
                        new SqlParameter("@Status", EventTypes.Received),
                        new SqlParameter("@HL7Payload", hl7Message)
                    );

                    if (messageType.Contains("ACK"))
                    {
                        _logger.LogInformation("Acknowledgement received for OrderId: {OrderId}", orderId);
                    }
                    if (messageType.Contains("ORU"))
                    {
                        _logger.LogInformation("Report received for OrderId: {OrderId}", orderId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving incoming HL7 message.");
                    throw;
                }
            }
            else
            {
                try
                {
                    await ExecuteNonQueryAsync(
                        @"INSERT INTO RISDetail (EventId, OrderId, MessageTime, MessageType, Status, HL7Payload)
                        VALUES (@EventId, @OrderId, @MessageTime, @MessageType, @Status, @HL7Payload);",
                        new SqlParameter("@EventId", DBNull.Value),
                        new SqlParameter("@OrderId", DBNull.Value),
                        new SqlParameter("@MessageTime", DateTime.Now),
                        new SqlParameter("@MessageType", messageType),
                        new SqlParameter("@Status", EventTypes.Received),
                        new SqlParameter("@HL7Payload", hl7Message)
                    );

                    _logger.LogError("No matching OrderId was found for the incoming HL7 message.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving incoming HL7 message.");
                    throw;
                }
            }
        }

        public async Task<int?> FindOrderIdFromMsgCtrlId(string controlId)
        {
            try
            {
                int orderId = 0;

                orderId = await ExecuteScalarAsync<int>(
                    "SELECT OrderId FROM RISDetail WHERE HL7Payload LIKE @ControlId",
                    new SqlParameter("@ControlId", "%" + controlId + "%")
                );

                return orderId == 0 ? null : orderId;

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching OrderId from ControlId.");
                throw;
            }
        }

        private async Task<int?> GetOrderIdFromHl7Async(string hl7Message)
        {
            var identifier = HL7Service.ExtractOrderIdentifier(hl7Message);
            if (string.IsNullOrWhiteSpace(identifier)) 
                return null;

            if (int.TryParse(identifier, out int orderId))
            {
                return orderId;
            }

            return await FindOrderIdFromMsgCtrlId(identifier);
        }

        public async Task<int?> FindEventIdFromOrderId(int orderId)
        {
            try
            {
                int eventId = 0;

                eventId = await ExecuteScalarAsync<int>(
                    "SELECT TOP 1 EventId FROM RISDetail WHERE OrderId=@OrderId",
                    new SqlParameter("@OrderId", orderId)
                );

                return eventId == 0 ? null : eventId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching EventId from OrderId.");
                throw;
            }
        }
    }
}
