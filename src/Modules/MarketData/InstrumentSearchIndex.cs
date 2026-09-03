using System.Diagnostics.CodeAnalysis;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.MarketData;

/// <summary>
/// An immutable, searchable snapshot of one connector's instrument master.
///
/// Built once per load and then read-only, which is what makes it safe to share across every
/// concurrent request without a lock: publication is a single reference swap in
/// <see cref="InstrumentMaster"/>, and a reader either sees the whole old snapshot or the
/// whole new one, never a half-rebuilt index.
///
/// WHY WE RANK RATHER THAN FILTER. A trader typing "INFY" into a search box on an Indian
/// venue is matched by the cash scrip, by every INFY future, and by several thousand INFY
/// options. Returning the first twenty rows the master happens to list is useless — they are
/// all options. The ordering in <see cref="Candidate"/> is the actual product decision here:
/// match quality first, then live contracts before expired ones, then cash before
/// derivatives, then the nearest expiry.
/// </summary>
public sealed class InstrumentSearchIndex
{
    private readonly Entry[] _entries;
    private readonly Dictionary<InstrumentKey, InstrumentDefinition> _byKey;

    private InstrumentSearchIndex(
        Entry[] entries,
        Dictionary<InstrumentKey, InstrumentDefinition> byKey,
        DateTimeOffset asOf)
    {
        _entries = entries;
        _byKey = byKey;
        AsOf = asOf;
    }

    /// <summary>When the underlying master was read from the broker.</summary>
    public DateTimeOffset AsOf { get; }

    /// <summary>How many instruments this snapshot holds.</summary>
    public int Count => _entries.Length;

    /// <summary>
    /// Builds the index. Case folding happens HERE, once per instrument, rather than on every
    /// comparison of every search — the difference across a few hundred thousand rows and a
    /// per-keystroke search is the difference between a search box that feels instant and one
    /// that does not.
    /// </summary>
    public static InstrumentSearchIndex Build(
        IReadOnlyCollection<InstrumentDefinition> instruments,
        DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var today = DateOnly.FromDateTime(asOf.UtcDateTime);
        var entries = new Entry[instruments.Count];
        var byKey = new Dictionary<InstrumentKey, InstrumentDefinition>(instruments.Count);

        var i = 0;
        foreach (var definition in instruments)
        {
            entries[i++] = new Entry(
                definition,
                definition.Key.Symbol.ToUpperInvariant(),
                definition.Name.ToUpperInvariant(),
                IsExpired(definition.Key, today));

            // Last row wins on a duplicate key. The master is the broker's own list and a
            // duplicate there is its problem, not a reason to refuse the whole snapshot.
            byKey[definition.Key] = definition;
        }

        return new InstrumentSearchIndex(entries, byKey, asOf);
    }

    /// <summary>Exact lookup by canonical key. This is what makes a resolve a dictionary hit.</summary>
    public bool TryResolve(InstrumentKey key, [NotNullWhen(true)] out InstrumentDefinition? definition) =>
        _byKey.TryGetValue(key, out definition);

    /// <summary>
    /// Ranked substring search over symbol and name.
    ///
    /// A full scan, deliberately: the alternative is a prefix trie that still cannot answer
    /// the "contains" half of the query, and a scan over a few hundred thousand precomputed
    /// uppercase strings costs single-digit milliseconds. Only the best <paramref name="limit"/>
    /// candidates are ever held, so a one-letter query that matches half the master still
    /// allocates twenty results rather than a hundred thousand.
    /// </summary>
    public IReadOnlyList<InstrumentDefinition> Search(string query, int limit)
    {
        var needle = query?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(needle) || limit <= 0)
        {
            return [];
        }

        // A bounded max-heap: the WORST candidate held is always at the front, so once the
        // heap is full each new match costs one comparison to reject.
        var best = new PriorityQueue<InstrumentDefinition, Candidate>(limit + 1, Candidate.WorstFirst);

        foreach (var entry in _entries)
        {
            var tier = Match(entry, needle);
            if (tier is null)
            {
                continue;
            }

            var candidate = new Candidate(tier.Value, entry);

            if (best.Count < limit)
            {
                best.Enqueue(entry.Definition, candidate);
                continue;
            }

            // EnqueueDequeue rather than Enqueue-then-Dequeue: it never grows the heap past
            // `limit`, and it is a no-op when the incoming candidate is the worse one.
            best.EnqueueDequeue(entry.Definition, candidate);
        }

        // The heap drains worst-first; the caller wants best-first.
        var results = new InstrumentDefinition[best.Count];
        for (var i = results.Length - 1; i >= 0; i--)
        {
            results[i] = best.Dequeue();
        }

        return results;
    }

    /// <summary>
    /// Match quality, low is better, null for no match. The tiers are ordered by how likely
    /// the match is to be the thing the trader actually typed.
    /// </summary>
    private static int? Match(in Entry entry, string needle)
    {
        if (entry.Symbol.Equals(needle, StringComparison.Ordinal))
        {
            return 0;
        }

        if (entry.Symbol.StartsWith(needle, StringComparison.Ordinal))
        {
            return 1;
        }

        if (StartsWord(entry.Name, needle))
        {
            return 2;
        }

        if (entry.Symbol.Contains(needle, StringComparison.Ordinal))
        {
            return 3;
        }

        return entry.Name.Contains(needle, StringComparison.Ordinal) ? 4 : null;
    }

    /// <summary>
    /// True when the needle starts a WORD of the name, not merely appears inside one. "MAH"
    /// should find "MAHINDRA &amp; MAHINDRA" ahead of "RAMA PHOSPHATES", which merely contains
    /// the letters.
    /// </summary>
    private static bool StartsWord(string name, string needle)
    {
        var from = 0;
        while (from <= name.Length - needle.Length)
        {
            var at = name.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            if (at == 0 || !char.IsLetterOrDigit(name[at - 1]))
            {
                return true;
            }

            from = at + 1;
        }

        return false;
    }

    private static bool IsExpired(InstrumentKey key, DateOnly today) =>
        key.Expiry is { } expiry && expiry < today;

    /// <summary>One indexed instrument, with its case-folded forms precomputed.</summary>
    private readonly record struct Entry(
        InstrumentDefinition Definition,
        string Symbol,
        string Name,
        bool IsExpired);

    /// <summary>
    /// The sort key for one match. Every field is a deliberate tie-break, applied in order:
    ///
    ///  1. <see cref="Tier"/> — match quality; an exact symbol beats a name substring.
    ///  2. <see cref="Expired"/> — a contract that can no longer be traded goes last, always.
    ///  3. <see cref="Derivative"/> — "INFY" means the share unless you asked for a contract.
    ///  4. <see cref="ExpiryDay"/> — among contracts, the nearest expiry is the liquid one.
    ///  5. <see cref="SymbolLength"/> — shorter symbols are the better guess at what was meant.
    ///  6. <see cref="Symbol"/> — ordinal, purely so the order is stable across loads.
    /// </summary>
    private readonly record struct Candidate
    {
        public Candidate(int tier, in Entry entry)
        {
            Tier = tier;
            Expired = entry.IsExpired;
            Derivative = entry.Definition.Key.IsDerivative;
            ExpiryDay = entry.Definition.Key.Expiry?.DayNumber ?? int.MaxValue;
            SymbolLength = entry.Symbol.Length;
            Symbol = entry.Symbol;
        }

        public int Tier { get; }

        public bool Expired { get; }

        public bool Derivative { get; }

        public int ExpiryDay { get; }

        public int SymbolLength { get; }

        public string Symbol { get; }

        /// <summary>Orders WORST first, which is what a bounded max-heap needs at its front.</summary>
        public static IComparer<Candidate> WorstFirst { get; } = new WorstFirstComparer();

        private sealed class WorstFirstComparer : IComparer<Candidate>
        {
            public int Compare(Candidate x, Candidate y)
            {
                // Reversed: "greater" here means "worse", so the worst sorts to the heap root.
                var byTier = y.Tier.CompareTo(x.Tier);
                if (byTier != 0)
                {
                    return byTier;
                }

                var byExpired = y.Expired.CompareTo(x.Expired);
                if (byExpired != 0)
                {
                    return byExpired;
                }

                var byDerivative = y.Derivative.CompareTo(x.Derivative);
                if (byDerivative != 0)
                {
                    return byDerivative;
                }

                var byExpiry = y.ExpiryDay.CompareTo(x.ExpiryDay);
                if (byExpiry != 0)
                {
                    return byExpiry;
                }

                var byLength = y.SymbolLength.CompareTo(x.SymbolLength);
                return byLength != 0
                    ? byLength
                    : string.CompareOrdinal(y.Symbol, x.Symbol);
            }
        }
    }
}
