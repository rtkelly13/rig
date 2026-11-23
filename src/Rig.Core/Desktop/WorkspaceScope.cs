using Rig.Core.Text;

namespace Rig.Core.Desktop;

/// <summary>
/// Narrowing a plan or a status capture to one desktop or one screen, as <c>workspace audit</c> and <c>restore</c> do
/// with <c>--desktop</c> and <c>--screen</c>.
/// </summary>
public static class WorkspaceScope
{
    /// <summary>Only the desktop named <paramref name="desktop"/>, ignoring case; every desktop when null.</summary>
    public static WorkspacePlan ToDesktop(WorkspacePlan plan, string? desktop)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (desktop is null)
        {
            return plan;
        }

        var wanted = UnicodeText.CaseFold(desktop);
        return plan with
        {
            Desktops = [.. plan.Desktops.Where(item => UnicodeText.CaseFold(item.Name) == wanted)],
        };
    }

    /// <summary>Only the entries placed on <paramref name="screen"/>, and the windows captured on it.</summary>
    public static WorkspacePlan ToScreen(WorkspacePlan plan, int? screen)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (screen is not { } number)
        {
            return plan;
        }

        return plan with
        {
            Desktops =
            [
                .. plan.Desktops.Select(item => item with
                {
                    Launch = [.. item.Launch.Where(entry => entry.Placement?.Screen == number)],
                    CapturedWindows = [.. item.CapturedWindows.Where(window => window.ScreenNumber == number)],
                }),
            ],
        };
    }

    /// <summary>The plan narrowed to <paramref name="desktop"/> and then <paramref name="screen"/>.</summary>
    public static WorkspacePlan For(WorkspacePlan plan, string? desktop, int? screen) => ToScreen(ToDesktop(plan, desktop), screen);
}
