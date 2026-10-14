# My Photo Library Described

A screen-reader-friendly photo library app for the Mac and Windows. It shows your photos the way Finder or File Explorer shows files, in a list or a gallery, and describes each photo as you move to it, using the AI built into your computer: Apple Intelligence on the Mac (macOS 27 on Apple silicon), Windows AI on a Copilot+ PC. Photos aren't uploaded to describe them. Without the AI it still browses, searches, sorts and shows your photos.

The Mac app is in `PhotoFinder/` and works with VoiceOver; the Windows app is in [`windows/`](windows/README.md) and works with JAWS, NVDA and Narrator. The two work the same wherever the platform allows; [docs/PLATFORM-DIFFERENCES.md](docs/PLATFORM-DIFFERENCES.md) lists where they don't.

The [user guide](docs/user-guide.md) covers everything, with the full keyboard reference, and the [privacy policy](docs/PRIVACY.md) says what the apps send and keep.

## What it does

- **Sidebar:** on the Mac, the Photos library (All Photos, Favorites and the other smart albums, Dates, My Albums and Shared Albums); on Windows, the Pictures library. Folders added with **File > Add Folder to Library…** appear under Folders and are described like the rest.
- **List view** with Name, Date Taken, Kind, Caption, Dimensions, Duration and Favorite columns, and, if you choose them, Keywords, Rating, Date Modified, File Type, Orientation, Megapixels, Place Taken and Description (and Source on the Mac, Size and Folder on Windows). **View > Labels…** chooses the list's columns and their order, which is the order a screen reader reads each row in, and what the gallery says as you move to a photo (its position, "12 of 400", to start with).
- **Gallery view** shows one photo at a time, large, with its description.
- **Describing:** arrow to a photo and pause to hear its description. Descriptions are saved, so each photo is described once. Describe Photo and Redescribe are there when you want them, and **Photo > Describe Photos in the Background** describes the rest of the library behind the scenes.
- **Description panel:** the photo you're on with its description, in a box you can read by line, word or character and correct.
- **Read-Only Mode:** every launch starts in it, so nothing can change a photo until you turn it off, which asks first. Then Edit Caption and Use Description as Caption write captions (and, for files, alt text) into your photos, and Save Descriptions in Photos, which asks before it's turned on, saves each new description in them. On the Mac, captions for Photos library photos need macOS 27; files in added folders get them on any version.
- **Get Info** lists everything known about a photo. On the Mac, its *Describe Image* button describes the photo with Apple Intelligence (macOS 27), saves the description, and lets you ask follow-up questions; *Find Objects and Text* lists what's in the photo and reads any text in it, using on-device Vision.
- **F6 and Shift-F6** move between the sidebar, the list or gallery, the Description panel (when it's shown), Search and the status bar.
- **Help menu:** Check for Updates, Report a Bug… (files a GitHub issue for you, with a preview of exactly what's sent), Check System… and Check AI Description….

## Building

Mac CI builds the app and runs its unit tests on every pull request. GitHub's Mac runners don't have Xcode 27 yet, so until they do, CI compiles everything except the macOS 27 calls, which are inside `#if !PF_SDK26` (CI defines that flag only when its SDK is older than macOS 27).

Requires Xcode and [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`). `PhotoFinder.xcodeproj` isn't in the repo: it's generated from `project.yml`, so run `xcodegen generate` after cloning, and again after switching branches or pulling when files were added or removed. (To make that automatic, add a `post-checkout` and `post-merge` git hook that runs `xcodegen generate`.)

```bash
xcodegen generate
xcodebuild -project PhotoFinder.xcodeproj -scheme PhotoFinder -configuration Debug -derivedDataPath build build
open "build/Build/Products/Debug/My Photo Library Described.app"
```

The app is signed with an Apple Development certificate. Don't switch it to ad hoc signing: macOS can't remember Photos permission for ad hoc apps and will prompt over and over.

## Notes

- Live Photos export as the still image only.
- In the Photos library, captions, keywords and ratings need macOS 27 or later; files in added folders have them on any version. People and photo titles aren't available through Apple's public PhotoKit API.
- Original file names are saved to `~/Library/Application Support/My Photo Library Described` after the first read, so albums open instantly afterwards. Captions and keywords fill in a few seconds after a list appears.

## Releasing the Mac app

`scripts/release-mac.sh` builds a signed, notarized disk image on a Mac with Xcode 27 (the Apple Intelligence code needs the macOS 27 SDK, which GitHub's runners don't have yet), and leaves just that disk image in `build/release`. The disk image opens as a designed window, the app on the left and Applications on the right with an arrow between them: `scripts/make-dmg.sh` has Finder lay it out over a background drawn by `scripts/make-dmg-background.swift`, so the first run asks to let Terminal control Finder (System Settings > Privacy & Security > Automation), and it stops rather than make a plain one if that's refused. `scripts/make-dmg.sh` can be run alone on any built app to try a change to the layout. Double-clicking `scripts/Build Mac DMG.command` in Finder does the same and shows the disk image when it's done. It uses the Developer ID certificate in the keychain and the App Store Connect key in `~/.signing-kit`. With `--publish` it attaches the disk image to the draft release `v<version>`, which the Windows release workflow makes when the tag is pushed: one tag releases both apps (see CLAUDE.md, Releasing). The app's Help > Check for Updates looks for these releases.
