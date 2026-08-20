using System.Text.Json;
using AzureBusService.Contracts;

namespace AzureBusService.Worker;

public sealed record DecodedOrderMessage(
    string? MessageId,
    string? Subject,
    string? ContractName,
    object? SchemaVersion,
    object? OrderId,
    object? CorrelationId,
    string? TraceParent,
    string? TraceState,
    ReadOnlyMemory<byte> Body);

public sealed record MessageValidationResult(
    OrderCreatedV1? Message,
    Guid? TrustedOrderId,
    string? DeadLetterReason,
    string? Diagnostic)
{
    public bool IsValid => Message is not null;
}

public sealed class OrderMessageValidator
{
    private const int MaxBodyBytes = 256 * 1024;
    private const decimal MaxSqlDecimal = 999_999_999_999_999.9999m;

    public MessageValidationResult Validate(DecodedOrderMessage input)
    {
        var trustedOrderId = ParseGuid(input.OrderId);
        if (input.Body.IsEmpty || input.Body.Length > MaxBodyBytes)
        {
            return Invalid(null, "InvalidMessageSize", "Message body size is invalid.");
        }

        if (!string.Equals(input.Subject, OrderCreatedV1.ContractName, StringComparison.Ordinal) ||
            !string.Equals(input.ContractName, OrderCreatedV1.ContractName, StringComparison.Ordinal) ||
            !string.Equals(input.Subject, input.ContractName, StringComparison.Ordinal))
        {
            return Invalid(trustedOrderId, "ContractMismatch", "Message contract metadata is invalid.");
        }

        if (ParseSchemaVersion(input.SchemaVersion) != OrderCreatedV1.SchemaVersion)
        {
            return Invalid(trustedOrderId, "UnknownSchemaVersion", "Message schema version is not supported.");
        }

        if (!Guid.TryParse(input.MessageId, out var envelopeMessageId) || envelopeMessageId == Guid.Empty)
        {
            return Invalid(trustedOrderId, "MetadataMismatch", "Message identity metadata is invalid.");
        }

        if (trustedOrderId is null)
        {
            return Invalid(null, "MetadataMismatch", "Message identity metadata is invalid.");
        }

        var correlationId = ParseGuid(input.CorrelationId);
        if (correlationId is null)
        {
            return Invalid(trustedOrderId, "MetadataMismatch", "Message correlation metadata is invalid.");
        }

        try
        {
            var message = JsonSerializer.Deserialize<OrderCreatedV1>(input.Body.Span);
            if (message is null || !HasValidBusinessData(message))
            {
                return Invalid(trustedOrderId, "MalformedMessage", "Message body is malformed.");
            }

            if (message.MessageId != envelopeMessageId || message.OrderId != trustedOrderId.Value ||
                message.CorrelationId != correlationId.Value)
            {
                return Invalid(trustedOrderId, "MetadataMismatch", "Message metadata does not match its body.");
            }

            return new MessageValidationResult(message, trustedOrderId, null, null);
        }
        catch (JsonException)
        {
            return Invalid(trustedOrderId, "MalformedMessage", "Message body is malformed.");
        }
        catch (NotSupportedException)
        {
            return Invalid(trustedOrderId, "MalformedMessage", "Message body is malformed.");
        }
    }

    private static bool HasValidBusinessData(OrderCreatedV1 message)
    {
        if (message.MessageId == Guid.Empty || message.OrderId == Guid.Empty ||
            message.CorrelationId == Guid.Empty || message.CustomerId == Guid.Empty ||
            message.CreatedUtc == default || message.CreatedUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrEmpty(message.Currency) || message.Currency.Length != 3 ||
            message.Currency.Any(character => character is < 'A' or > 'Z') ||
            message.Items.IsDefault || message.Items.Length is < 1 or > 100)
        {
            return false;
        }

        var products = new HashSet<Guid>();
        decimal total = 0;
        try
        {
            foreach (var item in message.Items)
            {
                if (item is null || item.ProductId == Guid.Empty || !products.Add(item.ProductId) ||
                    item.Quantity is < 1 or > 1000 || item.UnitPrice <= 0 ||
                    item.UnitPrice > MaxSqlDecimal ||
                    decimal.Round(item.UnitPrice, 4) != item.UnitPrice)
                {
                    return false;
                }

                total += item.Quantity * item.UnitPrice;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return total <= MaxSqlDecimal && total == message.Total &&
            decimal.Round(message.Total, 4) == message.Total;
    }

    private static MessageValidationResult Invalid(Guid? orderId, string reason, string diagnostic) =>
        new(null, orderId, reason, diagnostic);

    private static int? ParseSchemaVersion(object? value) => value switch
    {
        byte number => number,
        short number => number,
        int number => number,
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        _ => null,
    };

    private static Guid? ParseGuid(object? value) => value switch
    {
        Guid id when id != Guid.Empty => id,
        string text when Guid.TryParse(text, out var id) && id != Guid.Empty => id,
        _ => null,
    };
}
