# OBS talk recording

Local OBS setup for recording live talks — screen capture + webcam picture-in-picture,
mixed into one file. Versioned config lives in [`config/obs/`](../config/obs).

## The model

In the room, the [blog](https://github.com/rtkelly13/blog) live-talk system is the source
of truth: attendees follow the Convex-synced deck and the projector. OBS is the **archival
layer** — it captures the whole projected surface, including **live demos** (terminal,
editor, browser), which the slide-sync model structurally can't. The recording is for
after-the-fact viewers.

Deliberately **record-only, no streaming**: this runs on an 8 GB M1 MacBook Air with no
second machine, so keeping RTMP/upload off the critical path means network activity can't
stall a demo. Upload the finished file to YouTube afterwards, then point the talk at it.

## Install

```bash
bash config/obs/install.sh                 # records to ~/Movies/Talks
bash config/obs/install.sh --record-path /Volumes/SSD/Talks
```

Installs OBS via Homebrew if missing, backs up any existing OBS profiles/scenes to
`~/Library/Application Support/obs-studio/backups/<timestamp>/`, then installs the
`TalkRecording` profile + scene collection and makes them active. **Quit OBS first** — it
overwrites its config on quit.

## First launch (one-time)

1. Open OBS. Grant **Screen Recording**, **Camera**, and **Microphone** when macOS prompts
   (or System Settings → Privacy & Security). Screen Recording requires an OBS relaunch.
2. In the **Talk** scene:
   - Double-click **Screen** → select the display you'll project from.
   - Double-click **Camera** → select your webcam.
   - The webcam sits bottom-right as a 480×270 PiP; drag it above **Screen** if it's hidden.
3. Confirm **Settings → Output** (Simple mode): Video Encoder = **Apple VT H264 Hardware**,
   Recording Format = **MKV**, Recording Quality = **High Quality, Medium File Size**.

These device picks and the granted permissions are per-machine, so they aren't versioned.

## Recording a talk

1. **Start Recording** (not "Start Streaming"). Files land in the record path, named
   `YYYY-MM-DD HH-MM-SS talk.mkv`.
2. After the talk: **File → Remux Recordings** to convert the MKV → MP4. MKV is used while
   recording so an OBS crash still leaves a playable file; MP4 is what uploads cleanly.
3. Upload the MP4 to YouTube.
4. In the blog, set `videoUrl` in the talk's front-matter (`data/talks/<slug>.mdx`) to the
   YouTube link — it renders as an inline player on the talk page automatically
   (blog PR #64).

## Machine limits (M1 Air, 8 GB)

- **1080p30, hardware encoder.** Never switch to x264 (software) or 1080p60/4K — a fanless
  Air throttles and swaps under sustained software encode.
- **RAM is the constraint**, not CPU: OBS (~0.5–1 GB) plus the demo on 8 GB. Keep the demo
  light (a CLI, a single browser tab), not the whole dev stack.
- Do a dry run of the exact demo with OBS recording before the real thing.
- ~4–5 GB/hour at the default quality; watch free disk on long talks.

## Updating the versioned config

The profile is copied on install (it carries a machine-specific record path). To capture
scene tweaks made in OBS back into the repo, either copy the file back:

```bash
cp "$HOME/Library/Application Support/obs-studio/basic/scenes/TalkRecording.json" \
   config/obs/scenes/TalkRecording.json
```

or install with `--link` so OBS edits the repo copy directly (expect churn — OBS rewrites
volatile UI state on every change). Commit intentional changes via PR.
