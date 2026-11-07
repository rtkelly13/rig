---
name: disk-cleanup
description: Triage, diagnose, and clean up disk space across macOS, Windows, and Linux. Use when the user is running out of disk space, wants to reclaim storage, analyze disk usage, prune caches, clean package managers, or remove stale worktrees.
---

# Disk Cleanup

A repeatable, safe, phased runbook for diagnosing disk pressure, finding persistent disk hogs, and reclaiming storage across macOS, Windows, and Linux.

---

## Core Philosophy

1. **Measure First**: Never guess. Always establish capacity baselines (`df -h`) before touching anything.
2. **Distinguish Transient Caches from Real File Hogs**:
   - Package manager caches (`uv`, `pnpm`, `NuGet`) provide emergency breathing room but refill naturally during routine development.
   - Sustainable multi-gigabyte relief comes from discovering **loose worktrees**, **cloud duplicate downloads**, and **dormant application suites**.
3. **The Risk Gate Invariant**:
   - Never delete branches or checkouts holding uncommitted changes, stashes, or commits that exist on no remote.
4. **Cloud Storage Invariant**:
   - Google Drive and cloud storage are strictly **read-only and backup targets**. Never delete files from cloud storage during local cleanup.
5. **Verify Space Post-Run**: Always quantify reclaimed gigabytes before claiming completion.

---

## The 5 Phases of Disk Cleanup

Follow these phases in strict hierarchical order. Each phase is documented in its own detailed runbook:

| Phase | Focus | Primary Actions | Guide |
|:---|:---|:---|:---|
| **Phase 0** | **Capacity & Risk Gate** | Measure capacity; check `estate risk` for uncommitted/unpushed work; check APFS snapshots. | [`phases/phase-0-measurement-and-risk.md`](phases/phase-0-measurement-and-risk.md) |
| **Phase 1** | **Transient Cache Relief** | Emergency headroom (>95% full): clean `uv`, `pnpm`, `nuget`, `npm`, `brew`, `docker`. | [`phases/phase-1-transient-caches.md`](phases/phase-1-transient-caches.md) |
| **Phase 2** | **Worktrees & Git Tidy** | Prune squash-merged branches via `estate tidy --merged`; detect external estate worktrees. | [`phases/phase-2-worktrees-and-git-tidy.md`](phases/phase-2-worktrees-and-git-tidy.md) |
| **Phase 3** | **Downloads & Cloud Dedupe** | Run `estate downloads`; verify 100% duplicate media in Google Drive; move unique photos. | [`phases/phase-3-downloads-and-cloud-dedupe.md`](phases/phase-3-downloads-and-cloud-dedupe.md) |
| **Phase 4** | **Applications & Soundbanks** | Run `estate apps`; audit dormant apps (>180d); remove associated Logic/GarageBand sample banks. | [`phases/phase-4-applications-and-audio.md`](phases/phase-4-applications-and-audio.md) |
| **Phase 5** | **Updater Archives & VMs** | Purge ShipIt/CRX update caches; clean `/private/tmp`; evaluate Claude VM & Docker raw disks. | [`phases/phase-5-updater-archives-and-vms.md`](phases/phase-5-updater-archives-and-vms.md) |

---

## Platform-Specific Reference Runbooks

For operating system nuances and native tools:
- **macOS Deep Runbook**: [`references/macos.md`](references/macos.md) (DaisyDisk, Finder AppleScript, `/Library/Audio`, ShipIt)
- **Windows Runbook**: [`references/windows.md`](references/windows.md) (PowerShell commands, WSL virtual disks, AppData)
- **Linux Runbook**: [`references/linux.md`](references/linux.md) (Journal logs, apt/yum caches, tmpfiles)

---

## Post-Run Verification Checklist

Always produce a summary for the user reporting:
1. **Initial free space vs. final free space** (and capacity percentage drop).
2. **Breakdown of gigabytes reclaimed** across each phase.
3. **Confirmation of invariants**: No unpushed commits and no cloud files deleted.
