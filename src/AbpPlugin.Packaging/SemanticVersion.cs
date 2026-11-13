namespace AbpPlugin.Packaging;

/// <summary>Minimal semver (major.minor.patch) and range checks such as "&gt;=1.0.0 &lt;2.0.0".</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch) : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string text)
    {
        string core = text.Split('-', '+')[0];
        string[] parts = core.Split('.');
        if (parts.Length is < 1 or > 3 || !parts.All(p => int.TryParse(p, out _)))
        {
            throw new PluginException($"'{text}' is not a valid version.");
        }

        int Part(int i) => i < parts.Length ? int.Parse(parts[i]) : 0;
        return new SemanticVersion(Part(0), Part(1), Part(2));
    }

    public int CompareTo(SemanticVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    /// <summary>Space-separated comparators, all of which must hold: "&gt;=0.1.0 &lt;1.0.0".</summary>
    public bool Satisfies(string range)
    {
        foreach (string comparator in range.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            (string op, string version) = comparator switch
            {
                _ when comparator.StartsWith(">=") => (">=", comparator[2..]),
                _ when comparator.StartsWith("<=") => ("<=", comparator[2..]),
                _ when comparator.StartsWith('>') => (">", comparator[1..]),
                _ when comparator.StartsWith('<') => ("<", comparator[1..]),
                _ when comparator.StartsWith('=') => ("=", comparator[1..]),
                _ => ("=", comparator),
            };

            SemanticVersion bound = Parse(version);
            bool ok = op switch
            {
                ">=" => this >= bound,
                "<=" => this <= bound,
                ">" => this > bound,
                "<" => this < bound,
                _ => CompareTo(bound) == 0,
            };

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
