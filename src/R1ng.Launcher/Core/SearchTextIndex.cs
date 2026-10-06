namespace R1ng.Launcher.Core;

/// <summary>
/// Pre-computed searchable view of a display string: the literal text, its pinyin initials and the
/// mapping from each initials character back to the source index (used for highlighting).
/// </summary>
public sealed class SearchTextIndex
{
    private SearchTextIndex(string text, string initials, int[] initialsOwnerOffsets, bool[] initialsOwnerIsCjk)
    {
        Text = text;
        Initials = initials;
        InitialsOwnerOffsets = initialsOwnerOffsets;
        InitialsOwnerIsCjk = initialsOwnerIsCjk;
    }

    public string Text { get; }

    /// <summary>Lowercase initials of every letter/CJK character, in source order.</summary>
    public string Initials { get; }

    /// <summary>For each character of <see cref="Initials"/>, the index in <see cref="Text"/> it came from.</summary>
    private int[] InitialsOwnerOffsets { get; }

    private bool[] InitialsOwnerIsCjk { get; }

    public static SearchTextIndex Create(string? text, IPinyinMap? pinyinMap = null)
    {
        text ??= string.Empty;
        pinyinMap ??= Gb2312PinyinMap.Instance;

        var initials = new System.Text.StringBuilder(text.Length);
        var owners = new List<int>(text.Length);
        var isCjk = new List<bool>(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (!pinyinMap.TryGetInitials(character, out var mapped) || mapped.Length == 0)
                continue;

            var cjk = character > '\u2e7f';
            foreach (var initial in mapped)
            {
                initials.Append(initial);
                owners.Add(index);
                isCjk.Add(cjk);
            }
        }

        return new SearchTextIndex(text, initials.ToString(), owners.ToArray(), isCjk.ToArray());
    }

    /// <summary>Source indices covered by the initials range [start, start+length).</summary>
    public IReadOnlyList<int> MapInitialsRange(int start, int length)
    {
        if (length <= 0 || start < 0 || start >= InitialsOwnerOffsets.Length)
            return Array.Empty<int>();

        var end = Math.Min(InitialsOwnerOffsets.Length, start + length);
        var indices = new List<int>(end - start);
        for (var index = start; index < end; index++)
        {
            var owner = InitialsOwnerOffsets[index];
            if (indices.Count == 0 || indices[^1] != owner)
                indices.Add(owner);
        }

        return indices;
    }

    /// <summary>True when the matched initials character came from a CJK character.</summary>
    public bool IsCjkInitial(int initialsIndex) =>
        initialsIndex >= 0 && initialsIndex < InitialsOwnerIsCjk.Length && InitialsOwnerIsCjk[initialsIndex];
}
