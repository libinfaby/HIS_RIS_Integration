using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HIS_RIS_Integration
{
    public class RisClient
    {
        private readonly ILogger<RisClient> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        public RisClient(ILogger<RisClient> logger, IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        public async Task<bool> SendToRisAsync(string hl7Message, string host, int port, CancellationToken token = default)
        {
            try
            {
                _logger.LogInformation("Connecting to RIS at {Host}:{Port}...", host, port);
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, token);

                using var stream = client.GetStream();

                // MLLP Protocol Wrapping(Start Block: 0x0B, End Block: 0x1C + 0x0D)
                var data = Encoding.UTF8.GetBytes(hl7Message);
                var payload = new byte[data.Length + 3];

                payload[0] = 0x0B; // VT (Vertical Tab)
                Array.Copy(data, 0, payload, 1, data.Length);
                payload[payload.Length - 2] = 0x1C; // FS (File Separator)
                payload[payload.Length - 1] = 0x0D; // CR (Carriage Return)

                //await stream.WriteAsync(payload, 0, payload.Length);
                await stream.WriteAsync(payload, token);
                await stream.FlushAsync(token);

                _logger.LogInformation("HL7 message sent. Waiting for ACK...");

                // Setting timeout for incoming ACK messages
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                // Read ACK until MLLP end block
                using var ms = new MemoryStream();
                var buffer = new byte[1024];

                // Read until we encounter the MLLP end block (0x1C 0x0D)<FS><CR>
                while (true)
                {
                    int bytesRead = await stream.ReadAsync(buffer, timeoutCts.Token);
                    if (bytesRead == 0)
                        break;

                    ms.Write(buffer, 0, bytesRead);

                    var arr = ms.ToArray();
                    if (arr.Length >= 2 &&
                        arr[^2] == 0x1C &&
                        arr[^1] == 0x0D)
                    {
                        break;
                    }
                }

                var ackBytes = ms.ToArray();

                // Remove MLLP framing
                string ack = Encoding.UTF8.GetString(
                    ackBytes,
                    1,
                    ackBytes.Length - 3
                );

                _logger.LogInformation("Received HL7 Data");

                // Save to DB
                using (var scope = _scopeFactory.CreateScope())
                {
                    var repository = scope.ServiceProvider.GetRequiredService<OrderRepository>();
                    await repository.SaveIncomingHl7Async(ack);
                }

                // connection closes automatically (using TcpClient)

                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                _logger.LogWarning(
                    "RIS connection refused at {Host}:{Port}. RIS may be down or port is closed.",
                    host, port);

                return false; // graceful failure
            }
            catch (SocketException ex)
            {
                _logger.LogError(
                    ex,
                    "Network error while sending HL7 to {Host}:{Port}.",
                    host, port);

                return false;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _logger.LogWarning("ACK timeout from RIS.");
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("SendToRisAsync was cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while sending HL7 to RIS.");
                return false;
            }
        }
    }
}
