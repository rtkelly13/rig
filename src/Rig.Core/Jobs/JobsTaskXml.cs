using System.Globalization;
using System.Xml.Linq;

namespace Rig.Core.Jobs;

/// <summary>The coordinator's three tasks. The Logon and Tick actions run C#; only the display guard remains Python.</summary>
public static class JobsTaskXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Renders the task with interactive, least-privilege identity and the existing timing/restart policy.</summary>
    public static string Render(string name, JobsInstallTarget target, string userId, DateTimeOffset start)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!CoordinatorTasks.All.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unknown coordinator task", nameof(name));
        }

        var guard = name == CoordinatorTasks.Guard;
        var tick = name == CoordinatorTasks.Tick;
        var trigger = tick
            ? E("TimeTrigger", E("StartBoundary", start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)), E("Enabled", true),
                E("Repetition", E("Interval", $"PT{Job.TickMinutes}M"), E("StopAtDurationEnd", false)))
            : E("LogonTrigger", E("Enabled", true), E("UserId", userId), E("Delay", guard ? "PT30S" : "PT5S"));
        var description = guard ? "Put windows back after a monitor drops out or the machine wakes."
            : tick ? $"Every {Job.TickMinutes} minutes: tidy Chrome tabs and run due housekeeping (desktop-manager jobs)."
            : "Restore the workspace, then run due housekeeping (desktop-manager jobs, logon).";
        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", description), E("URI", "\\" + name)), E("Triggers", trigger),
            E("Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"), E("UserId", userId), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false), E("StopIfGoingOnBatteries", false),
                E("AllowHardTerminate", true), E("StartWhenAvailable", !tick), E("RunOnlyIfNetworkAvailable", false), E("AllowStartOnDemand", true),
                E("Enabled", true), E("Hidden", false), E("RunOnlyIfIdle", false), E("WakeToRun", false), E("ExecutionTimeLimit", guard ? "PT0S" : "PT2H"),
                E("Priority", tick ? 7 : 4), guard ? E("RestartOnFailure", E("Interval", "PT1M"), E("Count", 10)) : null),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"), E("Exec", E("Command", guard ? target.Pythonw : target.Rigw),
                E("Arguments", guard ? $"-m desktop_manager.cli workspace guard --config \"{target.Plan}\""
                    : $"jobs run --trigger {(tick ? "tick" : "logon")} --scheduled"), E("WorkingDirectory", target.Root))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" + task + "\n";
    }

    private static XElement E(string name, params object?[] content) => new(Ns + name, content);
}
