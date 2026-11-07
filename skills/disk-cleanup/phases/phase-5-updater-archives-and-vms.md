# Phase 5: App Updater Archives, Temp Scratch, & VM Images

**Rule: Purge accumulated app updater archives and temporary build scratch. Virtual machine disks require explicit user confirmation.**

---

## 1. Auto-Updater Archives (ShipIt & CRX Caches)

Modern desktop applications (Electron, Chromium, Slack, Claude, Warp) download full application archives during background updates and often fail to purge them:

```bash
# Claude Desktop update downloads:
rm -rf ~/Library/Caches/com.anthropic.claudefordesktop.ShipIt

# Slack update downloads:
rm -rf ~/Library/Caches/com.tinyspeck.slackmacgap.ShipIt

# Warp Terminal auto-update archives:
rm -rf ~/Library/Application\ Support/dev.warp.Warp-Stable/autoupdate

# Chrome extension installer cache:
rm -rf ~/Library/Application\ Support/Google/GoogleUpdater/crx_cache
```
*Typical Savings: 2 – 5 GB*. Safe to purge at any time.

---

## 2. Operating System Temporary Scratch

Agent runs, compilers, and test suites often write multi-gigabyte files to `/private/tmp`:
```bash
# Check size of /private/tmp:
du -d 1 -h /private/tmp | sort -rh | head -10

# Safe targets:
# Clean old agent worktrees (/private/tmp/claude-*, /private/tmp/design-system-*)
```

---

## 3. Virtual Machine Disk Images (Requires User Confirmation)

Desktop AI agents and container runtimes allocate static or sparse virtual disk images that grow to 10–30 GB:

### A. Claude Desktop Local VM Bundle
- **Location**: `~/Library/Application Support/Claude/vm_bundles/claudevm.bundle/rootfs.img`
- **Size**: **10.0 GB**
- **Action**: If you do not actively use local desktop agent VM mode in Claude Desktop, delete `rootfs.img` or the `vm_bundles` folder.

### B. Docker Desktop VM Raw Disk
- **Location**: `~/Library/Containers/com.docker.docker/Data/vms/0/Docker.raw`
- **Action**: Run `docker system prune -a --volumes`. If the `.raw` file remains bloated, adjust the disk limit in Docker Desktop Settings → Resources.

### C. Playwright Browser Binaries
- **Location**: `~/Library/Caches/ms-playwright`
- **Size**: 1 – 4 GB
- **Action**: Safe to delete; automatically re-downloaded on `npx playwright install`.
