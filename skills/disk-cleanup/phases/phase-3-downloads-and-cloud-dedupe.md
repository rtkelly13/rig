# Phase 3: Downloads & Cloud Storage Deduplication

**Rule: Google Drive is strictly read-only and a backup target. Never delete cloud files during local cleanup.**

---

## 1. Downloads Triage Workflow

Downloads folders naturally accumulate gigabytes of forgotten installers, large CSV/Parquet data dumps, and camera photo backups:

```bash
rig estate downloads --min 20MB
```

This classifies each item into:
- `dataset`: `.csv`, `.parquet`, `.sqlite`, `.json`
- `installer`: `.dmg`, `.pkg`, `.iso`
- `media`: `.jpg`, `.png`, `.mp4`, `.mov`
- `document`: `.pdf`, `.docx`, `.xlsx`
- `archive`: `.zip`, `.tar.gz`, `.tgz`

---

## 2. Cloud Deduplication Protocol

When personal photos or media are found in `~/Downloads`:

### Step 1: Check Google Drive First
The `estate downloads` command cross-references items against `~/Google Drive/My Drive/Photos` and the Drive root.

### Step 2: Invariant — Cloud Is Read-Only / Backup Target
- **NEVER** run delete commands against paths inside `~/Google Drive`.
- Google Drive is the authoritative cloud archive.

### Step 3: Handle Unique Media
If photos/videos exist in Downloads but NOT in Google Drive:
1. Create an appropriate folder in Google Drive:
   ```bash
   mkdir -p ~/Google\ Drive/My\ Drive/Photos/<EventName>/
   ```
2. Copy files to Google Drive:
   ```bash
   cp -p <source-files> ~/Google\ Drive/My\ Drive/Photos/<EventName>/
   ```
3. Verify that file counts and byte sizes match 100%.

### Step 4: Purge Local Duplicate Downloads
Once verified byte-for-byte against the cloud backup:
- Safely remove the local duplicate folders or loose files from `~/Downloads`.
