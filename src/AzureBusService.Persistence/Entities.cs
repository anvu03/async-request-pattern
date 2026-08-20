namespace AzureBusService.Persistence;

public sealed class Order
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public OrderStatus Status { get; set; }
    public required string Currency { get; set; }
    public decimal Total { get; set; }
    public required string OwnerSubject { get; set; }
    public required string IdempotencyKey { get; set; }
    public required byte[] RequestFingerprint { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<OrderItem> Items { get; } = [];
}

public sealed class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public Order Order { get; set; } = null!;
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public required string ContractName { get; set; }
    public int SchemaVersion { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? PublishedUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastFailureCode { get; set; }
    public string? LastFailureMessage { get; set; }
    public DateTimeOffset? QuarantinedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public Order Order { get; set; } = null!;
}

public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public required string ContractName { get; set; }
    public int SchemaVersion { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }
    public DateTimeOffset ProcessedUtc { get; set; }
}
