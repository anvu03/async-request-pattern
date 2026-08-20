using System.Security.Claims;
using AzureBusService.Api.Orders;
using AzureBusService.Persistence;
using Microsoft.Extensions.Primitives;

namespace AzureBusService.Api.Tests;

public sealed class OrderStatusAndCacheTests
{
    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Queued, true)]
    [InlineData(OrderStatus.Pending, OrderStatus.Completed, false)]
    [InlineData(OrderStatus.Queued, OrderStatus.Processing, true)]
    [InlineData(OrderStatus.Processing, OrderStatus.Completed, true)]
    [InlineData(OrderStatus.Completed, OrderStatus.Failed, false)]
    [InlineData(OrderStatus.Failed, OrderStatus.Failed, false)]
    public void StatusTransitionsAreGuarded(OrderStatus current, OrderStatus next, bool expected)
    {
        Assert.Equal(expected, OrderStatusTransitions.CanTransition(current, next));
    }

    [Fact]
    public void IfNoneMatchAcceptsWeakAndListValues()
    {
        var etag = OrderHttpCache.CreateEtag([1, 2, 3]);

        Assert.True(OrderHttpCache.Matches(new StringValues($"\"other\", W/{etag}"), etag));
        Assert.False(OrderHttpCache.Matches(new StringValues("\"other\""), etag));
    }

    [Theory]
    [InlineData(" key ", "key")]
    [InlineData("\tkey\r\n", "key")]
    public void IdempotencyKeysAreTrimmed(string input, string expected)
    {
        Assert.True(IdempotencyKey.TryNormalize(input, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Empty(error);
    }

    [Fact]
    public void BlankIdempotencyKeysAreRejectedAfterTrimming()
    {
        Assert.False(IdempotencyKey.TryNormalize("   ", out var normalized, out _));
        Assert.Null(normalized);
    }

    [Fact]
    public void FailedOrderUsesStoredSanitizedFailure()
    {
        var stored = OrderFailureResponse.FromStored("inventory_unavailable", "Inventory is unavailable.");
        var fallback = OrderFailureResponse.FromStored(null, null);

        Assert.Equal("inventory_unavailable", stored.Code);
        Assert.Equal("Inventory is unavailable.", stored.Message);
        Assert.Equal("order_failed", fallback.Code);
        Assert.Equal("Order processing failed.", fallback.Message);
    }

    [Fact]
    public void AuthenticatedOwnerIncludesIssuerAndPreservesExactIdentity()
    {
        var first = Principal("https://issuer-a", "Subject");
        var second = Principal("https://issuer-b", "Subject");
        var caseVariant = Principal("https://issuer-a", "subject");

        Assert.True(OrderOwner.TryGetAuthenticated(first, out var firstOwner));
        Assert.True(OrderOwner.TryGetAuthenticated(second, out var secondOwner));
        Assert.True(OrderOwner.TryGetAuthenticated(caseVariant, out var caseOwner));
        Assert.NotEqual(firstOwner, secondOwner);
        Assert.NotEqual(firstOwner, caseOwner);
    }

    private static ClaimsPrincipal Principal(string issuer, string subject) => new(
        new ClaimsIdentity(
            [new Claim("iss", issuer), new Claim("sub", subject)],
            authenticationType: "test"));
}
