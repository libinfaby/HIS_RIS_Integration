using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HIS_RIS_Integration
{
    public class ServiceBrokerListener
    {
        private readonly string _connectionString;
        private readonly string _queueName;
        private readonly ILogger<ServiceBrokerListener> _logger;

        public event Func<int, Task>? CheckBill; // Event to notify when a bill is received

        public ServiceBrokerListener(IConfiguration configuration, ILogger<ServiceBrokerListener> logger)
        {
            _logger = logger;
            var encryptedConnString = configuration.GetSection("Database:ConnectionString").Value;
            _connectionString = EncryptionHelper.Decrypt(encryptedConnString!);
            _queueName = configuration.GetValue<string>("ServiceBroker:OrderQueueName") ?? "OrderQueueName";
        }

        public async Task StartListeningAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Service Broker Listener on queue: {QueueName}", _queueName);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ListenForMessagesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Service Broker Listener loop. Retrying in 5 seconds.");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        private async Task ListenForMessagesAsync(CancellationToken token)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(token);

            // Long-polling WAITFOR
            // Expecting the message body to contain the OrderId 
            var query = $@"
                WAITFOR (
                    RECEIVE TOP(1) message_body, conversation_handle, message_type_name
                    FROM [{_queueName}]
                ), TIMEOUT 5000;";
            
            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = 0; // Infinite timeout for the command itself, but WAITFOR handles the logic

            Guid conversationHandle;
            string messageType;
            byte[]? body = null;

            using (var reader = await command.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync())
                    return; // WAITFOR timeout

                conversationHandle = reader.GetGuid(1);
                messageType = reader.GetString(2);

                if (!reader.IsDBNull(0))
                    body = (byte[])reader.GetValue(0);
            }

            if (messageType.EndsWith("EndDialog") || messageType.EndsWith("Error") || body == null)
            {
                using var endConv = new SqlCommand("END CONVERSATION @h", connection);
                endConv.Parameters.AddWithValue("@h", conversationHandle);
                await endConv.ExecuteNonQueryAsync(token);
                return;
            }

            string messageBody = Encoding.Unicode.GetString(body);

            _logger.LogInformation("Received Service Broker Message: {Body}", messageBody);

            if (int.TryParse(messageBody, out int billId))
            {
                if (CheckBill != null)
                    await CheckBill(billId);
            }

            // End conversation after successful processing
            using var endOrderConv = new SqlCommand("END CONVERSATION @h", connection);
            endOrderConv.Parameters.AddWithValue("@h", conversationHandle);
            await endOrderConv.ExecuteNonQueryAsync(token);
        }
    }
}
