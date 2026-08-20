using System.Data;
using System.Text.Json;
using AzureBusService.Contracts;
using AzureBusService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AzureBusService.Worker;

public enum MessageDisposition
{
    Complete,
    DeadLetter,
}

public sealed record MessageProcessingResult(
    MessageDisposition Disposition,
    string? DeadLetterReason = null,
    string? Diagnostic = null,
    bool IsDuplicate = false);

public sealed class OrderMessageProcessor(
    IDbContextFactory<OrdersDbContext> dbContextFactory,
    OrderMessageValidator validator,
    TimeProvider timeProvider)
{
    public async Task<MessageProcessingResult> ProcessAsync(
        DecodedOrderMessage input,
        CancellationToken cancellationToken)
    {
        var validation = validator.Validate(input);
        if (validation.Message is null)
        {
            return new(MessageDisposition.DeadLetter, validation.DeadLetterReason, validation.Diagnostic);
        }

        return await ProcessValidMessageAsync(validation.Message, cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid? orderId,
        string failureCode,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        if (orderId is null)
        {
            return;
        }

        await ExecuteWithRetryAsync(async (dbContext, attemptCancellationToken) =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                attemptCancellationToken);
            var order = await dbContext.Orders.SingleOrDefaultAsync(
                candidate => candidate.Id == orderId.Value,
                attemptCancellationToken);
            if (order is not null && OrderStateTransitions.CanFail(order.Status))
            {
                order.Status = OrderStatus.Failed;
                order.FailureCode = failureCode;
                order.FailureMessage = failureMessage;
                order.UpdatedUtc = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(attemptCancellationToken);
            }

            await transaction.CommitAsync(attemptCancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task<MessageProcessingResult> ProcessValidMessageAsync(
        OrderCreatedV1 message,
        CancellationToken cancellationToken)
    {
        return await ExecuteWithRetryAsync(async (dbContext, attemptCancellationToken) =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                attemptCancellationToken);

            if (await dbContext.InboxMessages.AsNoTracking().AnyAsync(
                    inbox => inbox.MessageId == message.MessageId,
                    attemptCancellationToken))
            {
                await transaction.CommitAsync(attemptCancellationToken);
                return new MessageProcessingResult(MessageDisposition.Complete, IsDuplicate: true);
            }

            var outbox = await dbContext.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == message.MessageId,
                attemptCancellationToken);
            if (!MessageProvenance.Matches(outbox, message))
            {
                await transaction.RollbackAsync(attemptCancellationToken);
                return new MessageProcessingResult(
                    MessageDisposition.DeadLetter,
                    "MessageProvenanceFailed",
                    "Message does not match a persisted outbox record.");
            }

            var order = await dbContext.Orders.SingleOrDefaultAsync(
                candidate => candidate.Id == message.OrderId,
                attemptCancellationToken);
            if (order is null)
            {
                await transaction.RollbackAsync(attemptCancellationToken);
                return new MessageProcessingResult(
                    MessageDisposition.DeadLetter,
                    "OrderNotFound",
                    "Order does not exist.");
            }

            if (order.Status == OrderStatus.Failed)
            {
                await transaction.RollbackAsync(attemptCancellationToken);
                return new MessageProcessingResult(
                    MessageDisposition.DeadLetter,
                    "OrderFailed",
                    "Order is already in a failed state.");
            }

            if (order.Status == OrderStatus.Completed)
            {
                await transaction.RollbackAsync(attemptCancellationToken);
                return new MessageProcessingResult(
                    MessageDisposition.DeadLetter,
                    "InvalidOrderState",
                    "Order cannot accept this message.");
            }

            var now = timeProvider.GetUtcNow();
            order.Status = OrderStateTransitions.BeginProcessing(order.Status);
            order.UpdatedUtc = now;
            await dbContext.SaveChangesAsync(attemptCancellationToken);

            order.Status = OrderStateTransitions.Complete(order.Status);
            order.FailureCode = null;
            order.FailureMessage = null;
            order.UpdatedUtc = now;

            dbContext.InboxMessages.Add(new InboxMessage
            {
                MessageId = message.MessageId,
                ContractName = OrderCreatedV1.ContractName,
                SchemaVersion = OrderCreatedV1.SchemaVersion,
                ReceivedUtc = now,
                ProcessedUtc = now,
            });
            await dbContext.SaveChangesAsync(attemptCancellationToken);
            await transaction.CommitAsync(attemptCancellationToken);
            return new MessageProcessingResult(MessageDisposition.Complete);
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
}

public static class MessageProvenance
{
    public static bool Matches(OutboxMessage? outbox, OrderCreatedV1 message) =>
        outbox is not null &&
        outbox.OrderId == message.OrderId &&
        outbox.ContractName == OrderCreatedV1.ContractName &&
        outbox.SchemaVersion == OrderCreatedV1.SchemaVersion &&
        outbox.QuarantinedUtc is null &&
        string.Equals(outbox.PayloadJson, JsonSerializer.Serialize(message), StringComparison.Ordinal);
}
