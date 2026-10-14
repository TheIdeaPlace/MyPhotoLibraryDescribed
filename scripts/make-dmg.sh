#!/bin/zsh
# Makes the Mac disk image, laid out to open as a designed window: the app on the left, an arrow, and Applications on
# the right, over the background scripts/make-dmg-background.swift draws. Called by scripts/release-mac.sh, which signs
# and notarizes the result; it can be run alone to try a layout change without a release:
#
#   scripts/make-dmg.sh <the .app> <the .dmg to make> "<volume name>"
#
# The layout is Finder's own .DS_Store, written by asking Finder to arrange the window, so this needs Finder to accept
# Apple events from whatever runs it (System Settings > Privacy & Security > Automation: Terminal > Finder; macOS asks
# the first time). If Finder can't be asked, this stops rather than make a disk image that opens as a plain window.
set -euo pipefail
cd "$(dirname "$0")/.."
APP=${1:?the app}; OUTFILE=${2:?the disk image to make}; VOL=${3:?the volume name}
APPNAME=$(basename "$APP")
# Where the icons sit, in points from the window's top left. make-dmg-background.swift draws its arrow and card to match.
APP_X=160 APPS_X=480 ICON_Y=205
# The window: 640 × 400 points for the picture, plus the title bar, which Finder's bounds include (32 points on macOS
# 26 and later, measured; 28 before, which leaves a sliver below the picture on those).
LEFT=200 TOP=120 WIDTH=640 HEIGHT=$(( 400 + 32 ))

WORK=$(mktemp -d "${TMPDIR:-/tmp}/mpld-dmg.XXXXXX")
MNT=""
cleanup() {
  [ -n "$MNT" ] && [ -d "$MNT" ] && hdiutil detach "$MNT" -force -quiet 2>/dev/null || true
  rm -rf "$WORK"
}
trap cleanup EXIT
# zsh skips the EXIT trap when a signal ends the script, so Control-C or closing the window exits through it.
trap 'exit 130' INT TERM HUP

# The folder that becomes the disk: the app, a link to Applications, the background, and the volume's icon.
STAGE="$WORK/stage"
mkdir -p "$STAGE/.background"
ditto "$APP" "$STAGE/$APPNAME"
ln -s /Applications "$STAGE/Applications"
swift scripts/make-dmg-background.swift "$WORK/bg" "${APPNAME%.app}"
tiffutil -cathidpicheck "$WORK/bg/background.png" "$WORK/bg/background@2x.png" -out "$STAGE/.background/background.tiff" 2>/dev/null
mkdir "$WORK/Volume.iconset"
cp PhotoFinder/Assets.xcassets/AppIcon.appiconset/icon_*.png "$WORK/Volume.iconset/"
iconutil -c icns "$WORK/Volume.iconset" -o "$WORK/VolumeIcon.icns"

# A disk with the same name already mounted (a run that stopped partway, or an earlier image left open) would make
# this one mount as "<name> 1", where Finder wouldn't find it by name.
for m in /Volumes/"$VOL" /Volumes/"$VOL "[0-9]*(N); do
  [ -d "$m" ] || continue
  echo "Ejecting $m, left mounted from before"
  hdiutil detach "$m" -force -quiet
done

# Read-write first, with room for Finder's .DS_Store, then mounted where Finder can see it.
SIZE=$(( $(du -sm "$STAGE" | cut -f1) + 20 ))
hdiutil create -srcfolder "$STAGE" -volname "$VOL" -fs HFS+ -format UDRW -size "${SIZE}m" "$WORK/rw.dmg" -quiet
hdiutil attach "$WORK/rw.dmg" -readwrite -noverify -noautoopen -mountpoint "/Volumes/$VOL" -quiet
MNT="/Volumes/$VOL"

if ! osascript - "$VOL" "$APPNAME" $APP_X $APPS_X $ICON_Y $LEFT $TOP $WIDTH $HEIGHT <<'APPLESCRIPT'
on run argv
  set {vol, appName, appX, appsX, iconY, l, t, w, h} to argv
  tell application "Finder"
    tell disk vol
      open
      delay 1
      tell container window
        set current view to icon view
        set toolbar visible to false
        set statusbar visible to false
        set pathbar visible to false
        set bounds to {l as integer, t as integer, (l + w) as integer, (t + h) as integer}
      end tell
      set opts to icon view options of container window
      set arrangement of opts to not arranged
      set icon size of opts to 128
      set text size of opts to 13
      set shows item info of opts to false
      set shows icon preview of opts to false
      set label position of opts to bottom
      set background picture of opts to file ".background:background.tiff"
      set position of item appName of container window to {appX as integer, iconY as integer}
      set position of item "Applications" of container window to {appsX as integer, iconY as integer}
      update without registering applications
      delay 1
      close
      -- Opened and closed once more, so Finder writes the .DS_Store with everything above.
      open
      delay 1
      close
    end tell
  end tell
end run
APPLESCRIPT
then
  echo "Finder couldn't lay out the disk image's window, so no disk image was made: one that opens as a plain window"
  echo "isn't fit to ship. Allow the app running this to control Finder (System Settings > Privacy & Security >"
  echo "Automation), then run it again."
  exit 1
fi

# The disk's icon is the app's. Copied in after Finder's done: on macOS 27, Finder deleted a .VolumeIcon.icns that was
# there while it laid out the window.
cp "$WORK/VolumeIcon.icns" "$MNT/.VolumeIcon.icns"
if command -v SetFile >/dev/null && SetFile -c icnC "$MNT/.VolumeIcon.icns" 2>/dev/null && SetFile -a C "$MNT" 2>/dev/null; then :; else
  echo "Warning: SetFile (from Xcode's command line tools) isn't available, so the disk keeps the standard icon."
fi

# Finder writes .DS_Store on its own schedule: wait for it, rather than ship a disk without the layout.
for i in {1..20}; do [ -f "$MNT/.DS_Store" ] && break; sleep 0.5; done
[ -f "$MNT/.DS_Store" ] || { echo "Finder didn't save the window's layout (.DS_Store), so no disk image was made."; exit 1; }
# Opens the window when the disk mounts. Not every macOS allows it; the layout doesn't depend on it.
bless --folder "$MNT" --openfolder "$MNT" 2>/dev/null || true
rm -rf "$MNT/.fseventsd" "$MNT/.Trashes"
chmod -Rf go-w "$MNT" 2>/dev/null || true
sync
for i in {1..10}; do hdiutil detach "$MNT" -quiet 2>/dev/null && break; sleep 1; done
[ -d "$MNT" ] && hdiutil detach "$MNT" -force -quiet
MNT=""

rm -f "$OUTFILE"
hdiutil convert "$WORK/rw.dmg" -format UDZO -imagekey zlib-level=9 -o "$OUTFILE" -quiet
