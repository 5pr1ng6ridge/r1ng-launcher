namespace R1ng.Launcher.Core;

/// <summary>Result of matching a query against one candidate string.</summary>
public readonly record struct NameMatch(bool Matched, int Score, IReadOnlyList<int> Indices)
{
    public static NameMatch None { get; } = new(false, 0, Array.Empty<int>());
}

/// <summary>
/// Determines whether a query matches a candidate name and how strong the match is.
/// <para>
/// The ladder (highest first) is: exact, prefix, word-boundary prefix, substring, pinyin-initial
/// prefix/substring, then an in-order character subsequence. Scores are stable integers so ranking
/// is deterministic and unit-testable.
/// </para>
/// </summary>
public static class NameMatcher
{
    public const int ExactScore = 1000;
    public const int PrefixScore = 900;
    public const int WordPrefixScore = 800;
    public const int SubstringScore = 700;
    public const int InitialsExactScore = 660;
    public const int InitialsPrefixScore = 640;
    public const int InitialsSubstringScore = 600;
    public const int SubsequenceScore = 400;
    public const int InitialsSubsequenceScore = 300;

    private static readonly char[] WordSeparators = [' ', '-', '_', '.', '(', '[', '/', '\\', '·'];

    /// <summary>Literal-only match; used for file results, which Everything already matched literally.</summary>
    public static NameMatch MatchLiteral(string? candidate, string? query)
    {
        candidate ??= string.Empty;
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return new NameMatch(true, 0, Array.Empty<int>());
        if (candidate.Length == 0)
            return NameMatch.None;

        return MatchCore(candidate, query, initials: null);
    }

    /// <summary>Literal + pinyin-initial match; used for applications and history entries.</summary>
    public static NameMatch MatchText(SearchTextIndex index, string? query)
    {
        ArgumentNullException.ThrowIfNull(index);
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return new NameMatch(true, 0, Array.Empty<int>());
        if (index.Text.Length == 0)
            return NameMatch.None;

        return MatchCore(index.Text, query, index);
    }

    private static NameMatch MatchCore(string candidate, string query, SearchTextIndex? initials)
    {
        var literal = MatchLiteralCore(candidate, query);
        var best = literal;
        var score = literal.Matched ? literal.Score : int.MinValue;

        if (initials is not null && initials.Initials.Length > 0)
        {
            var normalized = NormalizeQueryLetters(query);
            if (normalized.Length > 0)
            {
                var viaInitials = MatchInitials(initials, normalized);
                if (viaInitials.Matched && viaInitials.Score > score)
                {
                    best = viaInitials;
                    score = viaInitials.Score;
                }
            }
        }

        if (best.Matched)
            return best;

        // Last resort: in-order subsequence of the literal text, then of the initials.
        var subsequence = MatchSubsequence(candidate, query);
        if (subsequence.Matched)
            return subsequence;

        if (initials is not null && initials.Initials.Length > 0)
        {
            var initialsSubsequence = MatchInitialsSubsequence(initials, query);
            if (initialsSubsequence.Matched)
                return initialsSubsequence;
        }

        return NameMatch.None;
    }

    private static NameMatch MatchLiteralCore(string candidate, string query)
    {
        if (string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase))
            return new NameMatch(true, ExactScore, AllIndices(query.Length));

        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return new NameMatch(true, PrefixScore - LengthPenalty(candidate), AllIndices(query.Length));

        var wordIndex = FindWordStart(candidate, query);
        if (wordIndex >= 0)
            return new NameMatch(true, WordPrefixScore - LengthPenalty(candidate) - wordIndex, Range(wordIndex, query.Length));

        var containsIndex = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (containsIndex >= 0)
            return new NameMatch(true, SubstringScore - LengthPenalty(candidate) - containsIndex, Range(containsIndex, query.Length));

        return NameMatch.None;
    }

    private static NameMatch MatchInitials(SearchTextIndex initials, string query)
    {
        if (string.Equals(initials.Initials, query, StringComparison.OrdinalIgnoreCase))
            return new NameMatch(true, InitialsExactScore, initials.MapInitialsRange(0, query.Length));

        if (initials.Initials.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return new NameMatch(
                true,
                InitialsPrefixScore - LengthPenalty(initials.Initials),
                initials.MapInitialsRange(0, query.Length)
            );

        var index = initials.Initials.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
            return new NameMatch(
                true,
                InitialsSubstringScore - LengthPenalty(initials.Initials) - index,
                initials.MapInitialsRange(index, query.Length)
            );

        return NameMatch.None;
    }

    private static NameMatch MatchSubsequence(string candidate, string query)
    {
        var indices = new List<int>(query.Length);
        var position = 0;
        foreach (var character in query)
        {
            var found = -1;
            for (var index = position; index < candidate.Length; index++)
            {
                if (char.ToLowerInvariant(candidate[index]) == char.ToLowerInvariant(character))
                {
                    found = index;
                    break;
                }
            }

            if (found < 0)
                return NameMatch.None;

            indices.Add(found);
            position = found + 1;
        }

        if (indices.Count == 0)
            return NameMatch.None;

        var spread = indices[^1] - indices[0] + 1;
        return new NameMatch(true, SubsequenceScore - spread - LengthPenalty(candidate), indices);
    }

    private static NameMatch MatchInitialsSubsequence(SearchTextIndex initials, string query)
    {
        var mapped = new List<int>(query.Length);
        var position = 0;
        foreach (var character in query)
        {
            var found = -1;
            for (var index = position; index < initials.Initials.Length; index++)
            {
                if (initials.Initials[index] == char.ToLowerInvariant(character))
                {
                    found = index;
                    break;
                }
            }

            if (found < 0)
                return NameMatch.None;

            mapped.Add(found);
            position = found + 1;
        }

        if (mapped.Count == 0)
            return NameMatch.None;

        var indices = initials.MapInitialsRange(mapped[0], mapped[^1] - mapped[0] + 1);
        return new NameMatch(true, InitialsSubsequenceScore - initials.Initials.Length, indices);
    }

    /// <summary>
    /// Query characters that cannot be typed as pinyin initials (digits, symbols) keep their literal
    /// form so mixed queries such as "wps2" still resolve through the initials path.
    /// </summary>
    private static string NormalizeQueryLetters(string query)
    {
        var builder = new System.Text.StringBuilder(query.Length);
        foreach (var character in query)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static int FindWordStart(string candidate, string query)
    {
        for (var index = 1; index + query.Length <= candidate.Length; index++)
        {
            if (!WordSeparators.Contains(candidate[index - 1]))
                continue;

            if (candidate.AsSpan(index).StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private static int LengthPenalty(string value) => Math.Min(20, value.Length / 8);

    private static IReadOnlyList<int> AllIndices(int length)
    {
        if (length <= 0)
            return Array.Empty<int>();

        var indices = new int[length];
        for (var index = 0; index < length; index++)
            indices[index] = index;

        return indices;
    }

    private static IReadOnlyList<int> Range(int start, int length)
    {
        if (length <= 0)
            return Array.Empty<int>();

        var indices = new int[length];
        for (var index = 0; index < length; index++)
            indices[index] = start + index;

        return indices;
    }
}
