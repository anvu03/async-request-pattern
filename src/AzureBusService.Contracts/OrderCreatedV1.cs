using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AzureBusService.Contracts;

public sealed record OrderCreatedV1(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("orderId")] Guid OrderId,
    [property: JsonPropertyName("correlationId")] Guid CorrelationId,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("customerId")] Guid CustomerId,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("items")] ImmutableArray<OrderCreatedItemV1> Items)
{
    public const string ContractName = "OrderCreatedV1";
    public const int SchemaVersion = 1;
}

public sealed record OrderCreatedItemV1(
    [property: JsonPropertyName("productId")] Guid ProductId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("unitPrice")] decimal UnitPrice);
