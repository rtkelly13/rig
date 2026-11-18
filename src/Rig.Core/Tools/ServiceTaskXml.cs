using System.Xml.Linq;

namespace Rig.Core.Tools;

/// <summary>
/// The logon task <c>rig tools install</c> registers on Windows. A long-lived service runs without a time limit and is
/// restarted when it fails; a one-shot service gets two hours.
/// </summary>
public static class ServiceTaskXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>The task for <paramref name="target"/>, run as <paramref name="userId"/> at that user's sign-in.</summary>
    /// <param name="target">The service.</param>
    /// <param name="preview">Whether this is the preview environment's task.</param>
    /// <param name="command">The program the action runs: <c>pythonw</c>, or rig itself.</param>
    /// <param name="arguments">The action's command line after <paramref name="command"/>.</param>
    /// <param name="workingDirectory">The folder the service starts in.</param>
    /// <param name="userId">The interactive user the task runs as.</param>
    public static string Render(ServiceTarget target, bool preview, string command, string arguments, string workingDirectory, string userId)
    {
        ArgumentNullException.ThrowIfNull(target);
        var description = $"rig {target.Name}{(preview ? " (preview)" : string.Empty)}: "
            + (target.LongLived ? "started at sign-in and restarted if it stops." : "run once at sign-in.");
        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", description), E("URI", "\\" + ServiceManager.TaskName(target, preview))),
            E("Triggers", E("LogonTrigger", E("Enabled", true), E("UserId", userId), E("Delay", "PT5S"))),
            E("Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"), E("UserId", userId), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false), E("StopIfGoingOnBatteries", false),
                E("AllowHardTerminate", true), E("StartWhenAvailable", true), E("RunOnlyIfNetworkAvailable", false), E("AllowStartOnDemand", true),
                E("Enabled", true), E("Hidden", false), E("RunOnlyIfIdle", false), E("WakeToRun", false),
                E("ExecutionTimeLimit", target.LongLived ? "PT0S" : "PT2H"), E("Priority", 4),
                target.LongLived ? E("RestartOnFailure", E("Interval", "PT1M"), E("Count", 10)) : null),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                E("Exec", E("Command", command), E("Arguments", arguments), E("WorkingDirectory", workingDirectory))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" + task + "\n";
    }

    private static XElement E(string name, params object?[] content) => new(Ns + name, content);
}
