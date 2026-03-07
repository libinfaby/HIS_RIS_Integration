using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HIS_RIS_Integration
{
    public class RisListener
    {
        private readonly ILogger<RisListener> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private TcpListener? _listener;
        private readonly HL7Service _hl7Service;
        private readonly OrderRepository _orderRepository;
        private readonly string _hospitalname;

        public RisListener(
            ILogger<RisListener> logger, 
            IServiceScopeFactory scopeFactory, 
            HL7Service hl7Service, 
            OrderRepository orderRepository, 
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _hl7Service = hl7Service;
            _orderRepository = orderRepository;
            _hospitalname = configuration.GetValue<string>("Database:Hospital") ?? "";   
        }

        public async Task StartListeningAsync(int port, CancellationToken stoppingToken)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _logger.LogInformation("RIS Listener started on port {Port}.", port);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                    _ = HandleClientAsync(client, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RIS Listener.");
            }
            finally
            {
                _listener?.Stop();
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            try
            {
                _logger.LogInformation("Accepted connection from {Client}.", client.Client.RemoteEndPoint);
                using var stream = client.GetStream();
                var buffer = new byte[4096];
                var messageBytes = new List<byte>();
                bool foundEnd = false;

                while (!foundEnd && !token.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);

                    if (bytesRead == 0)
                        break; // Connection closed

                    for (int i = 0; i < bytesRead; i++)
                    {
                        messageBytes.Add(buffer[i]);

                        // Check for end sequence: 0x1C 0x0D
                        if (messageBytes.Count >= 2 &&
                            messageBytes[messageBytes.Count - 2] == 0x1C &&
                            messageBytes[messageBytes.Count - 1] == 0x0D)
                        {
                            foundEnd = true;
                            break;
                        }
                    }
                }

                if (messageBytes.Count > 0)
                {
                    // Decode
                    // Strip MLLP framing: remove 0x0B at start and 0x1C 0x0D at end
                    int startIndex = (messageBytes[0] == 0x0B) ? 1 : 0;
                    int length = messageBytes.Count - startIndex - 2; // Remove trailing 0x1C 0x0D

                    string receivedData = Encoding.UTF8.GetString(messageBytes.ToArray(), startIndex, length);


                    _logger.LogInformation("Received HL7 Data");

                    // Save to DB
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var repository = scope.ServiceProvider.GetRequiredService<OrderRepository>();
                        await repository.SaveIncomingHl7Async(receivedData);
                    }

                    string? messageType = HL7Service.ExtractMessageTypeFromHl7(receivedData);

                    if (messageType != null)
                    {
                        if (messageType.Contains('^') && messageType.Split('^')[0] == "ORU")
                        {
                            // *******IMPLEMENT SAVE REPORT DATA TO DB
                            TestReport? report = _hl7Service.ParseIncomingHL7Message(receivedData);
                            if (report == null)
                                return;

                            await _orderRepository.InsertReportAsync(report);

                            string? messageControlId = HL7Service.ExtractMessageControlIdFromHl7(receivedData);
                            var sb = new StringBuilder();

                            sb.Append($"\v"); // <VT>

                            // MSH Segment
                            sb.Append($"\vMSH|^~\\&|RIS|RIS|HIS|{_hospitalname}|{DateTime.Now:yyyyMMddHHmmss}||ACK^O01|{Guid.NewGuid()}|P|2.3");
                            sb.Append('\r');

                            // MSA Segment
                            sb.Append($"MSA|AA|{messageControlId}");
                            sb.Append($"\x1C\x0D"); // <FS><CR>

                            // Send ACK
                            //string ack = $"\x1C\x0D";
                            byte[] ackBytes = Encoding.UTF8.GetBytes(sb.ToString());
                            await stream.WriteAsync(ackBytes, 0, ackBytes.Length, token);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling RIS client connection.");
            }
            finally
            {
                client.Close();
            }
        }


        //private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        //{
        //    try
        //    {
        //        _logger.LogInformation("Accepted connection from {Client}.", client.Client.RemoteEndPoint);
        //        using var stream = client.GetStream();
        //        var buffer = new byte[4096];

        //        // Simple read - in production, need to handle MLLP framing robustly (buffering until 0x1C 0x0D)
        //        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);

        //        if (bytesRead > 0)
        //        {
        //            // Strip MLLP chars for simple logs
        //            // 0x0B ... 0x1C 0x0D

        //            // Decode
        //            string receivedData = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        //            // Simple cleanup for logging (remove non-printables)
        //            //string cleanData = receivedData.Replace("\x0B", "").Replace("\x1C", "").Replace("\x0D", "\n");
        //            _logger.LogInformation("Received HL7 Data");

        //            // Save to DB
        //            using (var scope = _scopeFactory.CreateScope())
        //            {
        //                var repository = scope.ServiceProvider.GetRequiredService<OrderRepository>();
        //                await repository.SaveIncomingHl7Async(receivedData);
        //            }

        //            string? messageType = HL7Service.ExtractMessageTypeFromHl7(receivedData);

        //            if (messageType != null)
        //            { 
        //                if (messageType.Contains('^') && messageType.Split('^')[0] == "ORU")
        //                {
        //                    // *******IMPLEMENT SAVE REPORT DATA TO DB
        //                    TestReport? report = _hl7Service.ParseIncomingHL7Message(receivedData);
        //                    if (report == null)
        //                        return;

        //                    await _orderRepository.InsertReportAsync(report);

        //                    string? messageControlId = HL7Service.ExtractMessageControlIdFromHl7(receivedData);
        //                    var sb = new StringBuilder();

        //                    sb.Append($"\v"); // <VT>

        //                    // MSH Segment
        //                    sb.Append($"\vMSH|^~\\&|RIS|RIS|HIS|{_hospitalname}|{DateTime.Now:yyyyMMddHHmmss}||ACK^O01|{Guid.NewGuid()}|P|2.3");
        //                    sb.Append('\r');

        //                    // MSA Segment
        //                    sb.Append($"MSA|AA|{messageControlId}");
        //                    sb.Append($"\x1C\x0D"); // <FS><CR>

        //                    // Send ACK
        //                    //string ack = $"\x1C\x0D";
        //                    byte[] ackBytes = Encoding.UTF8.GetBytes(sb.ToString());
        //                    await stream.WriteAsync(ackBytes, 0, ackBytes.Length, token);
        //                }
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error handling RIS client connection.");
        //    }
        //    finally
        //    {
        //        client.Close();
        //    }
        //}
    }
}
