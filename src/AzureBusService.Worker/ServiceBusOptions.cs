namespace AzureBusService.Worker;

public enum ServiceBusMode
{
    Unspecified,
    Emulator,
    Azure,
}

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    public ServiceBusMode Mode { get; init; }

    public string? ConnectionString { get; init; }

    public string? FullyQualifiedNamespace { get; init; }

    public string QueueName { get; init; } = "orders";

    public int MaxConcurrentCalls { get; init; } = 16;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(QueueName))
        {
            throw new InvalidOperationException("ServiceBus:QueueName is required.");
        }

        if (MaxConcurrentCalls is <= 0 or > 256)
        {
            throw new InvalidOperationException("ServiceBus:MaxConcurrentCalls must be between 1 and 256.");
        }

        switch (Mode)
        {
            case ServiceBusMode.Emulator when string.IsNullOrWhiteSpace(ConnectionString):
                throw new InvalidOperationException("ServiceBus:ConnectionString is required in Emulator mode.");
            case ServiceBusMode.Azure when string.IsNullOrWhiteSpace(FullyQualifiedNamespace):
                throw new InvalidOperationException("ServiceBus:FullyQualifiedNamespace is required in Azure mode.");
            case ServiceBusMode.Unspecified:
                throw new InvalidOperationException("ServiceBus:Mode must be Emulator or Azure.");
        }
    }
}
