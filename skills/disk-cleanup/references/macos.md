# macOS Disk Cleanup Runbook

Detailed procedure for diagnosing and reclaiming disk space on macOS (Apple Silicon & Intel).

---

## 1. Diagnostics & Measurement

Execute these commands in `zsh`:

### Check Free Capacity
```zsh
df -h / /System/Volumes/Data
```

### Check Local APFS Time Machine Snapshots
Snapshots can hold onto tens of gigabytes of deleted files:
```zsh
tmutil listlocalsnapshots /
```

### Scan Home Directory Breakdown
```zsh
du -d 1 -h ~ 2>/dev/null | sort -h
```

### Check Mounted Volumes (Leftover Installer DMGs)
Installer disks often get left mounted in the background:
```zsh
ls -la /Volumes
hdiutil info | grep -E "(image-path|mount-point)"
```

---

## 2. Tier 1: Ephemeral Junk & Installer Remnants (Zero Risk)

### Detach Orphaned Disk Images in `/Volumes`
```zsh
# List and detach any unneeded mounted DMGs
for vol in /Volumes/*; do
  # Ignore main boot drive symlink
  if [ -d "$vol" ] && [ ! -L "$vol" ] && [ "$vol" != "/Volumes/Macintosh HD" ]; then
    echo "Detaching $vol..."
    hdiutil detach "$vol" -force
  fi
done
```

### macOS System Temp Folders (`/private/var/folders`)
macOS assigns per-user temp folders under `/private/var/folders/<xx>/<yyyyyyy>/`.
These often accumulate abandoned multi-gigabyte installer DMGs and clones:
```zsh
# Check size of T (temp) and X directories
du -sh /private/var/folders/*/*/{T,X} 2>/dev/null

# Clean installer temporary downloads (e.g. stale CLI/app updater DMGs)
rm -rf /private/var/folders/*/*/T/*download*
rm -rf /private/var/folders/*/*/T/*.dmg

# Remove stale code-sign clones (e.g. Chrome/Electron update clones)
rm -rf /private/var/folders/*/*/X/com.google.Chrome.code_sign_clone
```

### Application Update Caches (`ShipIt`)
Many Electron and native apps (VS Code, Slack, GitHub Desktop) use the ShipIt framework, which stores downloaded update archives in `~/Library/Caches`:
```zsh
rm -rf ~/Library/Caches/com.microsoft.VSCode.ShipIt
rm -rf ~/Library/Caches/com.tinyspeck.slackmacgap.ShipIt
rm -rf ~/Library/Caches/com.github.GitHubClient.ShipIt
```

---

## 3. Tier 2: Developer Tooling Caches

```zsh
# Python uv cache
uv cache clean

# .NET / NuGet packages and HTTP cache
~/.dotnet/dotnet nuget locals all --clear  # or: dotnet nuget locals all --clear

# Node / npm
npm cache clean --force

# pnpm
pnpm store prune

# Yarn
yarn cache clean

# Homebrew (clears downloaded bottles and old versions)
brew cleanup --prune=all
rm -rf "$(brew --cache)"
```

---

## 4. Tier 3: Repository Worktrees & Build Artifacts

### Stale Git Worktrees
```zsh
# Inspect worktrees for a repo
git -C <repo-path> worktree list

# Check if worktree is clean
git -C <worktree-path> status -s

# Remove worktree safely
git -C <repo-path> worktree remove --force <worktree-path>
git -C <repo-path> worktree prune
```

### Heavy Build & Virtual Environment Scans
```zsh
# Find large node_modules directories across codebases
find ~/code -type d -name "node_modules" -prune -exec du -sh {} + 2>/dev/null | sort -h | tail -n 20

# Find Python virtual environments
find ~/code -type d -name ".venv" -prune -exec du -sh {} + 2>/dev/null | sort -h | tail -n 20

# Find .NET bin/obj folders
find ~/code -type d \( -name "bin" -o -name "obj" \) -prune -exec du -sh {} + 2>/dev/null | sort -h | tail -n 20
```

---

## 5. Tier 4: Large App Assets & User Data (Requires Confirmation)

### Claude Desktop Local Agent VM
Claude Desktop stores a full virtual machine image if local agent mode was enabled:
- Location: `~/Library/Application Support/Claude/vm_bundles/claudevm.bundle/rootfs.img`
- Typical Size: **10 GB**
- Action: If not using Claude local agent mode, delete `rootfs.img` or the `vm_bundles` directory.

### Docker / OrbStack VM Disk Images
- Docker Desktop: `~/Library/Containers/com.docker.docker/Data/vms/0/Docker.raw`
- Action: Run `docker system prune -a --volumes` inside Docker. If still bloated, resize the disk image in Docker Desktop settings.

### Playwright Browser Binaries
Playwright caches full headless builds of Chromium, WebKit, and Firefox:
- Location: `~/Library/Caches/ms-playwright`
- Typical Size: **1 – 4 GB**
- Action: `rm -rf ~/Library/Caches/ms-playwright` (re-downloaded automatically on `npx playwright install`).

### Downloads Folder Triage & Cloud Duplicates
Search for large archives and data delivery dumps in `~/Downloads`, and verify whether media/documents already exist in cloud storage (e.g. Google Drive) before removing:
```zsh
# Using estate CLI:
rig estate downloads --min 20MB

# Or manual inspection:
ls -lhS ~/Downloads | head -n 20
```
- Compare with Google Drive (`~/Google Drive/My Drive/Photos` or `~/Google Drive/My Drive`) to avoid deleting unbacked-up family photos while safely pruning exact duplicate downloads.

### Application Usage & Dormant App Audit
Audit `/Applications` and `~/Applications` to identify bloated, outdated, or dormant applications:
```zsh
# Using estate CLI:
rig estate apps --min 100MB

# Native UI tools:
open -a DaisyDisk                                             # Visual sunburst disk map
open "x-apple.systempreferences:com.apple.preferences.storage" # macOS native Storage manager
```
- **High-capacity targets**:
  - `iMovie.app` (~2.7 GB) — frequently untouched default app.
  - `GarageBand.app` (~920 MB) plus instrument packs in `/Library/Application Support/GarageBand` (~900 MB), `/Library/Application Support/Logic` (~900 MB), and `/Library/Audio` (~500 MB).
  - Unused or secondary IDEs (e.g. `PyCharm.app` ~3.2 GB, `RStudio.app` ~1.3 GB, `Orange.app` ~1.3 GB).
  - iWork suite if unused (`Keynote`, `Pages`, `Numbers` ~1.3 GB).

### Pitfall: External Worktrees at the Estate Root
Never check out worktrees at the estate virtual monorepo root (e.g. `personal/temp/<name>`). When repos contain large binary files or archives (like an Obsidian vault or video assets), a stray worktree duplicates the entire 10–15 GB footprint outside standard per-repo scans.
- Run `estate audit` / `estate reclaim` which scans both in-repo worktrees and external worktrees registered via `git worktree list`.
- Remove using `git -C <repo> worktree remove <path>`.

### Delete APFS Local Snapshots
If snapshots are locking disk space:
```zsh
# Delete a specific snapshot
sudo tmutil deletelocalsnapshots <snapshot_date>
```
