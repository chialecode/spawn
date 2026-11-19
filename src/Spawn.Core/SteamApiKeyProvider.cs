using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Encrypts and decrypts user secrets with Windows CurrentUser DPAPI bound to the active SPAWN channel.
/// </summary>
public sealed class DpapiSecretStore(SpawnPaths paths)
{
    public void SaveSecret(string credentialId, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        Directory.CreateDirectory(paths.SecretsDirectory);
        string targetPath = GetSecretFilePath(credentialId);
        string tempPath = targetPath + ".tmp";

        byte[] rawBytes = Encoding.UTF8.GetBytes(plaintext.Trim());
        byte[] entropy = GetEntropy(credentialId);
        try
        {
            byte[] cipherBytes = ProtectedData.Protect(rawBytes, entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(tempPath, cipherBytes);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawBytes);
        }
    }

    public string? TryReadSecret(string credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        string targetPath = GetSecretFilePath(credentialId);
        if (!File.Exists(targetPath))
        {
            return null;
        }

        try
        {
            byte[] cipherBytes = File.ReadAllBytes(targetPath);
            byte[] entropy = GetEntropy(credentialId);
            byte[] rawBytes = ProtectedData.Unprotect(cipherBytes, entropy, DataProtectionScope.CurrentUser);
            try
            {
                string value = Encoding.UTF8.GetString(rawBytes).Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rawBytes);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public bool DeleteSecret(string credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        string targetPath = GetSecretFilePath(credentialId);
        if (!File.Exists(targetPath))
        {
            return false;
        }

        File.Delete(targetPath);
        return true;
    }

    private string GetSecretFilePath(string credentialId)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safeId = string.Concat(credentialId.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        return Path.Combine(paths.SecretsDirectory, $"{safeId}.sec");
    }

    private byte[] GetEntropy(string credentialId) =>
        Encoding.UTF8.GetBytes($"spawn-dpapi-v1:{paths.ChannelName}:{credentialId}");
}

/// <summary>
/// Resolves the Steam Web API key from user DPAPI settings first, falling back in development mode
/// to a read-only <c>.local/secrets/steam-web-api.json</c> file without ever logging or copying the raw key.
/// </summary>
public sealed class SteamApiKeyProvider : ISteamApiKeyProvider
{
    private const string LocalSecretRelativePath = ".local/secrets/steam-web-api.json";

    private readonly SpawnPaths _paths;
    private readonly DpapiSecretStore _secretStore;
    private readonly bool _allowLocalDevFallback;

    public SteamApiKeyProvider(SpawnPaths paths, bool? allowLocalDevFallback = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _secretStore = new DpapiSecretStore(paths);
        _allowLocalDevFallback = allowLocalDevFallback ?? paths.IsDevelopment;
    }

    public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? dpapiKey = _secretStore.TryReadSecret(AppSettingKeys.SteamWebApiCredentialId);
        if (!string.IsNullOrWhiteSpace(dpapiKey))
        {
            return ValueTask.FromResult<string?>(dpapiKey);
        }

        if (_allowLocalDevFallback)
        {
            string? localKey = TryReadLocalDevSteamApiKey();
            if (!string.IsNullOrWhiteSpace(localKey))
            {
                return ValueTask.FromResult<string?>(localKey);
            }
        }

        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<SteamApiKeyStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? dpapiKey = _secretStore.TryReadSecret(AppSettingKeys.SteamWebApiCredentialId);
        if (!string.IsNullOrWhiteSpace(dpapiKey))
        {
            string masked = MaskSecret(dpapiKey);
            return ValueTask.FromResult(new SteamApiKeyStatusDto(
                IsConfigured: true,
                SourceLabel: "本机 DPAPI 加密存储",
                MaskedPreview: $"{masked} (本机 DPAPI)"));
        }

        if (_allowLocalDevFallback)
        {
            string? localKey = TryReadLocalDevSteamApiKey();
            if (!string.IsNullOrWhiteSpace(localKey))
            {
                string masked = MaskSecret(localKey);
                return ValueTask.FromResult(new SteamApiKeyStatusDto(
                    IsConfigured: true,
                    SourceLabel: "来自 .local",
                    MaskedPreview: $"{masked} (来自 .local)"));
            }
        }

        return ValueTask.FromResult(new SteamApiKeyStatusDto(
            IsConfigured: false,
            SourceLabel: "未配置（将使用本地缓存与公开镜像兜底）",
            MaskedPreview: null));
    }

    public ValueTask SaveCustomApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _secretStore.DeleteSecret(AppSettingKeys.SteamWebApiCredentialId);
        }
        else
        {
            _secretStore.SaveSecret(AppSettingKeys.SteamWebApiCredentialId, apiKey.Trim());
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Masks a credential string as <c>ABCD****************************WXYZ</c> so raw keys never leak into UI or diagnostics.
    /// </summary>
    public static string MaskSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return string.Empty;
        }

        string trimmed = secret.Trim();
        if (trimmed.Length <= 8)
        {
            return new string('*', Math.Max(4, trimmed.Length));
        }

        int starCount = Math.Max(8, trimmed.Length - 8);
        return string.Concat(trimmed.AsSpan(0, 4), new string('*', starCount), trimmed.AsSpan(trimmed.Length - 4, 4));
    }

    private string? TryReadLocalDevSteamApiKey()
    {
        string? filePath = null;
        if (!string.IsNullOrWhiteSpace(_paths.LocalSecretsDirectory))
        {
            string directCandidate = Path.Combine(_paths.LocalSecretsDirectory, "steam-web-api.json");
            if (File.Exists(directCandidate))
            {
                filePath = directCandidate;
            }
        }

        filePath ??= SpawnPaths.DiscoverLocalFileUpwards(
            LocalSecretRelativePath.Replace('/', Path.DirectorySeparatorChar),
            _paths.LocalWorkspaceRoot);

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
                    || prop.Name.Equals("api_key", StringComparison.OrdinalIgnoreCase)
                    || prop.Name.Equals("key", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string? key = prop.Value.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            return key;
                        }
                    }
                }
            }
        }
        catch
        {
            // Never log file content or propagate parse exceptions for optional dev secrets.
        }

        return null;
    }
}
