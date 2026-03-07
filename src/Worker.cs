using System.Diagnostics.Eventing.Reader;

namespace HIS_RIS_Integration
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;
        private readonly ServiceBrokerListener _brokerListener;
        private readonly RisListener _risListener;
        private readonly OrderRepository _orderRepository;
        private readonly HL7Service _hl7Service;
        private readonly RisClient _risClient;

        public Worker(
            ILogger<Worker> logger,
            IConfiguration configuration,
            ServiceBrokerListener brokerListener,
            RisListener risListener,
            OrderRepository orderRepository,
            HL7Service hl7Service,
            RisClient risClient)
        {
            _logger = logger;
            _configuration = configuration;
            _brokerListener = brokerListener;
            _risListener = risListener;
            _orderRepository = orderRepository;
            _hl7Service = hl7Service;
            _risClient = risClient;
        }

        /// <summary>
        /// Starts long-running background listeners for incoming RIS messages
        /// and SQL Service Broker message processing. These listeners run for
        /// the lifetime of the hosted service and operate independently without
        /// blocking application startup.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Integration Service Started");

            _brokerListener.CheckBill += ProcessNewOrderAsync;

            // Starting RIS Listener which waits for incoming HL7 messages
            int risListeningPort = _configuration.GetValue<int>("HIS:Port");
            _ = Task.Run(
                () => _risListener.StartListeningAsync(risListeningPort, stoppingToken),
                stoppingToken
            );

            // Starting Service Broker Listener which waits for bill events
            _ = Task.Run(
                () => _brokerListener.StartListeningAsync(stoppingToken),
                stoppingToken
            );

            // Keep service alive without blocking startup
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }

            _logger.LogInformation("Integration Service stopping");
        }


        private async Task ProcessNewOrderAsync(int billId)
        {
            try
            {
                _logger.LogInformation("Processing Bill ID: {BillId}", billId);

                // 1. Get Order Details
                var order = await _orderRepository.GetOrderDetailsAsync(billId);
                if (order == null)
                {
                    _logger.LogWarning("Bill ID {BillId} not found in database.", billId);
                    return;
                }

                var eventType = order.BillStatus != "CLD" ? "BILL_GENERATED" : "BILL_CANCELLED";

                // 2. Log bill generated / cancelled event to DB
                var eventId = await _orderRepository.LogOrderEventToDB(billId, eventType);

                foreach (var item in order.TestOrderItems)
                {
                    // 3. Generate HL7
                    string hl7Message = _hl7Service.GenerateOrderMessage(order, item);

                    _logger.LogInformation("Generated HL7 Message for Order {OrderId} from Bill {BillId}.", item.OrderId, order.BillId);

                    var messageId = 0;
                    // 4. Log HL7 generated event to DB
                    messageId = await _orderRepository.LogHL7InsertEventToDB(hl7Message, eventId, "PENDING");

                    // 5. Send to RIS
                    string risHost = _configuration.GetValue<string>("RIS:Host")!;
                    int risPort = _configuration.GetValue<int>("RIS:Port");

                    bool sent = await _risClient.SendToRisAsync(hl7Message, risHost, risPort);
                    if (sent)
                    {
                        await _orderRepository.LogHL7UpdateEventToDB(messageId, "SENT");
                        if (order.BillStatus == "CLD")
                        {
                            _logger.LogInformation("Successfully pushed CANCEL Order {OrderId} from Bill {BillId} to RIS.", item.OrderId, order.BillId);
                        }
                        else
                        {
                            _logger.LogInformation("Successfully pushed NEW Order {OrderId} from Bill {BillId} to RIS.", item.OrderId, order.BillId);
                        }
                    }
                    else
                    {
                        await _orderRepository.LogHL7UpdateEventToDB(messageId, "FAILED");
                        _logger.LogError("Failed to push Order {OrderId} to RIS.", item.OrderId);
                    }
                }
            }
            catch (Exception ex)
            {
                await _orderRepository.LogOrderEventToDB(billId, "BILL_GENERATED");
                _logger.LogError(ex, "Error processing Order ID: {BillId}", billId);
            }
        }
    }
}
