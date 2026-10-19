#!/bin/zsh
# Double-click in Finder to build the Mac disk image: signed, notarized and stapled, as scripts/release-mac.sh makes
# it, without attaching it to a release. When it's done, Finder shows the disk image in build/release.
cd "$(dirname "$0")/.." || exit 1
if scripts/release-mac.sh; then
  open -R build/release/*-mac.dmg
  say "The disk image is ready."
else
  say "The disk image couldn't be built. The Terminal window says why."
fi
echo ""
echo "Press any key to close this window."
read -k 1 -s
