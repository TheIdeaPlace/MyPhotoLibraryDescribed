# Photo Finder for Windows

A Windows app for browsing your photos and videos as if they were folders in File Explorer, built to work well with JAWS, NVDA and Narrator. It's the Windows counterpart of the Mac app in this repository and does the same job: a sidebar and a sortable list replace the Photos app's image grid.

On Windows your photo library is ordinary files, so Photo Finder reads the **Pictures library** (the same folders the Photos app and File Explorer show), plus any folders you add.

## What it does

- **Sidebar** with *Library* (All Photos, Favorites, Recently Added, Videos, Screenshots, Screen Recordings, Panoramas, RAW and Animated), *Dates* (a folder for each year, with a folder for each month inside) and *Folders* (the library's folders on disk, with their subfolders).
- **List view** with Name, Date Taken, Kind, Caption, Dimensions, Duration and Favorite columns. Click a column header, or use View > Sort By, to sort. **View > Columns** (or right-clicking a column header) picks which columns are shown and in what order: Available and Shown lists, Add, Remove, Move Up, Move Down, Move to Top, Move to Bottom and Reset to Default, with a preview of how the selected photo will read. In it, Alt+Up, Alt+Down, Alt+Home and Alt+End move the selected column, Enter in Available adds one and Delete in Shown removes one, and each change is spoken. The order is also the order screen readers read each row in, so putting Caption or Date Taken first means hearing it before the file name. Name is always shown, but can be moved. The extra columns are Keywords, Rating, Date Modified, File Type, Size, Orientation, Megapixels, Place Taken (where the photo was taken) and Folder (where the file is).
- **Gallery view** (View > as Gallery) shows one full-size image at a time. The image is named with just the file name, so arrowing says which photo and its description follows, and a screen reader's image description (JAWS Picture Smart, NVDA's describers) looks at the whole picture instead of a thumbnail. Arrow keys move between items. Tab from the photo goes through the footer: the name, details, caption and description, then Previous, the position ("3 of 10") and Next.
- **Read-only until you say otherwise (#83).** Photo Finder starts read-only, and nothing changes a photo file: no captions, alt text, ratings or Favorites, and Save Descriptions in Photos is paused (it reads as unchecked, "Paused while read-only"; descriptions made meanwhile aren't saved into photos later). Reading photos to describe them is all it does, and descriptions are kept in Photo Finder's own files. File > Read-Only turns it off after asking every time, with the risk said plainly and Keep Read-Only as the default button. Allowing changes lasts until Photo Finder quits: every launch starts read-only. The title bar ends with "Read-Only" or "Editing". A command that would change a photo says why it can't while read-only. The writers themselves refuse too, so nothing gets past it.
- **Status bar**: the last stop in the F6 loop, as in QuickMail. F6 there says "Status bar" and what it shows; Left and Right Arrow move between its sections (the library summary, what's being done now, and background describing), and Tab, F6 or Escape leave. Ctrl+F9 speaks it without moving focus, from the main window, Preview or Get Info (View > Read Status Bar), and Ctrl+Shift+F9 goes to it (View > Go to Status Bar). JAWS (Insert+Page Down) and NVDA (Insert+End) read it too.
- **Preview** (Space) is Photo Finder's Quick Look: a large view of the photo, or the video playing, with the arrow keys moving through the list. Space or Escape closes it.
- **Edit Caption** (F2) writes the caption into the file, in the standard caption field for JPEG and TIFF and as the Title File Explorer shows for others, so it goes wherever the file goes (and syncs with OneDrive).
- **Descriptions in photos:** Photo > Use Description as Caption (also in the list's context menu and in Get Info) writes each selected photo's saved description into its caption, and into a JPEG or TIFF file's accessibility fields (IPTC Alt Text, with the Extended Description when the alt text had to be shortened to 250 characters), asking before replacing what's there. **Photo > Save Descriptions in Photos** (off until you agree, once) does the same quietly whenever a description is saved, but only fills empty captions and alt text: never what you or another app wrote. Your earlier Save Descriptions as Captions choice carries over. In JPEG and TIFF files the caption is the standard XMP Caption/Description (`dc:description`), with Explorer's Title kept in step when it's empty or held the old caption; other formats keep the caption in the Title. JPEGs are changed segment by segment (#86): only the XMP metadata is replaced, so the EXIF block (with its thumbnail and the camera's MakerNote), comments, the picture and anything after it, such as a motion photo's video, stay byte for byte (except EXIF's own title, emptied in place when it held the old caption); ratings go in the XMP too, with EXIF's own rating patched in place. Multi-picture files (Ultra HDR, MPO) are changed only when the change fits without changing their size; otherwise the reason is said. TIFFs go through WIC, never decoding the picture. Either way the new file is written beside the photo and swapped in whole, so a failure partway leaves the photo as it was. The automatic saving also leaves an extended description another app wrote alone. Get Info shows any alt text in the file.
- **Favorites** are photos rated five stars, so they're the same in File Explorer and other apps that read ratings. Photo > Rating sets any rating.
- **Get Info** shows the date, location coordinates, camera, lens and exposure details. Its *Image Contents* section has:
  - **Describe Image**, which describes the photo with Windows' on-device AI on Copilot+ PCs (`Microsoft.Windows.AI.Imaging`). Choose Accessible, Detailed, Brief or Diagram descriptions. The photo isn't sent anywhere.
  - **Read Text in Image**, which reads any text in the photo with Windows' built-in text recognition, on any Windows 10 or 11 PC.
- **Describe Photo** (Ctrl+Shift+D): describes the photo you're on, in the list, the gallery or Preview, and speaks the description where you are, whether or not automatic descriptions are on. A saved description is spoken at once; **Describe Again** (Ctrl+Alt+Shift+D) describes it afresh and replaces the saved one, and Get Info's button says Describe Again when there's a saved description. Photo > Description Type chooses Accessible, Detailed, Brief or Diagram for this and for automatic descriptions (Get Info's choice changes it too).
- **Automatic descriptions**: on a Copilot+ PC, arrow to a photo and pause, and Photo Finder describes it with Windows AI and speaks the description as a UIA notification, so JAWS, NVDA and Narrator read it without moving focus. It waits for the row's name to be read first, then queues behind it rather than cutting it off; moving on silences it. Descriptions are saved, so going back to a photo speaks after the same short pause, and arrowing quickly past photos only describes the one you stop on. Stepping through photos one at a time, the next photo in the direction you're going is described quietly while you listen to this one, so it's ready when you get there; not on energy saver, and only when Windows AI is ready. Like any new description, it's also saved in that photo when you've chosen to save descriptions in photos. Descriptions are only made and spoken while you're in the list or gallery, not the sidebar, search or a dialog. They also show in the gallery and in Get Info. It uses Photo > Description Type; Brief keeps it short. Photo > Describe Photos Automatically (Ctrl+Shift+U) turns it on and off. On a PC without Windows AI it stays quiet; turning it on says why, and gets Windows' model ready if it only needs downloading.
- **Background descriptions**: with Photo > Describe Added Folders in the Background turned on, adding a folder to the library describes its photos behind the scenes, one at a time, so their descriptions are ready when you get to them: spoken after the usual short pause, without waiting for Windows AI. The status bar shows progress, and you hear when a folder is finished. Photo > Stop Background Describing stops a run (the setting stays on for folders added later); arrowing to a photo always comes first. Photos already described, videos and online-only cloud photos are skipped. It's off until you turn it on; turning it on says if Windows AI isn't available, and gets the model ready if it only needs downloading.
- **Open, Copy, Export, Show in File Explorer and drag** work on the files themselves. Export never overwrites: a second copy of `IMG_1.JPG` becomes `IMG_1 2.JPG`.
- Folders, years and months also appear as rows in the list, so you can move through the library like File Explorer.
- **Description panel** (View > Show Description, Ctrl+Shift+I, on by default): beside the list or gallery, the photo you're on with its description in a box you can read by line, word or character and correct (Enter saves; Shift+Enter starts a new line), marked "Edited by you" so nothing replaces it without asking. A status line says "Describing…", "Saved description", "Edited by you" or "Not described yet", with Describe Again, Use as Caption and Copy, and the caption with Edit Caption. It's in the F6 cycle after the list or gallery, and its width can be dragged. View > Columns also has a **Description** column, off by default, which is read with the row when shown.
- **Help** has Check for Updates…, Report a Bug…, Check System… and About, the order agreed for both apps. **Check System** lists, one line each, whether this PC can describe photos (Windows build, NPU, Windows AI's model and the AI helper) and the library folders, saved descriptions and describing settings, each OK or what to do, with Check Again, Copy to Clipboard and Report a Bug. **Report a Bug** opens a new GitHub issue with those details filled in (without folder paths, since issues are public) and copies the same text.
- Screen readers hear the results of navigation, sorting, exports, copies and favorite changes.

## Keyboard

| Action | Keys |
|---|---|
| Open a photo, or go into a folder | Enter, Ctrl+O or Alt+Down Arrow |
| Go to the enclosing folder | Alt+Up Arrow or Backspace |
| Preview | Space or Ctrl+Y |
| Get Info | Ctrl+I or Alt+Enter |
| Describe the photo you're on and speak it | Ctrl+Shift+D |
| Describe it again, replacing the saved description | Ctrl+Alt+Shift+D |
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
| Move between the sidebar, the list or gallery, the Description panel, search and the status bar | F6 and Shift+F6 |
| Show or hide the Description panel | Ctrl+Shift+I |
| Read the status bar / go to it | Ctrl+F9 / Ctrl+Shift+F9 |
| Move between status bar sections | Left and Right Arrow |
| Go to All Photos | Ctrl+Shift+A |
| Refresh | F5 |
| Remove a library folder (in the sidebar, after asking) | Delete |

In the sidebar, Enter moves to the list, and Delete on a folder you added (or a standard Pictures folder) removes it from the library after asking; the folder and its photos stay on your PC. Folders inside a library folder can't be removed on their own. The menu bar isn't in the Tab order: Alt or F10 opens it, then the arrow keys move along it. Menu items that can't be used right now are still reached with the arrow keys and read as unavailable, as in other Windows menus.

## Describe Image and the AI helper

Windows only lets a *packaged* app use its image description model, so the describing is done by **Photo Finder AI helper** (`src/PhotoFinderAI`), a small packaged console program that Photo Finder starts through the command its package adds, `photofinder-ai`. It's adapted from the helper in [Image Description Toolkit](https://github.com/kellylford/Image-Description-Toolkit) and speaks the same JSON-lines protocol.

It needs:

- A Copilot+ PC (with an NPU) and a recent Windows 11.
- The Windows App Runtime 1.8.
- The helper, which the installer puts in Photo Finder's own package. For development, in Developer Mode: `scripts\build-ai-helper.ps1 -Register`, then `photofinder-ai --check` should report `"state":"Ready"`.

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

## Installing

Photo Finder installs as an MSIX package, Windows' own app format. Open **PhotoFinder.appinstaller** and Windows' App Installer shows Photo Finder with an Install button. It installs the Windows App Runtime first if the PC needs it, then Photo Finder for the PC's architecture (x64 or Arm64). Photo Finder is then in the Start menu, uninstalls from Settings > Apps like any app, and updates itself: Windows checks every few hours and installs a new build while Photo Finder isn't running, and Help > Check for Updates checks now. No admin rights or Developer Mode are needed, and the AI helper is part of the same package, so Describe Photo works on a Copilot+ PC straight away.

To build it:

```powershell
cd windows
.\scripts\build-msix.ps1 -Sign -Destination <folder> [-BaseUri https://...]
```

This writes `PhotoFinder.appinstaller`, `PhotoFinder_<version>.msixbundle` (x64 and Arm64) and `Dependencies\Microsoft.WindowsAppRuntime.1.8_<arch>.msix` (Microsoft's own, from the Windows App SDK). `-BaseUri` is where those files will be downloaded from and where updates are looked for. By default it's the destination folder itself, written into the .appinstaller as that exact path, so it works only on PCs where the same path exists (a OneDrive folder on your own PCs); to share Photo Finder with other people, give it an https address. `-Sign` signs the executables and the bundle with Kelly Ford's Azure Artifact Signing certificate (`scripts\sign-files.ps1`; it needs `az login` and the Artifact Signing client tools), which every Windows PC trusts. Windows won't install an unsigned package, so Windows CI builds one unsigned only to check the packaging. The version is `windows\VERSION` plus a fourth number from the git history (ten times the commit count, plus 1 to 9 for a build with uncommitted changes), so builds of main made here and by a workflow agree and count up. Testers should get builds of main.

The package is laid out from `packaging\AppxManifest.xml` and `packaging\Assets` (the logos, drawn to match `src\PhotoFinder\PhotoFinder.ico`). The .appinstaller doesn't ask Windows to check for updates at every launch: that check stopped the helper's `photofinder-ai.exe` alias from starting. The installed app starts the helper from its own package folder instead of through the alias, so an older test install's helper or a development registration can't stand in for it.

If you used the earlier test installer (Install.cmd), remove it once, from PowerShell: `Get-AppxPackage TheIdeaPlace.PhotoFinder.WindowsAI | Remove-AppxPackage`, then delete `%LOCALAPPDATA%\Programs\Photo Finder` and the Photo Finder shortcut in the Start menu's Programs folder. Your settings and descriptions in `%LOCALAPPDATA%\PhotoFinder` are kept and used by the installed app.

## Notes

- Online-only OneDrive files keep their file dates in the list: reading their details would download them. Opening, previewing or Get Info downloads that one file; its details fill in the next time the library is refreshed (F5). Changing many files, or any online-only ones, asks first.
- Captions and ratings are written through Windows' property handlers, so they work for the formats Windows can write: JPEG, PNG, HEIC, MP4 and MOV were tested. Photo Finder reads each change back and says which files it couldn't change, rather than claiming a save that didn't happen.
- Writing a caption or rating keeps the file's modified date, so a photo without a Date Taken doesn't jump to today.
- Photo details are saved to `%LOCALAPPDATA%\PhotoFinder` after the first read, so the library opens with its details already filled in. Settings and saved descriptions are kept there too. On a PC that didn't have that folder before, Windows keeps it inside Photo Finder's package data instead, and removes it when Photo Finder is uninstalled, saved descriptions included.
- People and Windows Photos albums aren't available: Windows doesn't offer them to other apps.
