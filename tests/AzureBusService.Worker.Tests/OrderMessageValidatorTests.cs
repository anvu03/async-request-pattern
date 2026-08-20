using System.Text.Json;
using AzureBusService.Contracts;
using AzureBusService.Persistence;
using AzureBusService.Worker;
using Xunit;

namespace AzureBusService.Worker.Tests;

public sealed class OrderMessageValidatorTests
{
    private readonly OrderMessageValidator _validator = new();

    [Fact]
    public void ValidateAcceptsSupportedContract()
    {
        var orderId = Guid.NewGuid();
        var message = CreateMessage(orderId);

        var result = _validator.Validate(Decode(message));

        Assert.True(result.IsValid);
        Assert.Equal(orderId, result.Message!.OrderId);
        Assert.Null(result.DeadLetterReason);
    }

    [Fact]
    public void ValidateRejectsUnknownContractWithoutExposingInput()
    {
        var input = Decode(CreateMessage(Guid.NewGuid())) with { Subject = "SecretContract" };

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal("ContractMismatch", result.DeadLetterReason);
        Assert.DoesNotContain("SecretContract", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsDisagreeingContractMetadata()
    {
        var input = Decode(CreateMessage(Guid.NewGuid())) with { ContractName = "OtherContract" };

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal("ContractMismatch", result.DeadLetterReason);
    }

    [Fact]
    public void ValidateRejectsUnknownSchemaVersion()
    {
        var input = Decode(CreateMessage(Guid.NewGuid())) with { SchemaVersion = 2 };

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal("UnknownSchemaVersion", result.DeadLetterReason);
    }

    [Fact]
    public void ValidateRejectsMalformedBodyAndPreservesParsedOrderId()
    {
        var orderId = Guid.NewGuid();
        var input = new DecodedOrderMessage(
            Guid.NewGuid().ToString(),
            OrderCreatedV1.ContractName,
            OrderCreatedV1.ContractName,
            OrderCreatedV1.SchemaVersion,
            orderId.ToString(),
            Guid.NewGuid(),
            null,
            null,
            "not-json"u8.ToArray());

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal("MalformedMessage", result.DeadLetterReason);
        Assert.Equal(orderId, result.TrustedOrderId);
        Assert.DoesNotContain("not-json", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsEnvelopeMessageIdMismatch()
    {
        var input = Decode(CreateMessage(Guid.NewGuid())) with { MessageId = Guid.NewGuid().ToString() };

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal("MetadataMismatch", result.DeadLetterReason);
    }

    [Fact]
    public void ValidateRejectsMalformedEnvelopeMessageId()
    {
        var input = Decode(CreateMessage(Guid.NewGuid())) with { MessageId = "not-a-guid" };

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal("MetadataMismatch", result.DeadLetterReason);
    }

    [Fact]
    public void ValidateRequiresTrustedOrderId()
    {
        var input = Decode(CreateMessage(Guid.NewGuid())) with { OrderId = null };

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Null(result.TrustedOrderId);
        Assert.Equal("MetadataMismatch", result.DeadLetterReason);
    }

    [Fact]
    public void ValidateRejectsOrderIdMismatchAndPreservesTrustedOrderId()
    {
        var trustedOrderId = Guid.NewGuid();
        var input = Decode(CreateMessage(Guid.NewGuid())) with { OrderId = trustedOrderId };

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal(trustedOrderId, result.TrustedOrderId);
        Assert.Equal("MetadataMismatch", result.DeadLetterReason);
    }

    [Fact]
    public void ValidateRejectsCorrelationIdMismatch()
    {
        var input = Decode(CreateMessage(Guid.NewGuid())) with { CorrelationId = Guid.NewGuid() };

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal("MetadataMismatch", result.DeadLetterReason);
    }

    [Fact]
    public void ValidateRejectsMissingCorrelationMetadata()
    {
        var input = Decode(CreateMessage(Guid.NewGuid())) with { CorrelationId = null };

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal("MetadataMismatch", result.DeadLetterReason);
    }

    [Fact]
    public void ValidateRejectsInconsistentTotal()
    {
        var input = Decode(CreateMessage(Guid.NewGuid()) with { Total = 11m });

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal("MalformedMessage", result.DeadLetterReason);
    }

    [Fact]
    public void ValidateRejectsEmptyItems()
    {
        var input = Decode(CreateMessage(Guid.NewGuid()) with { Items = [] });

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal("MalformedMessage", result.DeadLetterReason);
    }

    [Fact]
    public void ProvenanceRequiresMatchingPersistedOutboxPayload()
    {
        var message = CreateMessage(Guid.NewGuid());
        var outbox = new OutboxMessage
        {
            Id = message.MessageId,
            OrderId = message.OrderId,
            ContractName = OrderCreatedV1.ContractName,
            SchemaVersion = OrderCreatedV1.SchemaVersion,
            PayloadJson = JsonSerializer.Serialize(message),
            CreatedUtc = message.CreatedUtc,
            NextAttemptUtc = message.CreatedUtc,
            Order = null!,
        };

        Assert.True(MessageProvenance.Matches(outbox, message));
        Assert.False(MessageProvenance.Matches(outbox, message with { Total = message.Total + 1 }));
        Assert.False(MessageProvenance.Matches(null, message));
    }

    private static DecodedOrderMessage Decode(OrderCreatedV1 message) => new(
        message.MessageId.ToString(),
        OrderCreatedV1.ContractName,
        OrderCreatedV1.ContractName,
        OrderCreatedV1.SchemaVersion,
        message.OrderId,
        message.CorrelationId,
        null,
        null,
        JsonSerializer.SerializeToUtf8Bytes(message));

    private static OrderCreatedV1 CreateMessage(Guid orderId) => new(
        Guid.NewGuid(),
        orderId,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        Guid.NewGuid(),
        "USD",
        10m,
        [new(Guid.NewGuid(), 1, 10m)]);
}
