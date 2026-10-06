using R1ng.Launcher.Core;

namespace R1ng.Launcher.Tests;

public class PinyinInitialsTests
{
    [Theory]
    [InlineData("记事本", "jsb")]
    [InlineData("计算器", "jsq")]
    [InlineData("微信", "wx")]
    [InlineData("WPS Office", "wpsoffice")]
    [InlineData("7-Zip", "zip")]
    [InlineData("", "")]
    public void Build_produces_lowercase_initials(string text, string expected)
    {
        Assert.Equal(expected, PinyinInitials.Build(text));
    }

    [Fact]
    public void Build_skips_characters_without_a_known_initial()
    {
        // Symbols and out-of-range CJK never contribute an initial.
        Assert.Equal("ab", PinyinInitials.Build("a◆b"));
    }

    [Fact]
    public void TryGetInitials_handles_ascii_case_and_ambiguous_characters()
    {
        var map = Gb2312PinyinMap.Instance;
        Assert.True(map.TryGetInitials('A', out var upper));
        Assert.Equal("a", upper);

        Assert.True(map.TryGetInitials('长', out var polyphonic));
        Assert.Equal("c", polyphonic);

        Assert.False(map.TryGetInitials('★', out var unknown));
        Assert.Equal(string.Empty, unknown);
    }

    [Fact]
    public void Build_uses_an_injected_map_so_tests_stay_deterministic()
    {
        var map = new StubMap();
        Assert.Equal("zz", PinyinInitials.Build("记事本", map));
    }

    private sealed class StubMap : IPinyinMap
    {
        public bool TryGetInitials(char value, out string initials)
        {
            if (value is >= 'a' and <= 'z')
            {
                initials = value.ToString();
                return true;
            }

            if (value is '记' or '事')
            {
                initials = "z";
                return true;
            }

            initials = string.Empty;
            return false;
        }
    }
}
