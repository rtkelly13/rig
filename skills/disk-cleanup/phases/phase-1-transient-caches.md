# Phase 1: Transient Cache Relief (Emergency Breathing Room)

**Rule: Transient caches provide immediate emergency headroom, but re-accumulate through regular work.**

---

## 1. The Role of Package Caches

When a drive is critically full (>95%), developer package managers (`uv`, `pnpm`, `npm`, `nuget`) frequently hold 15–30 GB of downloaded wheels, packages, and tarballs.

Purging them gives instant breathing room to perform operations, run git, or install tools. However:
- Every byte deleted here is fetched again automatically when you next build or run projects.
- Use Phase 1 as an emergency measure or routine garbage collection, not as a replacement for investigating real file hogs.

---

## 2. Cross-Platform Cache Purge Commands

| Ecosystem | Safe Cache Cleanup Command | Typical Savings | Notes |
|:---|:---|:---|:---|
| **Python (`uv`)** | `uv cache clean` | 2 – 10 GB | Deletes downloaded wheels in `~/.cache/uv`. Rebuilt automatically on `uv sync`. |
| **.NET (NuGet)** | `~/.dotnet/dotnet nuget locals all --clear` | 2 – 8 GB | Clears http-cache, temp, and global-packages. Re-fetched on `dotnet build`. |
| **Node (`pnpm`)** | `pnpm store prune` | 2 – 10 GB | Removes unreferenced packages from the global content-addressable store. |
| **Node (`npm`)** | `npm cache clean --force` | 1 – 5 GB | Clears `~/.npm`. |
| **Node (`yarn`)** | `yarn cache clean` | 1 – 3 GB | Clears global yarn cache. |
| **Rust (`cargo`)** | `cargo cache --autoclean` | 2 – 8 GB | Removes unused git checkouts and crate sources. |
| **Homebrew (macOS)** | `brew cleanup --prune=all` | 1 – 5 GB | Removes old downloaded bottles and outdated cellar versions. |
| **Docker** | `docker builder prune -f` | 5 – 30 GB | Clears build cache only. Do **not** use `docker system prune`: it deletes stopped containers (and their writable-layer data) without prompting; inventory with `docker ps -a` and confirm before any container/image prune. |

---

## 3. Post-Cache Verification

After clearing transient caches, re-check free space:
```bash
df -h / /System/Volumes/Data
```
If disk pressure is still high (>85%), proceed immediately to **Phase 2 (Worktrees)** and **Phase 3 (Downloads/Cloud)**.
