using System.Diagnostics;
using Azure.Messaging.ServiceBus;

namespace AzureBusService.Worker;

public sealed class ServiceBusWorker(
    ServiceBusProcessor processor,
    OrderMessageProcessor messageProcessor,
    OrderMessageValidator validator,
    ServiceBusOptions options,
    ILogger<ServiceBusWorker> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;
        await processor.StartProcessingAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await processor.StopProcessingAsync(cancellationToken);
        processor.ProcessMessageAsync -= ProcessMessageAsync;
        processor.ProcessErrorAsync -= ProcessErrorAsync;
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var input = Decode(args.Message);
        var messageId = SanitizeMessageId(args.Message.MessageId);
        var started = Stopwatch.GetTimestamp();
        using var activity = StartActivity(input);
        activity?.SetTag("messaging.system", "servicebus");
        activity?.SetTag("messaging.destination.name", options.QueueName);

        try
        {
            var result = await messageProcessor.ProcessAsync(input, args.CancellationToken);
            if (result.Disposition == MessageDisposition.Complete)
            {
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                WorkerTelemetry.Completed.Add(1);
                if (result.IsDuplicate)
                {
                    WorkerTelemetry.Duplicates.Add(1);
                }

                WorkerLog.MessageCompleted(logger, messageId);
                return;
            }

            await args.DeadLetterMessageAsync(
                args.Message,
                result.DeadLetterReason!,
                result.Diagnostic!,
                args.CancellationToken);
            WorkerTelemetry.DeadLettered.Add(1);
            WorkerLog.MessageDeadLettered(logger, messageId, result.DeadLetterReason!);
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            WorkerLog.MessageProcessingFailed(
                logger,
                messageId,
                args.Message.DeliveryCount,
                exception.GetType().Name);

            if (args.Message.DeliveryCount >= 5)
            {
                var validation = validator.Validate(input);
                await messageProcessor.MarkFailedAsync(
                    validation.Message?.OrderId,
                    "MaxDeliveryCountExceeded",
                    "Message processing failed after five deliveries.",
                    args.CancellationToken);
                await args.DeadLetterMessageAsync(
                    args.Message,
                    "MaxDeliveryCountExceeded",
                    "Message processing failed after five deliveries.",
                    args.CancellationToken);
                WorkerTelemetry.DeadLettered.Add(1);
                WorkerLog.MessageDeadLettered(logger, messageId, "MaxDeliveryCountExceeded");
                return;
            }

            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
            WorkerTelemetry.Abandoned.Add(1);
        }
        finally
        {
            WorkerTelemetry.ProcessingDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        WorkerLog.ServiceBusError(
            logger,
            args.ErrorSource.ToString(),
            args.Exception.GetType().Name);
        return Task.CompletedTask;
    }

    private static DecodedOrderMessage Decode(ServiceBusReceivedMessage message)
    {
        message.ApplicationProperties.TryGetValue("ContractName", out var contractName);
        message.ApplicationProperties.TryGetValue("SchemaVersion", out var schemaVersion);
        message.ApplicationProperties.TryGetValue("OrderId", out var orderId);
        message.ApplicationProperties.TryGetValue("CorrelationId", out var correlationId);
        message.ApplicationProperties.TryGetValue("traceparent", out var traceParent);
        message.ApplicationProperties.TryGetValue("tracestate", out var traceState);
        return new(
            message.MessageId,
            message.Subject,
            contractName as string,
            schemaVersion,
            orderId,
            correlationId,
            traceParent as string,
            traceState as string,
            message.Body.ToMemory());
    }

    private static Activity? StartActivity(DecodedOrderMessage message) =>
        ActivityContext.TryParse(message.TraceParent, message.TraceState, true, out var parentContext)
            ? WorkerTelemetry.ActivitySource.StartActivity(
                "process order message",
                ActivityKind.Consumer,
                parentContext)
            : WorkerTelemetry.ActivitySource.StartActivity("process order message", ActivityKind.Consumer);

    private static string SanitizeMessageId(string messageId) =>
        Guid.TryParse(messageId, out var id) ? id.ToString() : "invalid";
}
