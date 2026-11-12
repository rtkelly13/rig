# Windows Disk Cleanup Runbook

Detailed procedure for diagnosing and reclaiming disk space on Microsoft Windows (Windows 10, Windows 11, and Windows Server).

---

## 1. Diagnostics & Measurement

Execute these commands in PowerShell (Admin recommended for system areas):

### Check Drives and Free Capacity
```powershell
Get-PSDrive -PSProvider FileSystem | Select-Object Name, Root,
    @{N="Used (GB)";E={[math]::round($_.Used/1GB, 2)}},
    @{N="Free (GB)";E={[math]::round($_.Free/1GB, 2)}},
    @{N="Capacity (GB)";E={[math]::round(($_.Used + $_.Free)/1GB, 2)}},
    @{N="% Free";E={[math]::round(($_.Free / ($_.Used + $_.Free)) * 100, 1)}}
```

### Identify Top Folders in User Profile
```powershell
$UserRoot = $env:USERPROFILE
Get-ChildItem -Path $UserRoot -Directory -Force -ErrorAction SilentlyContinue |
    ForEach-Object {
        $Folder = $_
        $Size = (Get-ChildItem -Path $Folder.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
                 Measure-Object -Property Length -Sum).Sum
        [PSCustomObject]@{
            Folder = $Folder.Name
            SizeGB = [math]::Round($Size / 1GB, 2)
        }
    } | Sort-Object SizeGB -Descending | Select-Object -First 15
```

### Find Top 20 Largest Files
```powershell
Get-ChildItem -Path $env:USERPROFILE -File -Recurse -Force -ErrorAction SilentlyContinue |
    Sort-Object Length -Descending |
    Select-Object -First 20 FullName, @{N="SizeGB";E={[math]::round($_.Length / 1GB, 2)}}
```

---

## 2. Tier 1: Windows Temporary & Ephemeral Files (Zero Risk)

### User & System Temp
```powershell
# Remove user temp files older than 24 hours
Get-ChildItem -Path $env:TEMP -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-1) } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# System temp (requires Admin PowerShell)
Get-ChildItem -Path "C:\Windows\Temp" -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-1) } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
```

### Windows Update SoftwareDistribution Cache
Windows saves completed update installers in `C:\Windows\SoftwareDistribution\Download`.
```powershell
# In Admin PowerShell:
Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
Remove-Item -Path "C:\Windows\SoftwareDistribution\Download\*" -Recurse -Force -ErrorAction SilentlyContinue
Start-Service -Name wuauserv
```

### Component Store (WinSxS) Cleanup
Reclaim multiple gigabytes of superseded Windows updates safely:
```powershell
# In Admin PowerShell:
Dism.exe /online /Cleanup-Image /StartComponentCleanup
```

### Built-in Cleanmgr
```powershell
cleanmgr.exe /lowdisk
```

---

## 3. Tier 2: Windows Package Managers & Caches

### Winget, Scoop, Chocolatey
```powershell
# Scoop: remove old package versions and download cache
if (Get-Command scoop -ErrorAction SilentlyContinue) {
    scoop cleanup *
    scoop cache rm *
}

# Chocolatey: clean download cache
if (Get-Command choco -ErrorAction SilentlyContinue) {
    choco clean
}
```

### .NET & NuGet Caches
Visual Studio and .NET projects frequently amass gigabytes of package downloads:
```powershell
dotnet nuget locals all --clear
```
Also check Visual Studio symbols and project caches:
```powershell
Remove-Item -Path "$env:LOCALAPPDATA\Microsoft\VisualStudio\*\ComponentModelCache" -Recurse -Force -ErrorAction SilentlyContinue
```

### Python, Node, and Rust
```powershell
# uv cache
uv cache clean

# npm cache
npm cache clean --force

# pnpm store prune
pnpm store prune

# Cargo / Rust cache
if (Test-Path "$env:USERPROFILE\.cargo\registry\cache") {
    Remove-Item -Path "$env:USERPROFILE\.cargo\registry\cache\*" -Recurse -Force -ErrorAction SilentlyContinue
}
```

---

## 4. Tier 3: Developer Worktrees & Build Artifacts

### Stale Git Worktrees
```powershell
# Check worktrees
git -C <repo-path> worktree list

# Remove and prune
git -C <repo-path> worktree remove --force <worktree-path>
git -C <repo-path> worktree prune
```

- Prefer `rig estate tidy --merged --repo <clone>` and `rig estate shelve` over hand removal: they check that origin holds the work first (see Phase 2).
- After `git worktree remove`, check the directory is really gone. pnpm junctions survive it as an empty link skeleton.
- Sizes from a recursive walk overstate what deleting a `.venv` frees: uv hardlinks into its cache. `estate` counts a hardlinked file only when every link is inside the tree. For volume-wide questions use `rig estate volume` (WizTree, MFT, allocated size).

### Scan & Purge `bin` / `obj` / `node_modules`
In project root:
```powershell
# Remove all bin and obj folders recursively
Get-ChildItem -Path . -Include bin,obj -Directory -Recurse |
    Remove-Item -Recurse -Force

# Remove node_modules
Get-ChildItem -Path . -Include node_modules -Directory -Recurse |
    Remove-Item -Recurse -Force
```

---

## 5. Tier 4: WSL2 & Docker VHDX Compaction (Huge Space Winner)

WSL2 and Docker Desktop use virtual hard disks (`ext4.vhdx`) that automatically expand on Windows but **never shrink automatically** after deleting files inside Linux or Docker!

### Shrink WSL2 / Docker `ext4.vhdx`
1. Shutdown WSL completely:
   ```powershell
   wsl --shutdown
   ```
2. Locate the `.vhdx` file (typical paths):
   - Docker Desktop: `%LOCALAPPDATA%\Docker\wsl\data\ext4.vhdx`
   - Default distro: `%LOCALAPPDATA%\Packages\CanonicalGroupLimited...\\LocalState\ext4.vhdx`
3. Compact using `diskpart`:
   ```powershell
   # Create a temporary script for diskpart
   $vhdxPath = "$env:LOCALAPPDATA\Docker\wsl\data\ext4.vhdx"
   @"
   select vdisk file="$vhdxPath"
   attach vdisk readonly
   compact vdisk
   detach vdisk
   "@ | Set-Content "$env:TEMP\compact.txt"

   diskpart /s "$env:TEMP\compact.txt"
   Remove-Item "$env:TEMP\compact.txt"
   ```
   *Note: In WSL 2.0.0+, you can also run `wsl --manage <distro> --set-sparse true` or `wsl --compact <distro>`.*

### Clear Windows Recycle Bin
```powershell
Clear-RecycleBin -Force -ErrorAction SilentlyContinue
```
