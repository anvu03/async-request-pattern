using AzureBusService.Persistence;

namespace AzureBusService.Worker;

public static class OrderStateTransitions
{
    public static bool CanProcess(OrderStatus status) =>
        status is OrderStatus.Pending or OrderStatus.Queued or OrderStatus.Processing;

    public static OrderStatus BeginProcessing(OrderStatus status) => CanProcess(status)
        ? OrderStatus.Processing
        : throw new InvalidOperationException($"Cannot process an order in {status} state.");

    public static OrderStatus Complete(OrderStatus status) => status == OrderStatus.Processing
        ? OrderStatus.Completed
        : throw new InvalidOperationException($"Cannot complete an order in {status} state.");

    public static bool CanFail(OrderStatus status) => CanProcess(status);
}
