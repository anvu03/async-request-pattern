using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using AzureBusService.Api.Orders;
using AzureBusService.Api.Telemetry;
using AzureBusService.Contracts;
using AzureBusService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AzureBusService.Api.Messaging;

public sealed partial class OutboxPublisher(
    IDbContextFactory<OrdersDbContext> dbContextFactory,
    ServiceBusSender sender,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    public const string ActivitySourceName = "AzureBusService.Api.Outbox";
    private const int BatchSize = 100;
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly string leaseOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claims = await ClaimAsync(stoppingToken);
                if (claims.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                foreach (var claim in claims)
                {
                    if (await PublishAsync(claim, stoppingToken))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogIterationFailure(exception);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task<List<ClaimedOutboxMessage>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ;WITH candidates AS
            (
                SELECT TOP (100) *
                FROM [orders].[OutboxMessages] WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE [PublishedUtc] IS NULL
                  AND [QuarantinedUtc] IS NULL
                  AND [NextAttemptUtc] <= SYSUTCDATETIME()
                  AND ([LeaseExpiresUtc] IS NULL OR [LeaseExpiresUtc] <= SYSUTCDATETIME())
                ORDER BY [CreatedUtc]
            )
            UPDATE candidates
            SET [LeaseOwner] = @leaseOwner,
                [LeaseExpiresUtc] = DATEADD(minute, 1, SYSUTCDATETIME())
            OUTPUT INSERTED.[Id], INSERTED.[OrderId], INSERTED.[ContractName],
                   INSERTED.[SchemaVersion], INSERTED.[PayloadJson], INSERTED.[AttemptCount];

            SELECT COUNT_BIG(*)
            FROM [orders].[OutboxMessages] WITH (READPAST)
            WHERE [PublishedUtc] IS NULL AND [QuarantinedUtc] IS NULL;
            """;
        AddParameter(command, "@leaseOwner", leaseOwner);

        var claims = new List<ClaimedOutboxMessage>(BatchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(new ClaimedOutboxMessage(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt32(5)));
        }

        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
        {
            ApiMetrics.SetOutboxBacklog(reader.GetInt64(0));
        }

        return claims;
    }

    // Returns true after a transient failure so remaining claims can expire without hammering an unavailable bus.
    private async Task<bool> PublishAsync(ClaimedOutboxMessage claim, CancellationToken cancellationToken)
    {
        try
        {
            using var activity = ActivitySource.StartActivity("outbox.publish", ActivityKind.Producer);
            activity?.SetTag("messaging.message.id", claim.Id);
            activity?.SetTag("messaging.destination.name", sender.EntityPath);

            var message = CreateMessage(
                claim.PayloadJson,
                claim.Id,
                claim.OrderId,
                claim.ContractName,
                claim.SchemaVersion,
                activity);

            await sender.SendMessageAsync(message, cancellationToken);
            await MarkPublishedAsync(claim.Id, cancellationToken);
            ApiMetrics.OutboxPublished.Add(1);
            LogPublished(claim.Id, claim.OrderId);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = Classify(exception);
            ApiMetrics.OutboxPublishFailures.Add(1, new KeyValuePair<string, object?>("failure.code", failure.Code));
            await RecordFailureAsync(claim, failure, cancellationToken);
            LogPublishFailure(exception, claim.Id, failure.Code);
            return !failure.Permanent;
        }
    }

    private async Task MarkPublishedAsync(Guid id, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(async (dbContext, attemptCancellationToken) =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(attemptCancellationToken);
            var message = await dbContext.OutboxMessages
                .Include(candidate => candidate.Order)
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == id && candidate.LeaseOwner == leaseOwner && candidate.PublishedUtc == null,
                    attemptCancellationToken);
            if (message is null)
            {
                await transaction.RollbackAsync(attemptCancellationToken);
                return true;
            }

            var now = DateTimeOffset.UtcNow;
            message.PublishedUtc = now;
            message.LastAttemptUtc = now;
            message.AttemptCount++;
            message.LeaseOwner = null;
            message.LeaseExpiresUtc = null;
            message.LastFailureCode = null;
            message.LastFailureMessage = null;
            if (OrderStatusTransitions.CanTransition(message.Order.Status, OrderStatus.Queued))
            {
                message.Order.Status = OrderStatus.Queued;
                message.Order.UpdatedUtc = now;
            }

            await dbContext.SaveChangesAsync(attemptCancellationToken);
            await transaction.CommitAsync(attemptCancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task RecordFailureAsync(
        ClaimedOutboxMessage claim,
        PublishFailure failure,
        CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(async (dbContext, attemptCancellationToken) =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(attemptCancellationToken);
            var message = await dbContext.OutboxMessages
                .Include(candidate => candidate.Order)
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == claim.Id && candidate.LeaseOwner == leaseOwner && candidate.PublishedUtc == null,
                    attemptCancellationToken);
            if (message is null)
            {
                await transaction.RollbackAsync(attemptCancellationToken);
                return true;
            }

            var now = DateTimeOffset.UtcNow;
            var attempt = claim.AttemptCount + 1;
            message.AttemptCount = attempt;
            message.LastAttemptUtc = now;
            message.LastFailureCode = failure.Code;
            message.LastFailureMessage = failure.Message;
            message.LeaseOwner = null;
            message.LeaseExpiresUtc = null;
            if (failure.Permanent)
            {
                message.QuarantinedUtc = now;
                if (OrderStatusTransitions.CanTransition(message.Order.Status, OrderStatus.Failed))
                {
                    message.Order.Status = OrderStatus.Failed;
                    message.Order.UpdatedUtc = now;
                    message.Order.FailureCode = "publish_failed";
                    message.Order.FailureMessage = "Order publication failed.";
                }
            }
            else
            {
                message.NextAttemptUtc = now.Add(OutboxRetry.ComputeDelay(attempt, Random.Shared.NextDouble()));
            }

            await dbContext.SaveChangesAsync(attemptCancellationToken);
            await transaction.CommitAsync(attemptCancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task<TResult> ExecuteWithRetryAsync<TResult>(
        Func<OrdersDbContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        await using var strategyContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async attemptCancellationToken =>
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(attemptCancellationToken);
            return await operation(dbContext, attemptCancellationToken);
        }, cancellationToken);
    }

    private static PublishFailure Classify(Exception exception) => exception switch
    {
        JsonException or NotSupportedException =>
            new PublishFailure(true, "invalid_payload", "Outbox payload is invalid."),
        ServiceBusException serviceBusException when !serviceBusException.IsTransient =>
            new PublishFailure(true, "service_bus_permanent", "Service Bus rejected the message permanently."),
        ArgumentException or InvalidOperationException =>
            new PublishFailure(true, "service_bus_configuration", "Service Bus configuration is invalid."),
        _ => new PublishFailure(false, "service_bus_transient", "Service Bus is temporarily unavailable.")
    };

    public static ServiceBusMessage CreateMessage(
        string payloadJson,
        Guid claimId,
        Guid claimOrderId,
        string claimContractName,
        int claimSchemaVersion,
        Activity? activity = null)
    {
        var contract = JsonSerializer.Deserialize<OrderCreatedV1>(payloadJson)
            ?? throw new JsonException("Outbox payload did not contain an order contract.");
        if (contract.MessageId != claimId || contract.OrderId != claimOrderId ||
            contract.CorrelationId == Guid.Empty ||
            claimContractName != OrderCreatedV1.ContractName ||
            claimSchemaVersion != OrderCreatedV1.SchemaVersion)
        {
            throw new JsonException("Outbox payload metadata does not match its claim.");
        }

        var message = new ServiceBusMessage(BinaryData.FromString(payloadJson))
        {
            MessageId = contract.MessageId.ToString("D"),
            CorrelationId = contract.CorrelationId.ToString("D"),
            Subject = claimContractName,
            ContentType = "application/json"
        };
        message.ApplicationProperties["ContractName"] = claimContractName;
        message.ApplicationProperties["SchemaVersion"] = claimSchemaVersion;
        message.ApplicationProperties["OrderId"] = contract.OrderId.ToString("D");
        message.ApplicationProperties["CorrelationId"] = contract.CorrelationId.ToString("D");
        if (activity?.Id is { } traceParent)
        {
            message.ApplicationProperties["traceparent"] = traceParent;
        }

        if (activity?.TraceStateString is { } traceState)
        {
            message.ApplicationProperties["tracestate"] = traceState;
        }

        return message;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Outbox publisher iteration failed")]
    private partial void LogIterationFailure(Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Published outbox message {MessageId} for order {OrderId}")]
    private partial void LogPublished(Guid messageId, Guid orderId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Outbox publish failed for message {MessageId} with {FailureCode}")]
    private partial void LogPublishFailure(Exception exception, Guid messageId, string failureCode);

    private sealed record ClaimedOutboxMessage(
        Guid Id,
        Guid OrderId,
        string ContractName,
        int SchemaVersion,
        string PayloadJson,
        int AttemptCount);

    private sealed record PublishFailure(bool Permanent, string Code, string Message);
}

public static class OutboxRetry
{
    public static TimeSpan ComputeDelay(int attempt, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        ArgumentOutOfRangeException.ThrowIfLessThan(jitter, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitter, 1);
        var exponentialSeconds = Math.Min(300, Math.Pow(2, Math.Min(attempt, 9)));
        return TimeSpan.FromSeconds(Math.Min(300, exponentialSeconds * (0.5 + jitter)));
    }
}
