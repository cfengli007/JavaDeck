namespace JavaDeck.Services;

/// <summary>Minecraft 版本 → 所需 Java 主版本的经验规则（官方 version.json 的 javaVersion 为准，此表用于离线兜底）。</summary>
public static class McJavaRule
{
    /// <summary>本地已知的映射锚点（版本号, Java 主版本）。取「不小于该版本的最小锚点」。</summary>
    private static readonly (string version, int java)[] Anchors =
    {
        ("0.0",   8),
        ("1.17",  16),
        ("1.18",  17),
        ("1.20.5",21),
        ("1.21",  21),
        ("26.0",  25),
    };

    public static int Guess(string mcVersion)
    {
        var v = Parse(mcVersion);
        if (v == null) return 8;

        var best = 8;
        foreach (var (anchor, java) in Anchors)
        {
            var a = Parse(anchor);
            if (a == null) continue;
            if (Compare(v, a) >= 0) best = java;
        }
        return best;
    }

    /// <summary>把 "1.21.4" / "26.3" / "1.20.5-pre1" 解析成数字段。</summary>
    private static int[]? Parse(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var core = version;
        var dash = core.IndexOfAny(new[] { '-', '_', ' ' });
        if (dash > 0) core = core[..dash];
        var parts = core.Split('.');
        var nums = new List<int>();
        foreach (var p in parts)
        {
            var digits = new string(p.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            nums.Add(int.Parse(digits));
        }
        return nums.Count == 0 ? null : nums.ToArray();
    }

    private static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>从已装 JDK 里挑一个能跑该 MC 版本的（优先恰好等于，其次更高的大版本）。</summary>
    public static InstalledJdk? PickFor(IEnumerable<InstalledJdk> jdks, int required)
    {
        var list = jdks.OrderBy(j => j.FeatureVersion).ToList();
        return list.FirstOrDefault(j => j.FeatureVersion == required)
            ?? list.FirstOrDefault(j => j.FeatureVersion >= required);
    }

    public static string Describe(int java) => java switch
    {
        8 => "Java 8（1.16.5 及更早）",
        11 => "Java 11（少见，部分整合包）",
        16 => "Java 16（1.17.x 过渡版本，实际多用 17）",
        17 => "Java 17（1.18 ~ 1.20.4）",
        21 => "Java 21（1.20.5 ~ 1.21.x）",
        25 => "Java 25（26.x 起）",
        _ => "Java " + java,
    };
}
