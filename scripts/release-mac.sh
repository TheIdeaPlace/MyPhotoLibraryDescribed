#!/bin/zsh
# Builds a signed, notarized Mac release of the app and attaches it to the GitHub release (#22).
#
#   scripts/release-mac.sh            build, sign, notarize and staple; leaves the disk image in build/release
#   scripts/release-mac.sh --publish  and then attaches it to the draft release v<version>
#
# One tag releases both apps (Kelly, 13 Oct): pushing v<version> runs .github/workflows/release.yml, which builds and
# signs the Windows installer and makes the draft release. This attaches the Mac's disk image to that draft, built from
# the tagged commit, and only once the Windows files are on it: a published release without them would break every
# Windows install and update, which read the latest release. The draft is published when Kelly says go.
#
# Built here rather than in GitHub Actions: the Apple Intelligence code needs the macOS 27 SDK, and GitHub's runners
# don't have Xcode 27 yet, so a release built there couldn't describe photos. It uses what's already on Kelly's Mac:
# the Developer ID Application certificate in the login keychain, and the App Store Connect API key in the signing
# kit (~/.signing-kit, see The-Idea-Place-Projects/signing). The version is MARKETING_VERSION in project.yml.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH=/opt/homebrew/bin:$PATH

KIT=${SIGNING_KIT:-$HOME/.signing-kit}
KEY_ID=38ANZ53D9L
ISSUER=d36bb41d-a198-401d-b36c-2e4fd15163e1
TEAM=P887QF74N8
IDENTITY="Developer ID Application: Kelly Ford ($TEAM)"
PUBLISH=${1:-}

VERSION=$(sed -n 's/^ *MARKETING_VERSION: *"\{0,1\}\([0-9.]*\)"\{0,1\}/\1/p' project.yml | head -1)
NAME=$(sed -n 's/^ *CFBundleDisplayName: *//p' project.yml | head -1)
TAG="v$VERSION"
OUT=build/release
# Everything made on the way (the archive, the export, the zip sent to Apple, the disk image's staging folder) goes
# in a working folder that's removed at the end, so build/release holds only the disk image.
WORK=$(mktemp -d "${TMPDIR:-/tmp}/mpld-release.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
FILE="${NAME// /-}-$VERSION-mac.dmg"
[ -n "$VERSION" ] && [ -n "$NAME" ] || { echo "Couldn't read the version or name from project.yml"; exit 1; }
[ -f "$KIT/asc-api-key.p8" ] || { echo "No App Store Connect key at $KIT/asc-api-key.p8"; exit 1; }
IDENTITIES=$(security find-identity -v -p codesigning)
[[ "$IDENTITIES" == *"$IDENTITY"* ]] || { echo "No '$IDENTITY' in the keychain"; exit 1; }
if [ "$PUBLISH" = "--publish" ]; then
  # Checked before ten minutes of notarizing, not after.
  gh auth status >/dev/null 2>&1 || { echo "Sign in to GitHub first: gh auth login"; exit 1; }
  git fetch -q origin "refs/tags/$TAG:refs/tags/$TAG" 2>/dev/null || true
  git rev-parse -q --verify "refs/tags/$TAG" >/dev/null || { echo "Push the tag $TAG first: it builds the Windows installer and makes the draft."; exit 1; }
  [ "$(git rev-parse "$TAG^{commit}")" = "$(git rev-parse HEAD)" ] || { echo "Check out $TAG first, so the Mac and Windows builds come from the same commit."; exit 1; }
  gh release view "$TAG" >/dev/null 2>&1 || { echo "There's no draft $TAG yet. Wait for the Release workflow to finish."; exit 1; }
  [ "$(gh release view "$TAG" --json isDraft --jq .isDraft)" = "true" ] || { echo "$TAG is already published. Raise MARKETING_VERSION in project.yml (and windows/VERSION) first."; exit 1; }
  ASSETS=$(gh release view "$TAG" --json assets --jq '.assets[].name')
  if ! printf '%s\n' "$ASSETS" | grep -qx 'MyPhotoLibraryDescribed.appinstaller'; then echo "The draft $TAG doesn't have the Windows installer yet. Wait for the Release workflow."; exit 1; fi
  if printf '%s\n' "$ASSETS" | grep -q '[.]dmg$'; then echo "$TAG already has a Mac disk image."; exit 1; fi
  git fetch -q origin
  git merge-base --is-ancestor HEAD origin/main || { echo "Release from a commit that's on origin/main (pull, or merge first)."; exit 1; }
fi
[ -z "$(git status --porcelain -- PhotoFinder project.yml)" ] || { echo "Commit the app's changes first, so the release matches a commit."; exit 1; }

# Report a Bug's relay key (#33), which isn't in the repo: from the environment, or the signing kit's file (the PC
# keeps the same key as appkit-relay-photofinder.key, so that name is read too). Without it the release still builds,
# and its Report a Bug opens the GitHub page instead. Never echoed.
RELAY_KEY=${APPKIT_RELAY_KEY:-}
for f in "$KIT/appkit-relay-key-photofinder" "$KIT/appkit-relay-photofinder.key"; do
  if [ -z "$RELAY_KEY" ] && [ -f "$f" ]; then RELAY_KEY=$(tr -d ' \r\n' < "$f"); fi
done
[ -n "$RELAY_KEY" ] || echo "Warning: no relay key (APPKIT_RELAY_KEY or $KIT/appkit-relay-key-photofinder), so Report a Bug will open the GitHub page instead of filing reports."

echo "Building $NAME $VERSION"
rm -rf "$OUT" && mkdir -p "$OUT"
xcodegen generate --quiet
xcodebuild -project PhotoFinder.xcodeproj -scheme PhotoFinder -configuration Release \
  -destination 'generic/platform=macOS' -archivePath "$WORK/app.xcarchive" -allowProvisioningUpdates \
  APPKIT_RELAY_KEY="$RELAY_KEY" archive -quiet
cat > "$WORK/export.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>method</key><string>developer-id</string>
  <key>teamID</key><string>$TEAM</string>
  <key>signingStyle</key><string>automatic</string>
</dict></plist>
PLIST
xcodebuild -exportArchive -archivePath "$WORK/app.xcarchive" -exportPath "$WORK/export" \
  -exportOptionsPlist "$WORK/export.plist" -allowProvisioningUpdates -quiet
APP="$WORK/export/$NAME.app"
codesign --verify --strict --deep --verbose=2 "$APP"

notarize() {
  xcrun notarytool submit "$1" --key "$KIT/asc-api-key.p8" --key-id "$KEY_ID" --issuer "$ISSUER" --wait --timeout 30m
}
echo "Notarizing the app"
ditto -c -k --keepParent "$APP" "$WORK/app.zip"
notarize "$WORK/app.zip"
xcrun stapler staple "$APP"

echo "Making the disk image"
mkdir -p "$WORK/dmg" && cp -R "$APP" "$WORK/dmg/" && ln -s /Applications "$WORK/dmg/Applications"
hdiutil create -volname "$NAME $VERSION" -srcfolder "$WORK/dmg" -fs HFS+ -format UDZO -ov "$OUT/$FILE" -quiet
codesign --sign "$IDENTITY" --timestamp "$OUT/$FILE"
notarize "$OUT/$FILE"
xcrun stapler staple "$OUT/$FILE"
spctl --assess --type open --context context:primary-signature -v "$OUT/$FILE"
echo "Ready: $OUT/$FILE"

if [ "$PUBLISH" = "--publish" ]; then
  gh release upload "$TAG" "$OUT/$FILE"
  echo "Attached $FILE to the draft release $TAG. It's published when Kelly says go."
fi
