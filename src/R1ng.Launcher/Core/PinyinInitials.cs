using System.Text;

namespace R1ng.Launcher.Core;

/// <summary>Supplies pinyin initials for individual characters. Injectable so matching stays testable.</summary>
public interface IPinyinMap
{
    /// <summary>Returns the lowercase pinyin initial(s) of <paramref name="value"/>, or false if unknown.</summary>
    bool TryGetInitials(char value, out string initials);
}

/// <summary>
/// Own pinyin-initial resolver based on the classic GB2312 (code page 936) sort order.
/// <para>
/// GB2312 level-1 hanzi are stored in pinyin order, so a code-point range table maps each character
/// to its first letter. A small override list covers the most common polyphonic characters. The
/// mapping is intentionally single-reading and covers common simplified Chinese only; characters
/// outside GB2312 level-1 return no initial (documented limitation in README).
/// </para>
/// </summary>
public sealed class Gb2312PinyinMap : IPinyinMap
{
    public static Gb2312PinyinMap Instance { get; } = new();

    private const int Gb2312FirstCode = -20319;
    private const int Gb2312LastCode = -10247;

    private static readonly (int LowerCode, char Initial)[] Ranges =
    [
        (-20319, 'a'), (-20283, 'b'), (-19775, 'c'), (-19218, 'd'), (-18710, 'e'), (-18526, 'f'),
        (-18239, 'g'), (-17922, 'h'), (-17417, 'j'), (-16474, 'k'), (-16212, 'l'), (-15640, 'm'),
        (-15165, 'n'), (-14922, 'o'), (-14914, 'p'), (-14630, 'q'), (-14149, 'r'), (-14090, 's'),
        (-13318, 't'), (-12838, 'w'), (-12556, 'x'), (-11847, 'y'), (-11055, 'z'),
    ];

    // Dominant reading overrides for characters that are common in app or file names.
    private static readonly Dictionary<char, string> Overrides = new()
    {
        ['长'] = "c", ['重'] = "c", ['行'] = "x", ['乐'] = "l", ['还'] = "h", ['都'] = "d",
        ['地'] = "d", ['得'] = "d", ['会'] = "h", ['着'] = "z", ['为'] = "w", ['了'] = "l",
        ['只'] = "z", ['种'] = "z", ['中'] = "z", ['调'] = "d", ['量'] = "l", ['系'] = "x",
        ['率'] = "l", ['传'] = "c", ['单'] = "d", ['藏'] = "c", ['查'] = "c", ['差'] = "c",
        ['处'] = "c", ['旁'] = "p", ['区'] = "q", ['解'] = "j", ['厦'] = "x",
        ['便'] = "b", ['朝'] = "c", ['曾'] = "c", ['参'] = "c", ['朴'] = "p", ['沈'] = "s",
    };

    private readonly Encoding? _gb2312;

    private Gb2312PinyinMap()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _gb2312 = Encoding.GetEncoding(936);
        }
        catch (Exception)
        {
            // Without code page 936 the map degrades to ASCII letters only.
            _gb2312 = null;
        }
    }

    public bool TryGetInitials(char value, out string initials)
    {
        if (value is >= 'a' and <= 'z')
        {
            initials = value.ToString();
            return true;
        }

        if (value is >= 'A' and <= 'Z')
        {
            initials = char.ToLowerInvariant(value).ToString();
            return true;
        }

        if (Overrides.TryGetValue(value, out var overridden))
        {
            initials = overridden;
            return true;
        }

        if (TryGetGb2312Initial(value, out var initial))
        {
            initials = initial.ToString();
            return true;
        }

        initials = string.Empty;
        return false;
    }

    private bool TryGetGb2312Initial(char value, out char initial)
    {
        initial = default;
        if (_gb2312 is null || value < '\u4e00' || value > '\u9fff')
            return false;

        var bytes = _gb2312.GetBytes(value.ToString());
        if (bytes.Length != 2)
            return false;

        var code = (bytes[0] << 8) + bytes[1] - 65536;
        if (code < Gb2312FirstCode || code > Gb2312LastCode)
            return false;

        for (var index = Ranges.Length - 1; index >= 0; index--)
        {
            if (code >= Ranges[index].LowerCode)
            {
                initial = Ranges[index].Initial;
                return true;
            }
        }

        return false;
    }
}

/// <summary>Convenience helpers around <see cref="IPinyinMap"/>.</summary>
public static class PinyinInitials
{
    /// <summary>Concatenated lowercase initials of every letter/CJK character in <paramref name="text"/>.</summary>
    public static string Build(string text, IPinyinMap? map = null)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        map ??= Gb2312PinyinMap.Instance;
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (map.TryGetInitials(character, out var initials) && initials.Length > 0)
                builder.Append(initials);
        }

        return builder.ToString();
    }
}
