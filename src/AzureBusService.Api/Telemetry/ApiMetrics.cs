using System.Diagnostics.Metrics;

namespace AzureBusService.Api.Telemetry;

public static class ApiMetrics
{
    public const string MeterName = "AzureBusService.Api";

    private static readonly Meter Meter = new(MeterName);
    private static long outboxBacklog;

    public static readonly Counter<long> OrdersAccepted = Meter.CreateCounter<long>("orders.accepted");

    public static readonly Counter<long> OrderConflicts = Meter.CreateCounter<long>("orders.conflicts");

    public static readonly Counter<long> OutboxPublished = Meter.CreateCounter<long>("outbox.published");

    public static readonly Counter<long> OutboxPublishFailures = Meter.CreateCounter<long>("outbox.publish_failures");

    private static readonly ObservableGauge<long> OutboxBacklog =
        Meter.CreateObservableGauge("outbox.backlog", () => Interlocked.Read(ref outboxBacklog));

    public static void SetOutboxBacklog(long value) => Interlocked.Exchange(ref outboxBacklog, value);
}
