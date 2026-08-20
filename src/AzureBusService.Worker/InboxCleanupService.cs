using AzureBusService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureBusService.Worker;

public sealed class InboxCleanupOptions
{
    public const string SectionName = "Inbox";

    public int RetentionDays { get; init; } = 30;
}

public sealed partial class InboxCleanupService(
    IDbContextFactory<OrdersDbContext> dbContextFactory,
    IOptions<InboxCleanupOptions> options,
    TimeProvider timeProvider,
    ILogger<InboxCleanupService> logger) : BackgroundService
{
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), timeProvider);
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
        var cutoff = timeProvider.GetUtcNow().AddDays(-options.Value.RetentionDays);
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var ids = await dbContext.InboxMessages
                .Where(message => message.ProcessedUtc < cutoff)
                .OrderBy(message => message.ProcessedUtc)
                .Select(message => message.MessageId)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken);
            if (ids.Length == 0)
            {
                return;
            }

            var deleted = await dbContext.InboxMessages
                .Where(message => ids.Contains(message.MessageId))
                .ExecuteDeleteAsync(cancellationToken);
            LogDeleted(deleted);
            if (deleted < BatchSize)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Error, Message = "Inbox cleanup failed")]
    private partial void LogCleanupFailure(Exception exception);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Deleted {InboxMessageCount} expired inbox messages")]
    private partial void LogDeleted(int inboxMessageCount);
}
