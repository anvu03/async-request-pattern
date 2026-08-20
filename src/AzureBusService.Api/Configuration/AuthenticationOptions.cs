namespace AzureBusService.Api.Configuration;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool Enabled { get; init; }

    public bool AllowAnonymousLocal { get; init; }

    public string? Authority { get; init; }

    public string? Audience { get; init; }

    public static bool IsValid(AuthenticationOptions options) => options.Enabled
        ? !string.IsNullOrWhiteSpace(options.Authority) && !string.IsNullOrWhiteSpace(options.Audience)
        : options.AllowAnonymousLocal;
}
