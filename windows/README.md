# Photo Finder for Windows

A Windows app for browsing your photos and videos as if they were folders in File Explorer, built to work well with JAWS, NVDA and Narrator. It's the Windows counterpart of the Mac app in this repository and does the same job: a sidebar and a sortable list replace the Photos app's image grid.

On Windows your photo library is ordinary files, so Photo Finder reads the **Pictures library** (the same folders the Photos app and File Explorer show), plus any folders you add.

## What it does

- **Sidebar** with *Library* (All Photos, Favorites, Recently Added, Videos, Screenshots, Screen Recordings, Panoramas, RAW and Animated), *Dates* (a folder for each year, with a folder for each month inside) and *Folders* (the library's folders on disk, with their subfolders).
- **List view** with Name, Date Taken, Kind, Caption, Dimensions, Duration and Favorite columns. Click a column header, or use View > Sort By, to sort. **View > Columns** (or right-clicking a column header) picks which columns are shown and in what order: Available and Shown lists, Add, Remove, Move Up, Move Down, Move to Top, Move to Bottom and Reset to Default, with a preview of how the selected photo will read. In it, Alt+Up, Alt+Down, Alt+Home and Alt+End move the selected column, Enter in Available adds one and Delete in Shown removes one, and each change is spoken. The order is also the order screen readers read each row in, so putting Caption or Date Taken first means hearing it before the file name. Name is always shown, but can be moved. The extra columns are Keywords, Rating, Date Modified, File Type, Size, Orientation, Megapixels, Location and Folder.
- **Gallery view** (View > as Gallery) shows one full-size image at a time. The image has the same name as the list row, so a screen reader's image description (JAWS Picture Smart, NVDA's describers) looks at the whole picture instead of a thumbnail. Arrow keys move between items.
- **Preview** (Space) is Photo Finder's Quick Look: a large view of the photo, or the video playing, with the arrow keys moving through the list. Space or Escape closes it.
- **Edit Caption** (F2) writes the caption into the file as its Title, the one File Explorer shows, so it goes wherever the file goes (and syncs with OneDrive).
- **Favorites** are photos rated five stars, so they're the same in File Explorer and other apps that read ratings. Photo > Rating sets any rating.
- **Get Info** shows the date, location coordinates, camera, lens and exposure details. Its *Image Contents* section has:
  - **Describe Image**, which describes the photo with Windows' on-device AI on Copilot+ PCs (`Microsoft.Windows.AI.Imaging`). Choose Accessible, Detailed, Brief or Diagram descriptions. The photo isn't sent anywhere.
  - **Read Text in Image**, which reads any text in the photo with Windows' built-in text recognition, on any Windows 10 or 11 PC.
- **Automatic descriptions**: on a Copilot+ PC, arrow to a photo and pause, and Photo Finder describes it with Windows AI and speaks the description as a UIA notification, so JAWS, NVDA and Narrator read it without moving focus. It waits for the row's name to be read first, then queues behind it rather than cutting it off; moving on silences it. Descriptions are saved, so going back to a photo speaks after the same short pause, and arrowing quickly past photos only describes the one you stop on. Descriptions are only made and spoken while you're in the list or gallery, not the sidebar, search or a dialog. They also show in the gallery and in Get Info. It uses the description type last chosen in Get Info; Brief keeps it short. Photo > Describe Photos Automatically (Ctrl+Shift+U) turns it on and off. On a PC without Windows AI it stays quiet; turning it on says why, and gets Windows' model ready if it only needs downloading.
- **Background descriptions**: with Photo > Describe Added Folders in the Background turned on, adding a folder to the library describes its photos behind the scenes, one at a time, so their descriptions are ready (and spoken at once) when you get to them. The status bar shows progress, and you hear when a folder is finished. Photos already described, videos and online-only cloud photos are skipped. It's off until you turn it on; turning it on says if Windows AI isn't available, and gets the model ready if it only needs downloading.
- **Open, Copy, Export, Show in File Explorer and drag** work on the files themselves. Export never overwrites: a second copy of `IMG_1.JPG` becomes `IMG_1 2.JPG`.
- Folders, years and months also appear as rows in the list, so you can move through the library like File Explorer.
- Screen readers hear the results of navigation, sorting, exports, copies and favorite changes.

## Keyboard

| Action | Keys |
|---|---|
| Open a photo, or go into a folder | Enter, Ctrl+O or Alt+Down Arrow |
| Go to the enclosing folder | Alt+Up Arrow or Backspace |
| Preview | Space or Ctrl+Y |
| Get Info | Ctrl+I or Alt+Enter |
| Describe Image (opens Get Info and starts describing) | Ctrl+Shift+D |
| Turn automatic descriptions on or off | Ctrl+Shift+U |
| Copy the files (paste them into File Explorer, Mail and so on) | Ctrl+C |
| Export to a folder | Ctrl+E |
| Show in File Explorer | Ctrl+Shift+R |
| Toggle Favorite | Ctrl+Shift+F |
| Edit Caption | F2 or Ctrl+Shift+E |
| List view / Gallery view | Ctrl+2 / Ctrl+4 |
| Previous or next item in the gallery | Left or Right Arrow (Up or Down also work); Home and End go to the first and last |
| Pause or play a video in Preview | K or P |
| Search the current list | Ctrl+F or F3; Escape clears it, Enter or Down Arrow goes to the results |
| Move between the sidebar, the list and search | F6 and Shift+F6 |
| Go to All Photos | Ctrl+Shift+A |
| Refresh | F5 |

In the sidebar, Enter moves to the list. The menu bar isn't in the Tab order: Alt or F10 opens it, then the arrow keys move along it.

## Describe Image and the AI helper

Windows only lets a *packaged* app use its image description model, so the describing is done by **Photo Finder AI helper** (`src/PhotoFinderAI`), a small packaged console program that Photo Finder starts through the command its package adds, `photofinder-ai`. It's adapted from the helper in [Image Description Toolkit](https://github.com/kellylford/Image-Description-Toolkit) and speaks the same JSON-lines protocol.

It needs:

- A Copilot+ PC (with an NPU) and a recent Windows 11.
- The Windows App Runtime 1.8.
- The helper installed. For development, in Developer Mode: `scripts\build-ai-helper.ps1 -Register`, then `photofinder-ai --check` should report `"state":"Ready"`.

Without the helper, Get Info says why Describe Image isn't available, and everything else, Read Text included, works as usual.

The first description after the model loads can take up to half a minute; later ones take a few seconds.

## Building

Requires the .NET 10 SDK on Windows.

```powershell
cd windows
dotnet build PhotoFinder.sln
dotnet run --project src\PhotoFinder
dotnet test --solution PhotoFinder.sln
```

Run the tests from PowerShell or cmd, not Git Bash: started from Git Bash, the AI helper answers every picture with an internal error (found in Image Description Toolkit; the cause isn't known). The Describe Image tests run only where the helper is installed and Windows' model is ready, and report themselves skipped everywhere else.

## Test builds

`scripts\package-test.ps1 -Destination <folder>` makes a test install for arm64 and x64: a folder, and a zip of it, with `Install.cmd` and `Uninstall.cmd`. Install puts Photo Finder in `%LOCALAPPDATA%\Programs\Photo Finder` for the current user (no admin), adds it to the Start menu, registers the AI helper on a Copilot+ PC with Developer Mode on (the helper isn't signed yet), and starts it. Running Install again updates it.

## Notes

- Online-only OneDrive files keep their file dates in the list: reading their details would download them. Opening, previewing or Get Info downloads that one file; its details fill in the next time the library is refreshed (F5). Changing many files, or any online-only ones, asks first.
- Captions and ratings are written through Windows' property handlers, so they work for the formats Windows can write: JPEG, PNG, HEIC, MP4 and MOV were tested. Photo Finder reads each change back and says which files it couldn't change, rather than claiming a save that didn't happen.
- Writing a caption or rating keeps the file's modified date, so a photo without a Date Taken doesn't jump to today.
- Photo details are saved to `%LOCALAPPDATA%\PhotoFinder` after the first read, so the library opens with its details already filled in. Settings are saved there too.
- People and Windows Photos albums aren't available: Windows doesn't offer them to other apps.
