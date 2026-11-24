namespace Spawn.Core.Update;

/// <summary>
/// Immutable configuration for <see cref="UpdateService"/> with production defaults locked to <c>ADR-0007 §3.3</c>.
/// Unit tests may override channel, version, public key, and timers without performing real network or installer actions.
/// </summary>
public sealed record UpdateServiceOptions
{
    public const string DefaultManifestUrl =
        "https://github.com/chialecode/spawn/releases/latest/download/update-windows-x64.json";

    public const string DefaultSignatureUrl =
        "https://github.com/chialecode/spawn/releases/latest/download/update-windows-x64.json.sig";

    public const string DefaultInstallerUrlPrefixTemplate =
        "https://github.com/chialecode/spawn/releases/download/v{0}/";

    /// <summary>NSIS installer (installer/spawn.nsi): <c>/S</c> silent, <c>/RELAUNCH</c> starts SPAWN with
    /// <c>--after-update</c> once the update is in place.</summary>
    public const string DefaultSilentInstallerArguments = "/S /RELAUNCH";

    public const long DefaultMaxManifestBytes = 64L * 1024;
    public const long DefaultMaxSignatureBytes = 4L * 1024;
    public const long DefaultMaxInstallerBytes = 300L * 1024 * 1024;
    public const int DefaultMaxRedirects = 5;
    public const int DefaultMaxHistoryEntries = 20;

    public static readonly IReadOnlySet<string> DefaultAllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    public string? ChannelOverride { get; init; }
    public string? CurrentVersionOverride { get; init; }
    public string? CommitShaOverride { get; init; }

    public string PublicKeyPem { get; init; } = UpdateSigningPublicKey.Pem;
    public Uri ManifestUri { get; init; } = new(DefaultManifestUrl, UriKind.Absolute);
    public Uri SignatureUri { get; init; } = new(DefaultSignatureUrl, UriKind.Absolute);
    public string InstallerUrlPrefixTemplate { get; init; } = DefaultInstallerUrlPrefixTemplate;
    public string SilentInstallerArguments { get; init; } = DefaultSilentInstallerArguments;

    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan IdleRetryInterval { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan HttpRequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxRedirects { get; init; } = DefaultMaxRedirects;
    public long MaxManifestBytes { get; init; } = DefaultMaxManifestBytes;
    public long MaxSignatureBytes { get; init; } = DefaultMaxSignatureBytes;
    public long MaxInstallerBytes { get; init; } = DefaultMaxInstallerBytes;
    public int MaxHistoryEntries { get; init; } = DefaultMaxHistoryEntries;

    public IReadOnlySet<string> AllowedHosts { get; init; } = DefaultAllowedHosts;
}
