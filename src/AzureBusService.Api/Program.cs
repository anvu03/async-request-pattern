using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using AzureBusService.Api.Configuration;
using AzureBusService.Api.Health;
using AzureBusService.Api.Messaging;
using AzureBusService.Api.Orders;
using AzureBusService.Api.Telemetry;
using AzureBusService.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

const long MaxRequestBodySize = 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxRequestBodySize);

var exportTelemetry = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.ParseStateValues = true;
    if (exportTelemetry)
    {
        logging.AddOtlpExporter();
    }
});

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        if (!context.ProblemDetails.Extensions.ContainsKey("code"))
        {
            context.ProblemDetails.Extensions["code"] = "request_failed";
        }
    };
});
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi();
}

var connectionString = builder.Configuration.GetConnectionString("Orders");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Orders is required.");
}
builder.Services.AddPooledDbContextFactory<OrdersDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
        sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(30), null)));

var trustedProxies = builder.Configuration
    .GetSection(TrustedProxyOptions.SectionName)
    .Get<TrustedProxyOptions>() ?? new TrustedProxyOptions();
if (!TrustedProxyOptions.IsValid(trustedProxies))
{
    throw new InvalidOperationException("TrustedProxies:KnownIPs must contain valid IP addresses.");
}

if (trustedProxies.KnownIPs.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        foreach (var address in trustedProxies.KnownIPs)
        {
            options.KnownProxies.Add(IPAddress.Parse(address));
        }
    });
}

builder.Services.AddOptions<AuthenticationOptions>()
    .Bind(builder.Configuration.GetSection(AuthenticationOptions.SectionName))
    .Validate(
        AuthenticationOptions.IsValid,
        "Authority and Audience are required when enabled; anonymous access requires explicit AllowAnonymousLocal.")
    .ValidateOnStart();

var authentication = builder.Configuration
    .GetSection(AuthenticationOptions.SectionName)
    .Get<AuthenticationOptions>() ?? new AuthenticationOptions();
if (authentication.Enabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authentication.Authority;
            options.Audience = authentication.Audience;
            options.MapInboundClaims = false;
        });
    builder.Services.AddAuthorizationBuilder()
        .AddPolicy("orders", policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
            {
                return OrderOwner.TryGetAuthenticated(context.User, out _);
            }));
}

builder.Services.AddOptions<ServiceBusOptions>()
    .Bind(builder.Configuration.GetSection(ServiceBusOptions.SectionName))
    .Validate(ServiceBusOptions.IsValid, "ServiceBus must explicitly select Emulator or Azure mode and supply its required endpoint.")
    .ValidateOnStart();
var configuredServiceBus = builder.Configuration
    .GetSection(ServiceBusOptions.SectionName)
    .Get<ServiceBusOptions>() ?? new ServiceBusOptions();
if (!authentication.Enabled &&
    (!authentication.AllowAnonymousLocal || !builder.Environment.IsDevelopment() ||
     configuredServiceBus.Mode != ServiceBusMode.Emulator))
{
    throw new InvalidOperationException(
        "Anonymous API access is allowed only in the Development environment with the Service Bus emulator.");
}
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceBusOptions>>().Value;
    var clientOptions = new ServiceBusClientOptions();
    clientOptions.RetryOptions.Mode = ServiceBusRetryMode.Exponential;
    clientOptions.RetryOptions.MaxRetries = 5;
    clientOptions.RetryOptions.MaxDelay = TimeSpan.FromSeconds(10);
    clientOptions.RetryOptions.TryTimeout = TimeSpan.FromSeconds(60);

    return options.Mode switch
    {
        ServiceBusMode.Emulator => new ServiceBusClient(options.ConnectionString, clientOptions),
        ServiceBusMode.Azure => new ServiceBusClient(
            options.FullyQualifiedNamespace,
            new DefaultAzureCredential(),
            clientOptions),
        _ => throw new InvalidOperationException("Unsupported Service Bus mode.")
    };
});
builder.Services.AddSingleton(serviceProvider =>
{
    var client = serviceProvider.GetRequiredService<ServiceBusClient>();
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceBusOptions>>().Value;
    return client.CreateSender(options.QueueName);
});

builder.Services.AddSingleton<OrderService>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<OutboxCleanupService>();
builder.Services.AddHealthChecks().AddCheck<SqlHealthCheck>("sql");
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var subject = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst("sub")?.Value
            : null;
        var partition = !string.IsNullOrWhiteSpace(subject)
            ? $"subject:{subject}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        return RateLimitPartition.GetTokenBucketLimiter(partition, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 200,
            TokensPerPeriod = 100,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Rate limit exceeded",
                detail: "Try the request again later.")
            .ExecuteAsync(context.HttpContext);
    };
});

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(OutboxPublisher.ActivitySourceName);
        if (exportTelemetry)
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter(ApiMetrics.MeterName);
        if (exportTelemetry)
        {
            metrics.AddOtlpExporter();
        }
    });

var app = builder.Build();
await DatabaseSchema.EnsureCurrentAsync(
    app.Services.GetRequiredService<IDbContextFactory<OrdersDbContext>>());
if (trustedProxies.KnownIPs.Length > 0)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError
});
app.UseStatusCodePages(async statusCodeContext =>
{
    await Results.Problem(
            statusCode: statusCodeContext.HttpContext.Response.StatusCode,
            title: ReasonPhrases.GetReasonPhrase(statusCodeContext.HttpContext.Response.StatusCode))
        .ExecuteAsync(statusCodeContext.HttpContext);
});
if (authentication.Enabled)
{
    app.UseAuthentication();
}
app.UseRateLimiter();
if (authentication.Enabled)
{
    app.UseAuthorization();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/alive", () => Results.Ok(new { status = "alive" }))
    .DisableRateLimiting()
    .ExcludeFromDescription();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "sql"
}).DisableRateLimiting().ExcludeFromDescription();
app.MapOrderEndpoints(authentication.Enabled, MaxRequestBodySize);

app.Run();

public partial class Program;
