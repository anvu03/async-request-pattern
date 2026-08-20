using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using AzureBusService.Api.Messaging;
using AzureBusService.Contracts;

namespace AzureBusService.Api.Tests;

public sealed class OutboxPublisherTests
{
    [Fact]
    public void CreateMessageUsesExactWorkerEnvelope()
    {
        var contract = Contract();
        var payload = JsonSerializer.Serialize(contract);
        using var activity = new Activity("publish");
        activity.TraceStateString = "vendor=value";
        activity.Start();

        var message = OutboxPublisher.CreateMessage(
            payload,
            contract.MessageId,
            contract.OrderId,
            OrderCreatedV1.ContractName,
            OrderCreatedV1.SchemaVersion,
            activity);

        Assert.Equal(contract.MessageId.ToString("D"), message.MessageId);
        Assert.Equal(contract.CorrelationId.ToString("D"), message.CorrelationId);
        Assert.Equal(OrderCreatedV1.ContractName, message.ApplicationProperties["ContractName"]);
        Assert.Equal(OrderCreatedV1.SchemaVersion, message.ApplicationProperties["SchemaVersion"]);
        Assert.Equal(contract.OrderId.ToString("D"), message.ApplicationProperties["OrderId"]);
        Assert.Equal(contract.CorrelationId.ToString("D"), message.ApplicationProperties["CorrelationId"]);
        Assert.Equal(activity.Id, message.ApplicationProperties["traceparent"]);
        Assert.Equal(activity.TraceStateString, message.ApplicationProperties["tracestate"]);
        Assert.DoesNotContain("contractName", message.ApplicationProperties.Keys);
        Assert.DoesNotContain("schemaVersion", message.ApplicationProperties.Keys);
    }

    [Fact]
    public void CreateMessageRejectsBodyIdMismatch()
    {
        var contract = Contract();

        Assert.Throws<JsonException>(() => OutboxPublisher.CreateMessage(
            JsonSerializer.Serialize(contract),
            Guid.CreateVersion7(),
            contract.OrderId,
            OrderCreatedV1.ContractName,
            OrderCreatedV1.SchemaVersion));
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 3)]
    [InlineData(20, 1, 300)]
    public void RetryDelayUsesBoundedJitter(int attempt, double jitter, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, OutboxRetry.ComputeDelay(attempt, jitter).TotalSeconds);
    }

    private static OrderCreatedV1 Contract() => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        DateTimeOffset.UtcNow,
        Guid.CreateVersion7(),
        "USD",
        10,
        ImmutableArray.Create(new OrderCreatedItemV1(Guid.CreateVersion7(), 1, 10)));
}
