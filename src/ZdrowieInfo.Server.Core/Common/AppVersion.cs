namespace ZdrowieInfo.Server.Common;

/// <summary>Wersja aplikacji. Musi być równa &lt;Version&gt; w Directory.Build.props (sprawdza to test).</summary>
public static class AppVersion
{
    public const string Current = "0.1.1";

    /// <summary>Zwykłe x.y.z (to samo co update.sh: zi_is_plain_version) – bez „v”, sufiksów i spacji.</summary>
    public static bool IsPlainTriplet(string? version)
    {
        if (string.IsNullOrEmpty(version)) return false;
        var parts = version.Split('.');
        return parts.Length == 3 && parts.All(p => p.Length is > 0 and <= 5 && p.All(char.IsAsciiDigit));
    }

    /// <summary>Porównanie dwóch wersji x.y.z (−1, 0, 1).</summary>
    public static int Compare(string a, string b)
    {
        if (!IsPlainTriplet(a) || !IsPlainTriplet(b)) throw new ArgumentException("Oczekiwana wersja x.y.z.");
        var pa = a.Split('.').Select(int.Parse).ToArray();
        var pb = b.Split('.').Select(int.Parse).ToArray();
        for (var i = 0; i < 3; i++)
            if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
        return 0;
    }
}
