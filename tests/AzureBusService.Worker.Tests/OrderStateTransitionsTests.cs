using AzureBusService.Persistence;
using AzureBusService.Worker;
using Xunit;

namespace AzureBusService.Worker.Tests;

public sealed class OrderStateTransitionsTests
{
    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Queued)]
    [InlineData(OrderStatus.Processing)]
    public void ActiveStatusesTransitionDeterministicallyToCompleted(OrderStatus initialStatus)
    {
        var processing = OrderStateTransitions.BeginProcessing(initialStatus);
        var completed = OrderStateTransitions.Complete(processing);

        Assert.Equal(OrderStatus.Processing, processing);
        Assert.Equal(OrderStatus.Completed, completed);
    }

    [Theory]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Failed)]
    public void TerminalStatusesCannotRestartOrFail(OrderStatus status)
    {
        Assert.False(OrderStateTransitions.CanProcess(status));
        Assert.False(OrderStateTransitions.CanFail(status));
        Assert.Throws<InvalidOperationException>(() => OrderStateTransitions.BeginProcessing(status));
    }
}
