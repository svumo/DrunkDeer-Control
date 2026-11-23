using System;
using System.Globalization;

namespace WpfApp;

// Release version with an optional pre-release suffix ("2.7.2-beta.3").
// System.Version can't express betas, and the beta update channel has to order
// 2.7.2-beta.1 < 2.7.2-beta.10 < 2.7.2 the way SemVer does.
public sealed record AppVersion(int Major, int Minor, int Patch, string? Pre) : IComparable<AppVersion>
{
    public bool IsPrerelease => Pre is not null;
    public Version Core => new(Major, Minor, Patch);

    // Accepts "v2.7.1", "2.7.1-beta.9", "2.7.1.9" (4th part ignored) and
    // InformationalVersion strings with "+commit" build metadata.
    public static AppVersion? TryParse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0) return null;
        }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return null;
        var n = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return new AppVersion(n[0], n[1], n[2], pre);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        int c = Core.CompareTo(other.Core);
        if (c != 0) return c;
        if (Pre is null) return other.Pre is null ? 0 : 1;   // release > its pre-releases
        if (other.Pre is null) return -1;
        var a = Pre.Split('.');
        var b = other.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            bool bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = an && bn ? ai.CompareTo(bi)
              : an ? -1
              : bn ? 1
              : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => Pre is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";
}
