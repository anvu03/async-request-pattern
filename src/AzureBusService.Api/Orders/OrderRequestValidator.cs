using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AzureBusService.Api.Orders;

public static class OrderRequestValidator
{
    private const decimal MaxSqlDecimal = 999_999_999_999_999.9999m;

    public static OrderRequestValidationResult Validate(CreateOrderRequest? request)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (request is null)
        {
            AddError(errors, "$", "A request body is required.");
            return Invalid(errors);
        }

        var customerId = ParseUuid(request.CustomerId, "customerId", errors);
        if (request.Currency is null || request.Currency.Length != 3 ||
            request.Currency.Any(character => character is < 'A' or > 'Z'))
        {
            AddError(errors, "currency", "Currency must be a 3-letter uppercase ISO code.");
        }

        if (request.Items is null || request.Items.Count is < 1 or > 100)
        {
            AddError(errors, "items", "Items must contain between 1 and 100 products.");
        }

        var validatedItems = ImmutableArray.CreateBuilder<ValidatedOrderItem>();
        var productIds = new HashSet<Guid>();
        decimal total = 0;
        var totalOverflow = false;
        if (request.Items is not null)
        {
            for (var index = 0; index < request.Items.Count; index++)
            {
                var item = request.Items[index];
                var productId = ParseUuid(item.ProductId, $"items[{index}].productId", errors);
                if (productId != Guid.Empty && !productIds.Add(productId))
                {
                    AddError(errors, $"items[{index}].productId", "Product IDs must be unique.");
                }

                if (item.Quantity is < 1 or > 1000)
                {
                    AddError(errors, $"items[{index}].quantity", "Quantity must be between 1 and 1000.");
                }

                if (item.UnitPrice <= 0 || item.UnitPrice > MaxSqlDecimal || !HasAtMostFourFractionalDigits(item.UnitPrice))
                {
                    AddError(errors, $"items[{index}].unitPrice", "Unit price must be positive and fit decimal(19,4).");
                }

                if (item.Quantity is >= 1 and <= 1000 && item.UnitPrice > 0 &&
                    HasAtMostFourFractionalDigits(item.UnitPrice))
                {
                    try
                    {
                        total = checked(total + checked(item.Quantity * item.UnitPrice));
                    }
                    catch (OverflowException)
                    {
                        totalOverflow = true;
                    }
                }

                if (productId != Guid.Empty && item.Quantity is >= 1 and <= 1000 &&
                    item.UnitPrice > 0 && item.UnitPrice <= MaxSqlDecimal && HasAtMostFourFractionalDigits(item.UnitPrice))
                {
                    validatedItems.Add(new ValidatedOrderItem(productId, item.Quantity, item.UnitPrice));
                }
            }
        }

        if (totalOverflow || total > MaxSqlDecimal)
        {
            AddError(errors, "total", "Order total must fit decimal(19,4).");
        }

        if (errors.Count != 0)
        {
            return Invalid(errors);
        }

        return new OrderRequestValidationResult(
            new ValidatedOrderRequest(customerId, request.Currency!, validatedItems.ToImmutable(), total),
            new Dictionary<string, string[]>());
    }

    public static byte[] ComputeFingerprint(ValidatedOrderRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("customerId", request.CustomerId.ToString("D"));
            writer.WriteString("currency", request.Currency);
            writer.WriteStartArray("items");
            foreach (var item in request.Items.OrderBy(item => item.ProductId))
            {
                writer.WriteStartObject();
                writer.WriteString("productId", item.ProductId.ToString("D"));
                writer.WriteNumber("quantity", item.Quantity);
                writer.WriteString("unitPrice", item.UnitPrice.ToString("G29", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return SHA256.HashData(buffer.WrittenSpan);
    }

    private static bool HasAtMostFourFractionalDigits(decimal value) => decimal.Round(value, 4) == value;

    private static Guid ParseUuid(string? value, string key, Dictionary<string, List<string>> errors)
    {
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
        {
            AddError(errors, key, "A non-empty UUID is required.");
            return Guid.Empty;
        }

        return id;
    }

    private static void AddError(Dictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var messages))
        {
            messages = [];
            errors.Add(key, messages);
        }

        messages.Add(message);
    }

    private static OrderRequestValidationResult Invalid(Dictionary<string, List<string>> errors) =>
        new(null, errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal));
}
