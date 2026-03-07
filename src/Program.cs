using HIS_RIS_Integration;
using Serilog;
using Microsoft.Extensions.Hosting.WindowsServices;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var options = new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default
    };

    var builder = Host.CreateApplicationBuilder(options);
    
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "HISRISIntegration";
    });

    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(services.GetRequiredService<IConfiguration>()));

    // Register Services
    builder.Services.AddSingleton<OrderRepository>();
    builder.Services.AddSingleton<HL7Service>();
    builder.Services.AddSingleton<RisClient>();
    builder.Services.AddSingleton<RisListener>();
    builder.Services.AddSingleton<ServiceBrokerListener>();

    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}