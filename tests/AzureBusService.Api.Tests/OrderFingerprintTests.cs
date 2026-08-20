using AzureBusService.Api.Orders;

namespace AzureBusService.Api.Tests;

public sealed class OrderFingerprintTests
{
    [Fact]
    public void FingerprintNormalizesUuidDecimalAndItemOrder()
    {
        var firstProduct = Guid.Parse("10000000-0000-0000-0000-000000000000");
        var secondProduct = Guid.Parse("20000000-0000-0000-0000-000000000000");
        var customer = Guid.NewGuid();
        var first = Validate(new CreateOrderRequest(
            customer.ToString("B"),
            "USD",
            [
                new CreateOrderItemRequest(secondProduct.ToString("B"), 2, 1.00m),
                new CreateOrderItemRequest(firstProduct.ToString("D"), 1, 2.0m)
            ]));
        var second = Validate(new CreateOrderRequest(
            customer.ToString("D"),
            "USD",
            [
                new CreateOrderItemRequest(firstProduct.ToString("D"), 1, 2m),
                new CreateOrderItemRequest(secondProduct.ToString("D"), 2, 1m)
            ]));

        Assert.Equal(
            OrderRequestValidator.ComputeFingerprint(first),
            OrderRequestValidator.ComputeFingerprint(second));
    }

    [Fact]
    public void FingerprintChangesWhenPayloadChanges()
    {
        var product = Guid.NewGuid().ToString();
        var customer = Guid.NewGuid().ToString();
        var first = Validate(new CreateOrderRequest(customer, "USD", [new(product, 1, 2m)]));
        var second = Validate(new CreateOrderRequest(customer, "USD", [new(product, 2, 2m)]));

        Assert.NotEqual(
            OrderRequestValidator.ComputeFingerprint(first),
            OrderRequestValidator.ComputeFingerprint(second));
    }

    private static ValidatedOrderRequest Validate(CreateOrderRequest request) =>
        OrderRequestValidator.Validate(request).Value!;
}
