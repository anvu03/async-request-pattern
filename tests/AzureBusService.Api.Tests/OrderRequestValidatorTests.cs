using AzureBusService.Api.Orders;

namespace AzureBusService.Api.Tests;

public sealed class OrderRequestValidatorTests
{
    [Fact]
    public void ValidateAcceptsValidRequest()
    {
        var request = Request((Guid.NewGuid().ToString(), 2, 12.3456m));

        var result = OrderRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(24.6912m, result.Value!.Total);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateRejectsInvalidFieldsAndDuplicateProducts()
    {
        var productId = Guid.NewGuid().ToString();
        var request = new CreateOrderRequest(
            "not-a-uuid",
            "usd",
            [
                new CreateOrderItemRequest(productId, 0, 0),
                new CreateOrderItemRequest(productId, 1001, 1.00001m)
            ]);

        var result = OrderRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains("customerId", result.Errors.Keys);
        Assert.Contains("currency", result.Errors.Keys);
        Assert.Contains("items[1].productId", result.Errors.Keys);
        Assert.Contains("items[0].quantity", result.Errors.Keys);
        Assert.Contains("items[1].unitPrice", result.Errors.Keys);
    }

    [Fact]
    public void ValidateRejectsTotalOutsideDecimal19Scale4()
    {
        var request = Request((Guid.NewGuid().ToString(), 2, 999_999_999_999_999.9999m));

        var result = OrderRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains("total", result.Errors.Keys);
    }

    [Fact]
    public void ValidateConvertsDecimalOverflowToErrors()
    {
        var request = Request((Guid.NewGuid().ToString(), 1000, decimal.MaxValue));

        var result = OrderRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains("total", result.Errors.Keys);
        Assert.Contains("items[0].unitPrice", result.Errors.Keys);
    }

    private static CreateOrderRequest Request(params (string ProductId, int Quantity, decimal UnitPrice)[] items) =>
        new(
            Guid.NewGuid().ToString(),
            "USD",
            items.Select(item => new CreateOrderItemRequest(item.ProductId, item.Quantity, item.UnitPrice)).ToArray());
}
