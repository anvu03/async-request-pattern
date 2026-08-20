using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AzureBusService.Worker;

public static class WorkerTelemetry
{
    public const string ActivitySourceName = "AzureBusService.Worker";
    public const string MeterName = "AzureBusService.Worker";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> Completed = Meter.CreateCounter<long>("orders.messages.completed");
    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("orders.messages.dead_lettered");
    public static readonly Counter<long> Abandoned = Meter.CreateCounter<long>("orders.messages.abandoned");
    public static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("orders.messages.duplicates");
    public static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>(
        "orders.messages.processing.duration",
        "ms");
}
