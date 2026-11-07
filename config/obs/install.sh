#!/usr/bin/env bash
#
# Install the versioned "TalkRecording" OBS profile + scene collection onto this
# machine. Idempotent: safe to re-run; every run backs up the existing OBS
# profiles/ and scenes/ first.
#
#   bash config/obs/install.sh [--record-path DIR] [--link] [--force]
#
#   --record-path DIR  Where recordings are written (default: ~/Movies/Talks).
#   --link             Symlink the scene collection back to this repo so OBS
#                      edits flow into version control (the profile is always
#                      copied — it carries a per-machine record path).
#   --force            Overwrite an existing TalkRecording profile/scene without
#                      the "already installed" prompt.
#
# See docs/obs-recording.md for the first-launch steps (permissions + picking
# your camera/display) and the recording workflow.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OBS_DIR="$HOME/Library/Application Support/obs-studio"
RECORD_PATH="$HOME/Movies/Talks"
LINK_SCENE=false
FORCE=false

while [ $# -gt 0 ]; do
  case "$1" in
    --record-path) RECORD_PATH="$2"; shift 2 ;;
    --link) LINK_SCENE=true; shift ;;
    --force) FORCE=true; shift ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

log() { printf '  %s\n' "$*"; }

# 1. OBS must be installed and NOT running (it rewrites its config on quit,
#    which would clobber whatever we install here).
if [ ! -d /Applications/OBS.app ]; then
  log "OBS not found — installing via Homebrew…"
  brew install --cask obs
fi
if pgrep -x OBS >/dev/null 2>&1; then
  echo "OBS is running. Quit it first (it overwrites its config on quit), then re-run." >&2
  exit 1
fi

# 2. Back up existing profiles + scenes so nothing is lost.
mkdir -p "$OBS_DIR/basic/profiles" "$OBS_DIR/basic/scenes"
BACKUP="$OBS_DIR/backups/$(date +%Y%m%d-%H%M%S)"
if [ -d "$OBS_DIR/basic/profiles/TalkRecording" ] || [ -e "$OBS_DIR/basic/scenes/TalkRecording.json" ]; then
  if [ "$FORCE" = false ]; then
    log "TalkRecording is already installed; re-running will back it up and replace it."
  fi
fi
mkdir -p "$BACKUP"
cp -R "$OBS_DIR/basic/profiles" "$BACKUP/profiles" 2>/dev/null || true
cp -R "$OBS_DIR/basic/scenes" "$BACKUP/scenes" 2>/dev/null || true
log "backed up existing config → ${BACKUP/#$HOME/~}"

# 3. Install the profile (always copied — the record path is machine-specific).
mkdir -p "$RECORD_PATH"
DEST_PROFILE="$OBS_DIR/basic/profiles/TalkRecording"
rm -rf "$DEST_PROFILE"
mkdir -p "$DEST_PROFILE"
sed "s|__RECORD_PATH__|$RECORD_PATH|g" \
  "$SCRIPT_DIR/profiles/TalkRecording/basic.ini" > "$DEST_PROFILE/basic.ini"
log "installed profile → basic/profiles/TalkRecording (records to ${RECORD_PATH/#$HOME/~})"

# 4. Install the scene collection (copy, or symlink with --link).
DEST_SCENE="$OBS_DIR/basic/scenes/TalkRecording.json"
rm -f "$DEST_SCENE"
if [ "$LINK_SCENE" = true ]; then
  ln -s "$SCRIPT_DIR/scenes/TalkRecording.json" "$DEST_SCENE"
  log "linked scene collection → basic/scenes/TalkRecording.json (OBS edits flow back to the repo)"
else
  cp "$SCRIPT_DIR/scenes/TalkRecording.json" "$DEST_SCENE"
  log "installed scene collection → basic/scenes/TalkRecording.json"
fi

# 5. Point OBS at the profile + scene collection. OBS 31+ keeps the active
#    selection in user.ini (not global.ini) under [Basic]; we upsert there,
#    preserving all the UI state that file also holds. interpolation=None so
#    values containing '%' (none here, but future-proof) aren't mangled.
python3 - "$OBS_DIR/user.ini" <<'PY'
import configparser, sys
path = sys.argv[1]
cfg = configparser.ConfigParser(interpolation=None)
cfg.optionxform = str  # OBS keys are CamelCase — don't lowercase them
cfg.read(path)
if not cfg.has_section("Basic"):
    cfg.add_section("Basic")
for k, v in {
    "Profile": "TalkRecording",
    "ProfileDir": "TalkRecording",
    "SceneCollection": "TalkRecording",
    "SceneCollectionFile": "TalkRecording.json",
}.items():
    cfg.set("Basic", k, v)
with open(path, "w") as fh:
    cfg.write(fh, space_around_delimiters=False)
PY
log "set TalkRecording as the active profile + scene collection"

cat <<EOF

Done. Next (see docs/obs-recording.md):
  1. Open OBS. If prompted, grant Screen Recording, Camera, Microphone.
  2. In the Talk scene, double-click Screen → pick your display (the projector
     output during a talk); double-click Camera → pick your webcam.
  3. Verify Settings > Output: encoder "Apple VT H264 Hardware", format MKV.
  4. Hit "Start Recording". Files land in ${RECORD_PATH/#$HOME/~}.
EOF
