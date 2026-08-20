namespace AzureBusService.Worker;

internal static partial class WorkerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Completed message {MessageId}")]
    public static partial void MessageCompleted(ILogger logger, string messageId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Dead-lettered message {MessageId} with reason {Reason}")]
    public static partial void MessageDeadLettered(ILogger logger, string messageId, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Message {MessageId} failed on delivery {DeliveryCount} with error type {ErrorType}")]
    public static partial void MessageProcessingFailed(ILogger logger, string messageId, int deliveryCount, string errorType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Service Bus processor error from {ErrorSource} with error type {ErrorType}")]
    public static partial void ServiceBusError(ILogger logger, string errorSource, string errorType);
}
