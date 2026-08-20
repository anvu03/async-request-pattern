using System.Collections.Immutable;
using System.Text.Json;
using AzureBusService.Contracts;
using AzureBusService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AzureBusService.Api.Orders;

public sealed class OrderService(IDbContextFactory<OrdersDbContext> dbContextFactory)
{
    public async Task<OrderCreationResult> CreateAsync(
        string ownerSubject,
        string idempotencyKey,
        ValidatedOrderRequest request,
        CancellationToken cancellationToken)
    {
        var fingerprint = OrderRequestValidator.ComputeFingerprint(request);
        await using var strategyContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() =>
            CreateAttemptAsync(ownerSubject, idempotencyKey, request, fingerprint, cancellationToken));
    }

    private async Task<OrderCreationResult> CreateAttemptAsync(
        string ownerSubject,
        string idempotencyKey,
        ValidatedOrderRequest request,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.Orders.AsNoTracking()
            .SingleOrDefaultAsync(
                order => order.OwnerSubject == ownerSubject && order.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            return Existing(existing, fingerprint);
        }

        var now = DateTimeOffset.UtcNow;
        var orderId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();
        var order = new Order
        {
            Id = orderId,
            CustomerId = request.CustomerId,
            Status = OrderStatus.Pending,
            Currency = request.Currency,
            Total = request.Total,
            OwnerSubject = ownerSubject,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = fingerprint,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var contractItems = ImmutableArray.CreateBuilder<OrderCreatedItemV1>(request.Items.Length);
        foreach (var item in request.Items)
        {
            order.Items.Add(new OrderItem
            {
                Id = Guid.CreateVersion7(),
                OrderId = orderId,
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Order = order
            });
            contractItems.Add(new OrderCreatedItemV1(item.ProductId, item.Quantity, item.UnitPrice));
        }

        var contract = new OrderCreatedV1(
            messageId,
            orderId,
            Guid.CreateVersion7(),
            now,
            request.CustomerId,
            request.Currency,
            request.Total,
            contractItems.MoveToImmutable());
        var outboxMessage = new OutboxMessage
        {
            Id = messageId,
            OrderId = orderId,
            ContractName = OrderCreatedV1.ContractName,
            SchemaVersion = OrderCreatedV1.SchemaVersion,
            PayloadJson = JsonSerializer.Serialize(contract),
            CreatedUtc = now,
            NextAttemptUtc = now,
            Order = order
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            dbContext.Orders.Add(order);
            dbContext.OutboxMessages.Add(outboxMessage);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new OrderCreationResult(OrderCreationOutcome.Accepted, orderId, now);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            existing = await dbContext.Orders.AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.OwnerSubject == ownerSubject && candidate.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return Existing(existing, fingerprint);
        }
    }

    private static OrderCreationResult Existing(Order order, byte[] fingerprint) =>
        new(
            order.RequestFingerprint.AsSpan().SequenceEqual(fingerprint)
                ? OrderCreationOutcome.Accepted
                : OrderCreationOutcome.Conflict,
            order.Id,
            order.CreatedUtc);
}
