using System.Net;

namespace AzureBusService.Api.Configuration;

public sealed class TrustedProxyOptions
{
    public const string SectionName = "TrustedProxies";

    public string[] KnownIPs { get; init; } = [];

    public static bool IsValid(TrustedProxyOptions options) =>
        options.KnownIPs.All(address => IPAddress.TryParse(address, out _));
}
