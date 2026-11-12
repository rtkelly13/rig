# Phase 0: Capacity Measurement & Risk Gate

**Rule: Never delete before measuring drive capacity and verifying irreplaceable local work.**

---

## 1. High-Level Capacity Check

Always establish a baseline before touching anything:

### macOS
```bash
df -h / /System/Volumes/Data
```
Look at:
- `Capacity %`: If >90%, system performance and OS paging are degraded. If >98%, system operations can fail.
- `Avail`: Note the exact gigabytes available to measure reclaimed space at the end.

### Linux
```bash
df -h /
```

### Windows (PowerShell)
```powershell
Get-PSDrive -PSProvider FileSystem | Select-Object Name, @{N="Used(GB)";E={[math]::round($_.Used/1GB,1)}}, @{N="Free(GB)";E={[math]::round($_.Free/1GB,1)}}
```

---

## 2. Estate Unpushed Risk Gate

Before running any cleanups across repositories, verify what exists on this machine and nowhere else:

```bash
rig estate risk
```

This reports:
1. **Uncommitted files**: Dirty files that would be destroyed by a hard reset or clean.
2. **Stashes**: Unpopped stashes across clones.
3. **Local-only commits**: Commits reachable from no remote.

> [!IMPORTANT]
> **The Risk Gate Invariant**:
> If a clone holds commits on no remote, do not run automated branch/worktree deletion on it until the commits are pushed (`git push -u origin <branch>`) or confirmed abandoned. A branch whose PR is verified merged via `estate tidy --merged` (squash merges leave the original commits on no remote) is also cleared for deletion.

---

## 3. APFS Local Snapshots (macOS)

Time Machine or OS update snapshots can hold onto disk blocks even after large files are deleted:

```bash
# List local snapshots
tmutil listlocalsnapshots /
```

- If snapshots exist from previous OS updates (`com.apple.os.update-...`), they are managed by macOS.
- If user backup snapshots are locking gigabytes:
  ```bash
  sudo tmutil deletelocalsnapshots <snapshot_date>
  ```
