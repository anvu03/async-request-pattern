namespace AzureBusService.Persistence;

public enum OrderStatus
{
    Pending = 0,
    Queued = 1,
    Processing = 2,
    Completed = 3,
    Failed = 4
}
