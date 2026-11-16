"""The rig web host: the Python routes still in this child (jobs, C4 and the estate graph).

The C# host (`src/Rig.Web`) owns the port and serves the SPA, estate, triage, ops, telemetry and database backup;
it forwards everything else here.

Asset Studio is not mounted here: `image-reconcile serve` (tools/image-drift) runs it on its own
port, and the studio page calls that server directly.
"""
