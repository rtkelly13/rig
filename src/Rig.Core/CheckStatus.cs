namespace Rig.Core;

/// <summary>How a package check came out.</summary>
public enum CheckStatus
{
    /// <summary>Nothing to do.</summary>
    Ok,

    /// <summary>Worth a look.</summary>
    Warn,

    /// <summary>Publishing is broken or will break.</summary>
    Fail,

    /// <summary>Not a check: something only a person can confirm.</summary>
    Manual,

    /// <summary>Could not be checked.</summary>
    Skip,
}

/// <summary>Spellings of <see cref="CheckStatus"/> as the JSON and tables show them.</summary>
public static class CheckStatusText
{
    /// <summary><c>ok</c>, <c>warn</c>, <c>fail</c>, <c>manual</c> or <c>skip</c>.</summary>
    public static string Label(this CheckStatus status) => status switch
    {
        CheckStatus.Ok => "ok",
        CheckStatus.Warn => "warn",
        CheckStatus.Fail => "fail",
        CheckStatus.Manual => "manual",
        _ => "skip",
    };
}
