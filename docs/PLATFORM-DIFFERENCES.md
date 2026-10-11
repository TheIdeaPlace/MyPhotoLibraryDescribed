# Mac and Windows: what's different

Photo Finder has two separate apps: the Mac app (`PhotoFinder/`, SwiftUI and PhotoKit) and the Windows app (`windows/`, WPF). They aim at the same job, browsing photos like folders with a screen reader, but each is built on what its platform offers. This is the list of where they differ, so gaps can be closed on purpose. Update it when either app changes.

## Differences that come from the platform

These follow from what each operating system offers apps, and aren't gaps to close.

| | Mac | Windows |
|---|---|---|
| What's browsed | The Photos library, through PhotoKit | The Pictures library and any folders added, as files on disk |
| Albums | My Albums, album folders and Shared Albums from Photos | No albums: Windows doesn't offer the Photos app's albums to other apps. The Folders section (the folders on disk, with subfolders) takes their place. |
| Smart albums | Photos' own: Favorites, Recently Added, Videos, Live Photos, Screenshots, Selfies, Portrait, Panoramas, Bursts, Slo-mo, Time-lapse, Cinematic, Long Exposure, Animated, RAW | Worked out from the files: Favorites, Recently Added, Videos, Screenshots, Screen Recordings, Panoramas (by shape), RAW, Animated |
| Kinds | Live Photo, Portrait Photo, Burst Photo, HDR Photo, Slo-mo Video, Time-lapse Video, Cinematic Video and the rest, from Photos | Photo, Screenshot, RAW Photo, Animated Image, Panorama, Video, Screen Recording |
| Favorites | Photos' Favorite flag, synced with iCloud Photos | A five-star rating in the file, so it's the same in File Explorer |
| Captions | Photos' caption (macOS 27 and later), synced with iCloud Photos | The file's Title, the one File Explorer shows |
| Ratings | Shown (macOS 27 and later); can't be changed | Shown, and set from Photo > Rating |
| Keywords | Shown (macOS 27 and later) | Shown, from the file's Tags |
| Files in the cloud | Downloaded from iCloud when opened, previewed or exported | OneDrive online-only files keep their file dates until refreshed after opening, because reading their details would download them |
| Opening and previewing | Exports the current version (with edits) to a temporary file, then opens it or shows it in Quick Look | Opens the original file. Preview (Space) is Photo Finder's own window, which plays videos too. |
| Copy, Export, drag | Exports the current version, downloading it if needed | Copies the original file |
| Image description | Apple Intelligence (macOS 27), in the app | Windows AI on Copilot+ PCs, through a small packaged helper, because Windows only lets packaged apps use the model |
| Reading text in photos | Vision | Windows OCR |
| Place Taken | Coordinates, plus a place name from reverse geocoding | Coordinates only: Windows' geocoder needs a Maps key |
| Library changes | PhotoKit's change observer | Watching the library's folders on disk |
| Screen reader announcements | VoiceOver announcements | UIA notification events, for JAWS, NVDA and Narrator |
| Columns from the source | Source (My Library, Shared Album, Synced) | Size and Folder |
| Adding folders | Not applicable: the library is Photos | File > Add Folder to Library and Remove Folder from Library; Delete on a library folder in the sidebar |
| Unavailable menu items | macOS menus already let VoiceOver reach them | WPF skips disabled items, so Photo Finder's menu items stay reachable and are read as unavailable |
| Show the file | Not applicable | Show in File Explorer (Ctrl+Shift+R) |

## Gaps to close

Each gap below has a GitHub issue with the `parity` label (`gh issue list --label parity`). Claim and coordinate there, as `CLAUDE.md` describes. When a gap is closed, move it to "Now the same on both" in the same PR.

### On the Mac, from Windows

- **Setting ratings.** Windows sets ratings from Photo > Rating. PhotoKit may not allow apps to write ratings; check before building.
- **Sort By menu.** Windows has View > Sort By for sorting without a column header. On the Mac, sorting is only by clicking column headers.
- **Search result count.** Windows speaks how many results a search found. The Mac doesn't announce it.
- **Moving between panes.** Both have F6 and Shift+F6 (Go > Next Pane and Previous Pane on the Mac) for the sidebar, the list or gallery, search and the status bar, with Left and Right Arrow between status bar sections. On Windows the status bar is reached only with F6, not Tab, as in QuickMail; on the Mac, SwiftUI's focusable status sections may also be Tab stops.
- **Gallery Home and End.** Windows' gallery goes to the first and last item with Home and End.
- **Tests.** Windows has about 270 tests; the Mac has a handful (columns and the description cache). Mac CI can't compile the macOS 27 code until GitHub's runners have Xcode 27.
- **Installer.** Windows installs from a signed MSIX package through App Installer (`windows/scripts/build-msix.ps1`), with updates. The Mac is built from Xcode.

### On Windows, from the Mac

- **Follow-up questions about a photo.** The Mac's Apple Intelligence answers follow-up questions after describing. Windows' image description model takes no questions; Phi Silica (a Windows text model) could answer from the description, but is a separate piece of work.
- **What's in the photo.** The Mac's Find Objects and Text lists objects and scenes (Vision's classifier) as well as reading text. Windows reads text only: Windows has no built-in classifier for apps.
- **Place names.** The Mac shows the place name for a photo's location. Windows shows coordinates.

## Describing in the background

- **Windows:** Photo > Describe Added Folders in the Background. Adding a folder describes its photos behind the scenes.
- **Mac:** Photo > Describe New Photos in the Background (macOS 27). Photos added to the Photos library are described behind the scenes, and Get Info shows saved descriptions at once. The Mac has no folders to add, so new photos in the library are what's described.

## Now the same on both

- **Columns dialog:** View > Columns… on both, with the same lists, buttons, keys (Alt on Windows, Option on the Mac) and announcements. Column order sets the order each row is read in.
- **Saved descriptions:** both save descriptions, so a photo is described once.
- **Describe Photos Automatically:** Photo > Describe Photos Automatically on both, on by default. The photo you arrow to in the list or gallery is described after a short pause (700 ms) and the description is spoken behind other speech, only while you're still on that photo. Saved descriptions are spoken the same way.
- **Describe Photo:** Ctrl+Shift+D on Windows (list, gallery and Preview), Shift-Command-D on the Mac (list and gallery), describing the photo you're on and speaking it.
- **Choosing how photos are described:** Windows has Photo > Description Type, with Windows AI's four kinds (Accessible, Detailed, Brief, Diagram). The Mac's Settings has the prompt itself, since Apple Intelligence takes any prompt.
