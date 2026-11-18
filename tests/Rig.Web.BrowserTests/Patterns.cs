using System.Text.RegularExpressions;

namespace Rig.Web.BrowserTests;

/// <summary>The few accessible names a test matches by pattern rather than by whole string.</summary>
internal static partial class Patterns
{
    [GeneratedRegex("Stack #20")]
    public static partial Regex Stack20();

    [GeneratedRegex("^Inbox$")]
    public static partial Regex InboxOnly();

    [GeneratedRegex("JOBS")]
    public static partial Regex Jobs();

    [GeneratedRegex("BACKUP_DB")]
    public static partial Regex BackupDb();
}
