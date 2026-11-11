using Rig.Cli.Workspace;
using Rig.Core.Orca;
using Rig.Core.Tests.Orca;

namespace Rig.Cli.Tests.Estate;

/// <summary>An Orca that is never running: an empty environment and a home with no runtime file, so no test reaches a real one.</summary>
internal static class NoOrca
{
    /// <summary>The host for tests that never ask Orca anything.</summary>
    public static OrcaHost Host => new(new OrcaLocations(_ => null, "/nonexistent-home-for-rig-tests", OrcaOs.Linux), new FakeOrca(), TextWriter.Null);
}
