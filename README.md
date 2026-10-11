# Photo Finder

A Mac app for browsing your Photos library as if it were folders in Finder. It is built to work well with VoiceOver: the sidebar and a sortable list view replace the Photos app's image grid.

There's also a separate native Windows version, built for JAWS, NVDA and Narrator, in [`windows/`](windows/README.md). It browses the Windows Pictures library the same way, and describes photos with Windows' on-device AI on Copilot+ PCs.

## What it does

- **Sidebar** with *Library* (All Photos, Favorites, Videos, Screenshots and the other smart albums), *Dates* (a folder for each year, with a folder for each month inside), *My Albums* (including album folders) and *Shared Albums*.
- **List view** with Name (the original file name, such as `IMG_1234.HEIC`), Date Taken, Kind, Caption, Dimensions, Duration and Favorite columns. Click a column header to sort. Turn on more columns (Keywords, Rating, Date Modified, File Type, Orientation, Megapixels, Location, Source) from View > Columns, or by right-clicking a column header.
- **Gallery view** (View > as Gallery) shows one full-size image at a time. The image uses the same name as the list, so VoiceOver's image descriptions (VO-Shift-L) work on the whole picture instead of a thumbnail. Arrow keys move between items.
- **Edit Caption** (macOS 27 and later) writes the caption to your Photos library, where it syncs with iCloud Photos. PhotoKit can't rename a photo's original file, so the app offers captions instead.
- Folders, years and albums also appear as rows in the list, so you can move through the library like Finder.
- **Get Info** shows the date, location, camera, lens and exposure details. Its *Describe Image* button uses on-device Vision to list what is in the photo and read any text it contains.
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
| List view / Gallery view | Command-2 / Command-4 |
| Previous or next item in the gallery | Left or Right Arrow (Up or Down also work) |
| Search the current list | Command-F |
| Go to All Photos | Shift-Command-A |

## Building

Requires Xcode and [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`).

```bash
xcodegen generate
xcodebuild -project PhotoFinder.xcodeproj -scheme PhotoFinder -configuration Debug -derivedDataPath build build
open "build/Build/Products/Debug/Photo Finder.app"
```

The app is signed with an Apple Development certificate. Don't switch it to ad hoc signing: macOS can't remember Photos permission for ad hoc apps and will prompt over and over.

## Notes

- Live Photos export as the still image only.
- Captions, keywords and ratings need macOS 27 or later. People and photo titles aren't available through Apple's public PhotoKit API.
- Original file names are saved to `~/Library/Application Support/PhotoFinder` after the first read, so albums open instantly afterwards. Captions and keywords fill in a few seconds after a list appears.
