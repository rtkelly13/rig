# Phase 4: Application Footprint & Dormancy Audit

**Rule: Audit desktop application age and purge companion audio sample banks when removing media apps.**

---

## 1. Application Profiling

Installed applications in `/Applications` and `~/Applications` frequently consume 20–50 GB, with many apps untouched for months or years:

```bash
# Run CLI application audit
rig estate apps --min 100MB
```

This reports:
- Name and path
- Size (via `du -sk`)
- Version (via `Contents/Info.plist`)
- Last modified and last accessed dates
- **Status**: Flagged as `dormant` if untouched for >180 days.

---

## 2. Visual Disk Profiling

For interactive inspection:
- **DaisyDisk**: If installed, run `open -a DaisyDisk` to inspect visual sunburst charts of large files and drag candidates to a deletion tray.
- **macOS Storage Settings**:
  ```bash
  open "x-apple.systempreferences:com.apple.preferences.storage"
  ```
  Click **Applications** to view apps sorted by size with exact **Last Accessed** dates.

---

## 3. High-Capacity Targets & Companion Sample Banks

### A. Default macOS Media Apps
- `iMovie.app` (~2.7 GB): Often untouched default app. Can be re-downloaded from the Mac App Store at any time.
- `GarageBand.app` (~920 MB): Untouched default music creator.

### B. Companion Audio Libraries (The 2.5 GB Sample Bank)
Deleting GarageBand alone leaves its heavy sample libraries behind in `/Library`:
- `/Library/Application Support/GarageBand` (~900 MB: Instrument Library, Learn to Play)
- `/Library/Application Support/Logic` (~900 MB: EXS Factory Samples, Alchemy Samples, Ultrabeat Samples)
- `/Library/Audio/Apple Loops` (~500 MB)

### C. Secondary & Disused IDEs
- `PyCharm.app` (~3.2 GB)
- `RStudio.app` (~1.3 GB) + user support in `~/Library/Application Support/RStudio` & `~/.local/share/rstudio`
- `Orange.app` (~1.3 GB)
- iWork suite if unused (`Keynote`, `Pages`, `Numbers` ~1.3 GB)

---

## 4. Safe Removal Mechanism

### User-Owned Apps
Can be removed directly:
```python
import shutil
shutil.rmtree('/Applications/<App>.app')
```

### System / Root-Owned Apps
Root-owned apps in `/Applications` require admin permissions. The cleanest macOS approach is moving them to Trash via Finder AppleScript, then emptying Trash:
```bash
osascript -e 'tell application "Finder" to delete POSIX file "/Applications/iMovie.app"'
osascript -e 'tell application "Finder" to delete POSIX file "/Applications/GarageBand.app"'
osascript -e 'tell application "Finder" to delete POSIX file "/Library/Application Support/GarageBand"'

# Empty Trash to reclaim space
osascript -e 'tell application "Finder" to empty trash'
```

> [!WARNING]
> **Shared audio libraries are not GarageBand companions.** `/Library/Application Support/Logic` and `/Library/Audio/Apple Loops` are also used by Logic Pro, MainStage and third-party DAWs. Do not delete them as part of a GarageBand cleanup. Check that no other audio app depends on them, then ask the user to confirm each library separately before moving it to Trash.
