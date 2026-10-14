# My Photo Library Described

A Mac app for browsing your Photos library as if it were folders in Finder. It is built to work well with VoiceOver: the sidebar and a sortable list view replace the Photos app's image grid.

There's also a separate native Windows version, built for JAWS, NVDA and Narrator, in [`windows/`](windows/README.md). It browses the Windows Pictures library the same way, and describes photos with Windows' on-device AI on Copilot+ PCs.

## What it does

- **Sidebar** with *Library* (All Photos, Favorites, Videos, Screenshots and the other smart albums), *Dates* (a folder for each year, with a folder for each month inside), *My Albums* (including album folders) and *Shared Albums*.
- **List view** with Name (the original file name, such as `IMG_1234.HEIC`), Date Taken, Kind, Caption, Dimensions, Duration and Favorite columns. Click a column header to sort. **View > Labels…**, List tab, chooses which columns are shown and in what order: Available and Shown lists, Add, Remove, Move Up, Move Down, Move to Top, Move to Bottom and Reset to Default, with a preview of the selected photo's line. Option-Up and Option-Down Arrow (and Option-Home and Option-End) move the selected column, Return in Available adds one and Delete in Shown removes one, and each change is announced. VoiceOver reads each row's cells in this order. The extra columns are Keywords, Rating, Date Modified, File Type, Orientation, Megapixels, Place Taken (where the photo was taken) and Source.
- **Gallery view** (View > as Gallery) shows one full-size image at a time. Moving to a photo says what **View > Labels…**, Gallery tab, chooses, in its order: Position ("12 of 400"), Name, Kind, Date Taken, Dimensions, Caption, Keywords, Rating or Place Taken, with the same lists, buttons, keys and preview as the List tab. Position only is the default. Its description follows after a pause. The image is named with the same words, so nothing is said twice, and VoiceOver's image descriptions (VO-Shift-L) work on the whole picture instead of a thumbnail. The kind, date, size and caption are in the footer below. Arrow keys move between items.
- **Status bar**: Go > Next Pane (F6) and Previous Pane (Shift-F6) move between the sidebar, the list or gallery, Search and the status bar. In the status bar, Left and Right Arrow move between its sections (the library summary, what's being done now, and background describing), and Escape goes back to the list or gallery.
- **Edit Caption** (macOS 27 and later) writes the caption to your Photos library, where it syncs with iCloud Photos. PhotoKit can't rename a photo's original file, so the app offers captions instead.
- Folders, years and albums also appear as rows in the list, so you can move through the library like Finder.
- **Get Info** shows the date, location, camera, lens and exposure details. Its *Describe Image* button uses on-device Vision to list what is in the photo and read any text it contains.
- **Describe Photo** (Command-D, macOS 27): describes the photo you're on with Apple Intelligence and speaks it, in the list or gallery. A saved description is spoken at once. **My Photo Library Described > Settings** (Command-,) has the prompt Apple Intelligence is given, for people who want different descriptions; Use Standard Prompt puts it back.
- **Saved descriptions** (macOS 27): Apple Intelligence descriptions are saved, so Get Info shows a photo's description at once the next time, and follow-up questions still work. **Photo > Describe Photos in the Background** describes the library's photos behind the scenes, so their descriptions are ready; the status bar shows progress, and Photo > Stop Background Describing stops a run. Photos only in iCloud aren't downloaded for this.
- **Open, Quick Look, Copy, Export and drag to Finder** work on the current version of each item (including edits), and download originals from iCloud when needed.
- VoiceOver announces the results of navigation, exports, copies and favorite changes.

## Keyboard

| Action | Keys |
|---|---|
| Open photo, or go into a folder or album | Return, Command-O or Command-Down Arrow |
| Go to the enclosing folder | Command-Up Arrow |
| Quick Look | Space or Command-Y |
| Get Info | Command-I |
| Copy the files (paste them into Finder, Mail and so on) | Command-C |
| Export to a folder | Command-E |
| Toggle Favorite | Shift-Command-F |
| Edit Caption | Shift-Command-E |
| Describe the photo you're on and speak it | Command-D |
| Go to the description, and back to the list or gallery | Shift-Command-D |
| List view / Gallery view | Command-2 / Command-4 |
| Previous or next item in the gallery | Left or Right Arrow (Up or Down also work) |
| Move between the sidebar, the list or gallery, Search and the status bar | F6 and Shift-F6 (Fn-F6 on a Mac keyboard) |
| Search the current list | Command-F |
| Go to All Photos | Shift-Command-A |

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
- Captions, keywords and ratings need macOS 27 or later. People and photo titles aren't available through Apple's public PhotoKit API.
- Original file names are saved to `~/Library/Application Support/My Photo Library Described` after the first read, so albums open instantly afterwards. Captions and keywords fill in a few seconds after a list appears.

## Releasing the Mac app

`scripts/release-mac.sh` builds a signed, notarized disk image on a Mac with Xcode 27 (the Apple Intelligence code needs the macOS 27 SDK, which GitHub's runners don't have yet), and leaves just that disk image in `build/release`. The disk image opens as a designed window, the app on the left and Applications on the right with an arrow between them: `scripts/make-dmg.sh` has Finder lay it out over a background drawn by `scripts/make-dmg-background.swift`, so the first run asks to let Terminal control Finder (System Settings > Privacy & Security > Automation), and it stops rather than make a plain one if that's refused. `scripts/make-dmg.sh` can be run alone on any built app to try a change to the layout. Double-clicking `scripts/Build Mac DMG.command` in Finder does the same and shows the disk image when it's done. It uses the Developer ID certificate in the keychain and the App Store Connect key in `~/.signing-kit`. With `--publish` it attaches the disk image to the draft release `v<version>`, which the Windows release workflow makes when the tag is pushed: one tag releases both apps (see CLAUDE.md, Releasing). The app's Help > Check for Updates looks for these releases.
