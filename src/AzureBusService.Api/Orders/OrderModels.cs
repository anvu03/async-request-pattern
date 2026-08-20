using System.Collections.Immutable;
using AzureBusService.Persistence;

namespace AzureBusService.Api.Orders;

public sealed record CreateOrderRequest(
    string? CustomerId,
    string? Currency,
    IReadOnlyList<CreateOrderItemRequest>? Items);

public sealed record CreateOrderItemRequest(
    string? ProductId,
    int Quantity,
    decimal UnitPrice);

public sealed record AcceptedOrderResponse(
    Guid OrderId,
    string Status,
    string StatusUrl,
    DateTimeOffset CreatedUtc,
    int RetryAfterSeconds);

public sealed record OrderStatusResponse(
    Guid OrderId,
    Guid CustomerId,
    string Currency,
    decimal Total,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    OrderFailureResponse? Failure);

public sealed record OrderFailureResponse(string Code, string Message)
{
    public static OrderFailureResponse FromStored(string? code, string? message) => new(
        string.IsNullOrWhiteSpace(code) ? "order_failed" : code,
        string.IsNullOrWhiteSpace(message) ? "Order processing failed." : message);
}

public sealed record ValidatedOrderRequest(
    Guid CustomerId,
    string Currency,
    ImmutableArray<ValidatedOrderItem> Items,
    decimal Total);

public sealed record ValidatedOrderItem(Guid ProductId, int Quantity, decimal UnitPrice);

public sealed record OrderRequestValidationResult(
    ValidatedOrderRequest? Value,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Value is not null;
}

public enum OrderCreationOutcome
{
    Accepted,
    Conflict
}

public sealed record OrderCreationResult(
    OrderCreationOutcome Outcome,
    Guid OrderId,
    DateTimeOffset CreatedUtc);

public static class OrderStatusTransitions
{
    public static bool CanTransition(OrderStatus current, OrderStatus next) => current switch
    {
        OrderStatus.Pending => next is OrderStatus.Queued or OrderStatus.Failed,
        OrderStatus.Queued => next is OrderStatus.Processing or OrderStatus.Failed,
        OrderStatus.Processing => next is OrderStatus.Completed or OrderStatus.Failed,
        _ => false
    };
}

public static class IdempotencyKey
{
    public static bool TryNormalize(string? value, out string? normalized, out string error)
    {
        normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = null;
            error = "A non-empty Idempotency-Key header is required.";
            return false;
        }

        if (normalized.Length > 128)
        {
            error = "Idempotency-Key must not exceed 128 characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
