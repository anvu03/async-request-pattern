using Azure.Messaging.ServiceBus;
using AzureBusService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureBusService.Worker;

public sealed class SqlHealthCheck(IDbContextFactory<OrdersDbContext> dbContextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("SQL connection failed.");
    }
}

public sealed class ServiceBusHealthCheck(ServiceBusReceiver receiver) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await receiver.PeekMessageAsync(cancellationToken: cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
