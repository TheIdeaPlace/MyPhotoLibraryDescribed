#!/bin/zsh
# Builds a signed, notarized Mac release of the app and publishes it as a GitHub release (#22).
#
#   scripts/release-mac.sh            build, sign, notarize and staple; leaves the disk image in build/release
#   scripts/release-mac.sh --publish  and then publishes it as the GitHub release mac-v<version>
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
TAG="mac-v$VERSION"
OUT=build/release
FILE="${NAME// /-}-$VERSION-mac.dmg"
[ -n "$VERSION" ] && [ -n "$NAME" ] || { echo "Couldn't read the version or name from project.yml"; exit 1; }
[ -f "$KIT/asc-api-key.p8" ] || { echo "No App Store Connect key at $KIT/asc-api-key.p8"; exit 1; }
IDENTITIES=$(security find-identity -v -p codesigning)
[[ "$IDENTITIES" == *"$IDENTITY"* ]] || { echo "No '$IDENTITY' in the keychain"; exit 1; }
if [ "$PUBLISH" = "--publish" ]; then
  # Checked before ten minutes of notarizing, not after.
  gh auth status >/dev/null 2>&1 || { echo "Sign in to GitHub first: gh auth login"; exit 1; }
  if gh release view "$TAG" >/dev/null 2>&1; then
    echo "$TAG is already released. Raise MARKETING_VERSION in project.yml first."; exit 1
  fi
  git fetch -q origin
  git merge-base --is-ancestor HEAD origin/main || { echo "Release from a commit that's on origin/main (pull, or merge first)."; exit 1; }
fi
[ -z "$(git status --porcelain -- PhotoFinder project.yml)" ] || { echo "Commit the app's changes first, so the release matches a commit."; exit 1; }

echo "Building $NAME $VERSION"
rm -rf "$OUT" && mkdir -p "$OUT"
xcodegen generate --quiet
xcodebuild -project PhotoFinder.xcodeproj -scheme PhotoFinder -configuration Release \
  -destination 'generic/platform=macOS' -archivePath "$OUT/app.xcarchive" -allowProvisioningUpdates archive -quiet
cat > "$OUT/export.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>method</key><string>developer-id</string>
  <key>teamID</key><string>$TEAM</string>
  <key>signingStyle</key><string>automatic</string>
</dict></plist>
PLIST
xcodebuild -exportArchive -archivePath "$OUT/app.xcarchive" -exportPath "$OUT/export" \
  -exportOptionsPlist "$OUT/export.plist" -allowProvisioningUpdates -quiet
APP="$OUT/export/$NAME.app"
codesign --verify --strict --deep --verbose=2 "$APP"

notarize() {
  xcrun notarytool submit "$1" --key "$KIT/asc-api-key.p8" --key-id "$KEY_ID" --issuer "$ISSUER" --wait --timeout 30m
}
echo "Notarizing the app"
ditto -c -k --keepParent "$APP" "$OUT/app.zip"
notarize "$OUT/app.zip"
xcrun stapler staple "$APP"

echo "Making the disk image"
mkdir -p "$OUT/dmg" && cp -R "$APP" "$OUT/dmg/" && ln -s /Applications "$OUT/dmg/Applications"
hdiutil create -volname "$NAME $VERSION" -srcfolder "$OUT/dmg" -fs HFS+ -format UDZO -ov "$OUT/$FILE" -quiet
codesign --sign "$IDENTITY" --timestamp "$OUT/$FILE"
notarize "$OUT/$FILE"
xcrun stapler staple "$OUT/$FILE"
spctl --assess --type open --context context:primary-signature -v "$OUT/$FILE"
echo "Ready: $OUT/$FILE"

if [ "$PUBLISH" = "--publish" ]; then
  gh release create "$TAG" "$OUT/$FILE" --title "$NAME $VERSION for Mac" --target "$(git rev-parse HEAD)" --notes \
"$NAME $VERSION for the Mac: signed and notarized by Apple. Runs on macOS 15 or later; describing photos needs macOS 27 and Apple silicon with Apple Intelligence.

Open the disk image and drag $NAME to Applications. Help > Check for Updates finds newer versions."
  echo "Published $TAG"
fi
