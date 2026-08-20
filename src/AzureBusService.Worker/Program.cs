using Azure.Identity;
using Azure.Messaging.ServiceBus;
using AzureBusService.Persistence;
using AzureBusService.Worker;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffK";
});

var sqlConnectionString = builder.Configuration.GetConnectionString("Orders");
if (string.IsNullOrWhiteSpace(sqlConnectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Orders is required.");
}

var serviceBusOptions = builder.Configuration.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>()
    ?? throw new InvalidOperationException("ServiceBus configuration is required.");
serviceBusOptions.Validate();

builder.Services.AddSingleton(serviceBusOptions);
builder.Services.AddDbContextFactory<OrdersDbContext>(options => options.UseSqlServer(
    sqlConnectionString,
    sqlOptions => sqlOptions.EnableRetryOnFailure(
        maxRetryCount: 5,
        maxRetryDelay: TimeSpan.FromSeconds(30),
        errorNumbersToAdd: null)));
builder.Services.AddSingleton(static provider => CreateServiceBusClient(provider.GetRequiredService<ServiceBusOptions>()));
builder.Services.AddSingleton(static provider =>
{
    var options = provider.GetRequiredService<ServiceBusOptions>();
    return provider.GetRequiredService<ServiceBusClient>().CreateReceiver(options.QueueName);
});
builder.Services.AddSingleton(static provider =>
{
    var options = provider.GetRequiredService<ServiceBusOptions>();
    return provider.GetRequiredService<ServiceBusClient>().CreateProcessor(
        options.QueueName,
        new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = options.MaxConcurrentCalls,
            PrefetchCount = 32,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5),
        });
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OrderMessageValidator>();
builder.Services.AddSingleton<OrderMessageProcessor>();
builder.Services.AddHostedService<ServiceBusWorker>();
builder.Services.AddOptions<InboxCleanupOptions>()
    .Bind(builder.Configuration.GetSection(InboxCleanupOptions.SectionName))
    .Validate(options => options.RetentionDays > 0, "Inbox:RetentionDays must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddHostedService<InboxCleanupService>();

builder.Services
    .AddHealthChecks()
    .AddCheck<SqlHealthCheck>("sql", tags: ["ready"])
    .AddCheck<ServiceBusHealthCheck>("service-bus", tags: ["ready"]);

var exportOtlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
var openTelemetry = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(WorkerTelemetry.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        if (exportOtlp)
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(WorkerTelemetry.MeterName)
            .AddRuntimeInstrumentation();
        if (exportOtlp)
        {
            metrics.AddOtlpExporter();
        }
    });
if (exportOtlp)
{
    openTelemetry.WithLogging(
        logging => logging.AddOtlpExporter(),
        options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
        });
}

var app = builder.Build();
await DatabaseSchema.EnsureCurrentAsync(
    app.Services.GetRequiredService<IDbContextFactory<OrdersDbContext>>());
app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = static registration => registration.Tags.Contains("ready"),
});

await app.RunAsync();

static ServiceBusClient CreateServiceBusClient(ServiceBusOptions options)
{
    var clientOptions = new ServiceBusClientOptions
    {
        RetryOptions = new ServiceBusRetryOptions
        {
            Mode = ServiceBusRetryMode.Exponential,
            MaxRetries = 5,
            MaxDelay = TimeSpan.FromSeconds(10),
            TryTimeout = TimeSpan.FromSeconds(60),
        },
    };

    return options.Mode switch
    {
        ServiceBusMode.Emulator => new ServiceBusClient(options.ConnectionString, clientOptions),
        ServiceBusMode.Azure => new ServiceBusClient(
            options.FullyQualifiedNamespace,
            new DefaultAzureCredential(),
            clientOptions),
        _ => throw new InvalidOperationException("ServiceBus:Mode must be Emulator or Azure."),
    };
}
