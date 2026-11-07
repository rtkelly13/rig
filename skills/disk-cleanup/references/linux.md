# Linux Disk Cleanup Runbook

Detailed procedure for diagnosing and reclaiming disk space on Linux distributions (Ubuntu, Debian, Fedora, Arch, Alpine).

---

## 1. Diagnostics & Measurement

Execute these commands in `bash`:

### Check Free Capacity
```bash
df -h / /var /home
```

### Scan Home Directory Breakdown
```bash
du -d 1 -h ~ 2>/dev/null | sort -h
```

### Find Top 20 Largest Files
```bash
find / -xdev -type f -size +100M -exec ls -lh {} + 2>/dev/null | sort -k 5 -hr | head -n 20
```

---

## 2. Tier 1: System Logs & Ephemeral Files (Zero Risk)

### Systemd Journal Logs
Systemd logs can grow uncontrollably over time:
```bash
# Check current log size
journalctl --disk-usage

# Retain only the last 500MB or last 7 days
sudo journalctl --vacuum-size=500M
# or:
sudo journalctl --vacuum-time=7d
```

### Temporary Folders
```bash
# Clean /tmp and /var/tmp files not accessed recently
sudo find /tmp -type f -atime +10 -delete
sudo find /var/tmp -type f -atime +30 -delete
```

---

## 3. Tier 2: System & Developer Package Caches

### System Package Managers
```bash
# Debian / Ubuntu
sudo apt-get autoremove --purge -y
sudo apt-get clean

# Fedora / RHEL
sudo dnf clean all

# Arch Linux
sudo pacman -Sc --noconfirm
# or keep only current versions with paccache:
paccache -rk1
```

### Flatpak & Snap Runtimes
```bash
# Remove unused Flatpak runtimes
flatpak uninstall --unused -y

# Remove disabled Snap revisions (run in bash)
set -eu
snap list --all | awk '/disabled/{print $1, $3}' |
    while read snapname revision; do
        snap remove "$snapname" --revision="$revision"
    done
```

### Developer Tooling Caches
```bash
# Python uv cache
uv cache clean

# .NET / NuGet packages
dotnet nuget locals all --clear

# Node / npm
npm cache clean --force

# Docker system prune
docker system prune -af --volumes
```

---

## 4. Tier 3: Repository Worktrees & Build Artifacts

### Stale Git Worktrees
```bash
git -C <repo-path> worktree list
git -C <repo-path> worktree remove --force <worktree-path>
git -C <repo-path> worktree prune
```

### Dependency Tree Purge
```bash
# Find and remove orphaned node_modules or build folders in unneeded branches
find ~/code -type d -name "node_modules" -prune -exec du -sh {} + 2>/dev/null | sort -h | tail -n 15
```
