using NetDevPack.Domain;

namespace SwarmRoute.Map.Domain.ValueObjects;

/// <summary>
/// An immutable, symmetric set of interference relationships between roadmap resources keyed by their
/// stable string ids. Two resources "interfere" when their physical footprints overlap such that they
/// cannot be safely occupied by different vehicles simultaneously; an interfering resource is pruned
/// together with the resource it interferes with during planning.
/// </summary>
public sealed class InterferenceSet : ValueObject
{
    private readonly Dictionary<string, IReadOnlySet<string>> _adjacency;

    private InterferenceSet(Dictionary<string, IReadOnlySet<string>> adjacency) => _adjacency = adjacency;

    /// <summary>An empty interference set.</summary>
    public static InterferenceSet Empty { get; } = new(new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal));

    /// <summary>
    /// Builds a symmetric interference set from a sequence of unordered id pairs. Self-pairs are ignored.
    /// </summary>
    public static InterferenceSet FromPairs(IEnumerable<(string A, string B)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var builder = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        void Link(string from, string to)
        {
            if (!builder.TryGetValue(from, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                builder[from] = set;
            }
            set.Add(to);
        }

        foreach (var (a, b) in pairs)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                continue;
            var ta = a.Trim();
            var tb = b.Trim();
            if (string.Equals(ta, tb, StringComparison.Ordinal))
                continue;
            Link(ta, tb);
            Link(tb, ta);
        }

        var adjacency = builder.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlySet<string>)kvp.Value,
            StringComparer.Ordinal);

        return new InterferenceSet(adjacency);
    }

    /// <summary>The ids that interfere with <paramref name="resourceId"/> (empty if none).</summary>
    public IReadOnlySet<string> InterferingWith(string resourceId)
        => _adjacency.TryGetValue(resourceId, out var set) ? set : EmptySet;

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> interfere.</summary>
    public bool Interfere(string a, string b)
        => _adjacency.TryGetValue(a, out var set) && set.Contains(b);

    /// <summary>Number of resources that have at least one interference relationship.</summary>
    public int Count => _adjacency.Count;

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.Ordinal);

    protected override IEnumerable<object> GetEqualityComponents()
    {
        foreach (var key in _adjacency.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            yield return key;
            foreach (var v in _adjacency[key].OrderBy(v => v, StringComparer.Ordinal))
                yield return v;
        }
    }
}
