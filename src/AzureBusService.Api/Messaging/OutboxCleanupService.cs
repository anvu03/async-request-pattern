using AzureBusService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AzureBusService.Api.Messaging;

public sealed partial class OutboxCleanupService(
    IDbContextFactory<OrdersDbContext> dbContextFactory,
    ILogger<OutboxCleanupService> logger) : BackgroundService
{
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await DeleteExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogCleanupFailure(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var ids = await dbContext.OutboxMessages
                .Where(message => message.PublishedUtc < cutoff)
                .OrderBy(message => message.PublishedUtc)
                .Select(message => message.Id)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken);
            if (ids.Length == 0)
            {
                return;
            }

            var deleted = await dbContext.OutboxMessages
                .Where(message => ids.Contains(message.Id))
                .ExecuteDeleteAsync(cancellationToken);
            LogDeleted(deleted);
            if (deleted < BatchSize)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Published outbox cleanup failed")]
    private partial void LogCleanupFailure(Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Deleted {OutboxMessageCount} expired outbox messages")]
    private partial void LogDeleted(int outboxMessageCount);
}
