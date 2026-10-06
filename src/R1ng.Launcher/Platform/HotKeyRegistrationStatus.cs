namespace R1ng.Launcher.Platform;

/// <summary>
/// Pure presentation of a global-hotkey registration outcome: the honest summary string shown in the
/// log/manual checks, an optional UI notice, and whether the notice is a hard failure (no hotkey at
/// all) rather than an informational successful fallback.
/// </summary>
public readonly record struct HotKeyRegistrationStatus(string Summary, string? Notice, bool IsFailure)
{
    /// <summary>Points at the always-available UI quit route when the hotkey never came up.</summary>
    public const string UnavailableNotice = "快捷键不可用，点击界面右下角“退出”可关闭本程序";

    /// <summary>
    /// Builds the presentation from the registration result. A successful fallback is informational,
    /// never presented as the preferred hotkey, and a total failure always carries an exit hint.
    /// </summary>
    public static HotKeyRegistrationStatus From(
        bool registered,
        HotKeyDefinition? active,
        string? failureMessage
    )
    {
        if (registered)
        {
            var summary = active?.DisplayName ?? "未注册";
            if (!string.IsNullOrEmpty(failureMessage))
                summary += $"（{failureMessage}）";

            return new HotKeyRegistrationStatus(summary, failureMessage, IsFailure: false);
        }

        var reason = string.IsNullOrEmpty(failureMessage) ? "注册失败" : failureMessage;
        return new HotKeyRegistrationStatus(reason, $"{reason}；{UnavailableNotice}", IsFailure: true);
    }

    /// <summary>
    /// Folds the independent file-scope shortcut into the launcher status. The file shortcut is
    /// informational only: its failure never turns the launcher toggle into a hard failure, so the
    /// process still starts hidden and reachable through the launcher hotkey, with the file shortcut
    /// reported honestly in the banner.
    /// </summary>
    public static HotKeyRegistrationStatus WithFileHotKey(
        HotKeyRegistrationStatus launcher,
        bool fileRegistered,
        HotKeyDefinition? fileActive,
        string? fileFailureMessage
    )
    {
        string? fileNotice = null;
        string summary;

        if (fileRegistered)
        {
            summary = $"{launcher.Summary}；文件快捷键 {fileActive?.DisplayName ?? "已注册"}";
        }
        else
        {
            var reason = string.IsNullOrEmpty(fileFailureMessage)
                ? $"{HotKeyDefinition.CtrlAltSpace.DisplayName} 注册失败，可能被其他程序占用"
                : fileFailureMessage;
            summary = $"{launcher.Summary}；{reason}";
            fileNotice = reason;
        }

        return launcher with { Summary = summary, Notice = CombineNotices(launcher.Notice, fileNotice) };
    }

    private static string? CombineNotices(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first))
            return string.IsNullOrEmpty(second) ? null : second;
        if (string.IsNullOrEmpty(second))
            return first;

        return $"{first}；{second}";
    }
}
