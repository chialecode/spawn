using System.Globalization;

namespace Spawn.Core.Update;

/// <summary>
/// Strict three-part numeric <c>Major.Minor.Patch</c> version comparator for signed release updates.
/// </summary>
public readonly record struct UpdateSemanticVersion(int Major, int Minor, int Patch) : IComparable<UpdateSemanticVersion>
{
    public int CompareTo(UpdateSemanticVersion other)
    {
        int cmp = Major.CompareTo(other.Major);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = Minor.CompareTo(other.Minor);
        return cmp != 0 ? cmp : Patch.CompareTo(other.Patch);
    }

    public static bool operator >(UpdateSemanticVersion left, UpdateSemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(UpdateSemanticVersion left, UpdateSemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(UpdateSemanticVersion left, UpdateSemanticVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(UpdateSemanticVersion left, UpdateSemanticVersion right) => left.CompareTo(right) <= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>
    /// Parses a strict <c>Major.Minor.Patch</c> release version string (no leading <c>v</c>, no pre-release suffix,
    /// no whitespace, and strictly non-negative ASCII digits in each segment).
    /// </summary>
    public static bool TryParseStrict(string? raw, out UpdateSemanticVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        string[] parts = raw.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParseSegment(parts[0], out int major)
            || !TryParseSegment(parts[1], out int minor)
            || !TryParseSegment(parts[2], out int patch))
        {
            return false;
        }

        version = new UpdateSemanticVersion(major, minor, patch);
        return true;
    }

    private static bool TryParseSegment(string segment, out int value)
    {
        value = 0;
        if (segment.Length == 0)
        {
            return false;
        }

        // Disallow leading zeros on multi-digit segments (e.g. "01") and non-ASCII digits.
        if (segment.Length > 1 && segment[0] == '0')
        {
            return false;
        }

        foreach (char ch in segment)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }
}
