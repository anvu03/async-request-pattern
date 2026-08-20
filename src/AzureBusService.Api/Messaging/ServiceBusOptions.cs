namespace AzureBusService.Api.Messaging;

public enum ServiceBusMode
{
    Unspecified,
    Emulator,
    Azure
}

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    public ServiceBusMode Mode { get; init; }

    public string QueueName { get; init; } = "orders";

    public string? ConnectionString { get; init; }

    public string? FullyQualifiedNamespace { get; init; }

    public static bool IsValid(ServiceBusOptions options) =>
        !string.IsNullOrWhiteSpace(options.QueueName) && options.Mode switch
        {
            ServiceBusMode.Emulator => !string.IsNullOrWhiteSpace(options.ConnectionString),
            ServiceBusMode.Azure => !string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace),
            _ => false
        };
}
