using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

namespace Spawn.Modules.Progress.Snapshots;

/// <summary>
/// Compares strings by exact UTF-8 byte sequence (locale-independent, deterministic across machines).
/// </summary>
public sealed class Utf8LexicographicalComparer : IComparer<string>
{
    public static Utf8LexicographicalComparer Instance { get; } = new();

    private Utf8LexicographicalComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        byte[] xBytes = Encoding.UTF8.GetBytes(x);
        byte[] yBytes = Encoding.UTF8.GetBytes(y);
        return xBytes.AsSpan().SequenceCompareTo(yBytes.AsSpan());
    }
}

/// <summary>
/// Root deterministic JSON payload persisted as <c>registry_snapshot.json</c> for pre-trainer snapshots
/// and compatible with M4 save time-machine import.
/// </summary>
public sealed record DeterministicRegistrySnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<DeterministicRegistryRootKey> Keys { get; init; } = [];
}

/// <summary>
/// Snapshot of a single <c>HKCU</c> root key tree.
/// </summary>
public sealed record DeterministicRegistryRootKey
{
    public required string KeyPath { get; init; }
    public IReadOnlyList<DeterministicRegistryValueEntry> Values { get; init; } = [];
    public IReadOnlyList<DeterministicRegistrySubKeyNode> SubKeys { get; init; } = [];
}

/// <summary>
/// Recursive child key node sorted by <see cref="Name"/> in UTF-8 lexicographical order.
/// </summary>
public sealed record DeterministicRegistrySubKeyNode
{
    public required string Name { get; init; }
    public IReadOnlyList<DeterministicRegistryValueEntry> Values { get; init; } = [];
    public IReadOnlyList<DeterministicRegistrySubKeyNode> SubKeys { get; init; } = [];
}

/// <summary>
/// Single registry value with explicit Windows <c>REG_*</c> kind tag and deterministic JSON data representation.
/// </summary>
public sealed record DeterministicRegistryValueEntry
{
    public required string Name { get; init; }
    public required string ValueKind { get; init; }
    public required JsonElement Data { get; init; }
}

/// <summary>
/// Pure managed <c>HKCU</c>-only deterministic Windows Registry serializer/restorer using
/// <see cref="Microsoft.Win32.Registry"/> (never invokes <c>reg.exe</c>; strictly rejects <c>HKLM</c> and other hives).
/// </summary>
public static class DeterministicHkcuRegistrySerializer
{
    private const int MaxRecursionDepth = 32;

    public const string KindSz = "REG_SZ";
    public const string KindExpandSz = "REG_EXPAND_SZ";
    public const string KindMultiSz = "REG_MULTI_SZ";
    public const string KindDWord = "REG_DWORD";
    public const string KindQWord = "REG_QWORD";
    public const string KindBinary = "REG_BINARY";
    public const string KindNone = "REG_NONE";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Validates and canonicalizes a candidate <c>HKCU</c> registry key path.
    /// Strictly rejects <c>HKLM</c>, <c>HKCR</c>, <c>HKU</c>, <c>HKCC</c>, bare <c>HKCU</c>, and relative traversal segments.
    /// </summary>
    public static bool TryNormalizeHkcuSubKey(
        string? rawKeyPath,
        out string canonicalKeyPath,
        out string subKeyUnderCurrentUser,
        out string? error)
    {
        canonicalKeyPath = string.Empty;
        subKeyUnderCurrentUser = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(rawKeyPath))
        {
            error = "注册表键路径不能为空。";
            return false;
        }

        string trimmed = rawKeyPath.Trim();
        if (trimmed.Contains('\0', StringComparison.Ordinal))
        {
            error = "注册表键路径包含非法空字符。";
            return false;
        }

        string[] rawSegments = trimmed.Split(['\\', '/'], StringSplitOptions.None);
        if (rawSegments.Any(static s => s is ".." or "."))
        {
            error = $"注册表键路径包含非法相对跳转段：{rawKeyPath}";
            return false;
        }

        string normalized = trimmed.Replace('/', '\\').TrimEnd('\\');
        if (normalized.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = @"HKCU\" + normalized[@"HKEY_CURRENT_USER\".Length..];
        }

        if (!normalized.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
        {
            error = $"仅允许导出/恢复当前用户 HKCU 注册表键，已拒绝：{rawKeyPath}";
            return false;
        }

        string subKey = normalized[@"HKCU\".Length..].Trim('\\');
        if (string.IsNullOrWhiteSpace(subKey))
        {
            error = "禁止直接导出或恢复 HKCU 根键。";
            return false;
        }

        canonicalKeyPath = @"HKCU\" + subKey;
        subKeyUnderCurrentUser = subKey;
        return true;
    }

    /// <summary>
    /// Exports one or more <c>HKCU</c> registry key trees into a deterministic, UTF-8 lexicographically sorted snapshot.
    /// Throws <see cref="ArgumentException"/> if any input path targets a non-<c>HKCU</c> hive (such as <c>HKLM</c>).
    /// Missing <c>HKCU</c> keys are omitted gracefully.
    /// </summary>
    public static DeterministicRegistrySnapshot ExportKeys(IEnumerable<string> hkcuKeyPaths)
    {
        ArgumentNullException.ThrowIfNull(hkcuKeyPaths);

        var normalizedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawPath in hkcuKeyPaths)
        {
            if (!TryNormalizeHkcuSubKey(rawPath, out string canonical, out string subKey, out string? error))
            {
                throw new ArgumentException(error ?? $"非法的 HKCU 注册表键：{rawPath}", nameof(hkcuKeyPaths));
            }

            normalizedMap.TryAdd(canonical, subKey);
        }

        var orderedCanonicalPaths = normalizedMap.Keys
            .OrderBy(k => k, Utf8LexicographicalComparer.Instance)
            .ToList();

        var exportedRoots = new List<DeterministicRegistryRootKey>(orderedCanonicalPaths.Count);
        foreach (string canonicalPath in orderedCanonicalPaths)
        {
            string subKey = normalizedMap[canonicalPath];
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                continue;
            }

            var (values, subKeys) = ExportKeyContents(key, depth: 0);
            exportedRoots.Add(new DeterministicRegistryRootKey
            {
                KeyPath = canonicalPath,
                Values = values,
                SubKeys = subKeys,
            });
        }

        return new DeterministicRegistrySnapshot
        {
            SchemaVersion = 1,
            Keys = exportedRoots,
        };
    }

    public static byte[] SerializeToUtf8Bytes(DeterministicRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
    }

    public static string SerializeToJson(DeterministicRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Encoding.UTF8.GetString(SerializeToUtf8Bytes(snapshot));
    }

    public static string ComputeSha256Hex(DeterministicRegistrySnapshot snapshot)
    {
        byte[] bytes = SerializeToUtf8Bytes(snapshot);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static DeterministicRegistrySnapshot Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<DeterministicRegistrySnapshot>(utf8Json, JsonOptions)
        ?? throw new JsonException("注册表快照 JSON 内容为空或格式无效。");

    public static DeterministicRegistrySnapshot Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<DeterministicRegistrySnapshot>(json, JsonOptions)
               ?? throw new JsonException("注册表快照 JSON 内容为空或格式无效。");
    }

    /// <summary>
    /// Restores every <c>HKCU</c> key tree in <paramref name="snapshot"/>.
    /// Validates all keys are strictly <c>HKCU</c> before mutating the registry; each validated target subkey is replaced
    /// cleanly so re-exporting yields an identical deterministic hash.
    /// </summary>
    public static int Restore(DeterministicRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var validated = new List<(string SubKey, DeterministicRegistryRootKey Node)>(snapshot.Keys.Count);
        foreach (var rootNode in snapshot.Keys)
        {
            if (!TryNormalizeHkcuSubKey(rootNode.KeyPath, out _, out string subKey, out string? error))
            {
                throw new InvalidOperationException(error ?? $"拒绝恢复非 HKCU 注册表键：{rootNode.KeyPath}");
            }

            validated.Add((subKey, rootNode));
        }

        int restoredCount = 0;
        foreach (var (subKey, rootNode) in validated)
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            using RegistryKey? created = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
            if (created is null)
            {
                throw new IOException($"无法创建或打开目标 HKCU 注册表键：HKCU\\{subKey}");
            }

            ImportKeyContents(created, rootNode.Values, rootNode.SubKeys, depth: 0);
            restoredCount++;
        }

        return restoredCount;
    }

    private static (List<DeterministicRegistryValueEntry> Values, List<DeterministicRegistrySubKeyNode> SubKeys) ExportKeyContents(
        RegistryKey key,
        int depth)
    {
        var values = new List<DeterministicRegistryValueEntry>();
        string[] valueNames = key.GetValueNames();
        Array.Sort(valueNames, Utf8LexicographicalComparer.Instance);

        foreach (string valName in valueNames)
        {
            RegistryValueKind kind = key.GetValueKind(valName);
            object? rawValue = key.GetValue(valName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (rawValue is null)
            {
                continue;
            }

            var (kindTag, dataElement) = EncodeRegistryValue(kind, rawValue);
            values.Add(new DeterministicRegistryValueEntry
            {
                Name = valName,
                ValueKind = kindTag,
                Data = dataElement,
            });
        }

        var subKeys = new List<DeterministicRegistrySubKeyNode>();
        if (depth < MaxRecursionDepth)
        {
            string[] childNames = key.GetSubKeyNames();
            Array.Sort(childNames, Utf8LexicographicalComparer.Instance);

            foreach (string childName in childNames)
            {
                using RegistryKey? childKey = key.OpenSubKey(childName, writable: false);
                if (childKey is null)
                {
                    continue;
                }

                var (childValues, grandChildren) = ExportKeyContents(childKey, depth + 1);
                subKeys.Add(new DeterministicRegistrySubKeyNode
                {
                    Name = childName,
                    Values = childValues,
                    SubKeys = grandChildren,
                });
            }
        }

        return (values, subKeys);
    }

    private static (string KindTag, JsonElement DataElement) EncodeRegistryValue(RegistryValueKind kind, object rawValue)
    {
        switch (kind)
        {
            case RegistryValueKind.String:
                return (KindSz, JsonSerializer.SerializeToElement(rawValue as string ?? rawValue.ToString() ?? string.Empty, JsonOptions));

            case RegistryValueKind.ExpandString:
                return (KindExpandSz, JsonSerializer.SerializeToElement(rawValue as string ?? rawValue.ToString() ?? string.Empty, JsonOptions));

            case RegistryValueKind.MultiString:
                string[] multi = rawValue as string[] ?? [];
                return (KindMultiSz, JsonSerializer.SerializeToElement(multi, JsonOptions));

            case RegistryValueKind.DWord:
                uint dwordVal = unchecked((uint)Convert.ToInt32(rawValue, CultureInfo.InvariantCulture));
                return (KindDWord, JsonSerializer.SerializeToElement(dwordVal, JsonOptions));

            case RegistryValueKind.QWord:
                long qwordVal = Convert.ToInt64(rawValue, CultureInfo.InvariantCulture);
                return (KindQWord, JsonSerializer.SerializeToElement(qwordVal, JsonOptions));

            case RegistryValueKind.Binary:
                byte[] binBytes = rawValue as byte[] ?? [];
                return (KindBinary, JsonSerializer.SerializeToElement(Convert.ToBase64String(binBytes), JsonOptions));

            default:
                byte[] fallbackBytes = rawValue as byte[] ?? Encoding.UTF8.GetBytes(rawValue.ToString() ?? string.Empty);
                return (KindNone, JsonSerializer.SerializeToElement(Convert.ToBase64String(fallbackBytes), JsonOptions));
        }
    }

    private static void ImportKeyContents(
        RegistryKey targetKey,
        IReadOnlyList<DeterministicRegistryValueEntry> values,
        IReadOnlyList<DeterministicRegistrySubKeyNode> subKeys,
        int depth)
    {
        foreach (var entry in values)
        {
            var (kind, valueObj) = DecodeRegistryValue(entry);
            targetKey.SetValue(entry.Name, valueObj, kind);
        }

        if (depth >= MaxRecursionDepth)
        {
            return;
        }

        foreach (var childNode in subKeys)
        {
            if (string.IsNullOrWhiteSpace(childNode.Name)
                || childNode.Name.Contains('\\', StringComparison.Ordinal)
                || childNode.Name.Contains('/', StringComparison.Ordinal)
                || childNode.Name is "." or "..")
            {
                throw new InvalidOperationException($"注册表快照包含非法的子键名：{childNode.Name}");
            }

            using RegistryKey? childKey = targetKey.CreateSubKey(childNode.Name, writable: true);
            if (childKey is null)
            {
                throw new IOException($"无法创建子注册表键：{childNode.Name}");
            }

            ImportKeyContents(childKey, childNode.Values, childNode.SubKeys, depth + 1);
        }
    }

    private static (RegistryValueKind Kind, object Value) DecodeRegistryValue(DeterministicRegistryValueEntry entry)
    {
        string kindTag = entry.ValueKind?.Trim().ToUpperInvariant() ?? KindSz;
        switch (kindTag)
        {
            case KindSz:
                return (RegistryValueKind.String, entry.Data.ValueKind == JsonValueKind.String ? entry.Data.GetString() ?? string.Empty : entry.Data.ToString());

            case KindExpandSz:
                return (RegistryValueKind.ExpandString, entry.Data.ValueKind == JsonValueKind.String ? entry.Data.GetString() ?? string.Empty : entry.Data.ToString());

            case KindMultiSz:
            {
                if (entry.Data.ValueKind == JsonValueKind.Array)
                {
                    var items = new List<string>();
                    foreach (var elem in entry.Data.EnumerateArray())
                    {
                        items.Add(elem.ValueKind == JsonValueKind.String ? elem.GetString() ?? string.Empty : elem.ToString());
                    }

                    return (RegistryValueKind.MultiString, items.ToArray());
                }

                return (RegistryValueKind.MultiString, Array.Empty<string>());
            }

            case KindDWord:
            {
                int dword;
                if (entry.Data.ValueKind == JsonValueKind.Number)
                {
                    if (entry.Data.TryGetUInt32(out uint uVal))
                    {
                        dword = unchecked((int)uVal);
                    }
                    else if (entry.Data.TryGetInt32(out int iVal))
                    {
                        dword = iVal;
                    }
                    else
                    {
                        dword = unchecked((int)entry.Data.GetInt64());
                    }
                }
                else if (entry.Data.ValueKind == JsonValueKind.String
                         && long.TryParse(entry.Data.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                {
                    dword = unchecked((int)parsed);
                }
                else
                {
                    dword = 0;
                }

                return (RegistryValueKind.DWord, dword);
            }

            case KindQWord:
            {
                long qword;
                if (entry.Data.ValueKind == JsonValueKind.Number)
                {
                    qword = entry.Data.TryGetInt64(out long lVal)
                        ? lVal
                        : unchecked((long)entry.Data.GetUInt64());
                }
                else if (entry.Data.ValueKind == JsonValueKind.String
                         && long.TryParse(entry.Data.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                {
                    qword = parsed;
                }
                else
                {
                    qword = 0L;
                }

                return (RegistryValueKind.QWord, qword);
            }

            case KindBinary:
            {
                string b64 = entry.Data.ValueKind == JsonValueKind.String ? entry.Data.GetString() ?? string.Empty : string.Empty;
                return (RegistryValueKind.Binary, string.IsNullOrEmpty(b64) ? Array.Empty<byte>() : Convert.FromBase64String(b64));
            }

            case KindNone:
            {
                string b64 = entry.Data.ValueKind == JsonValueKind.String ? entry.Data.GetString() ?? string.Empty : string.Empty;
                return (RegistryValueKind.None, string.IsNullOrEmpty(b64) ? Array.Empty<byte>() : Convert.FromBase64String(b64));
            }

            default:
                throw new InvalidDataException($"不支持的注册表值类型：{entry.ValueKind}");
        }
    }
}
