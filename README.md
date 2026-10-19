# My Photo Library Described

A screen-reader-friendly photo library app for the Mac and Windows. It shows your photos the way Finder or File Explorer shows files, in a list or a gallery, and describes each photo as you move to it, using the AI built into your computer: Apple Intelligence on the Mac, Windows AI on a Copilot+ PC. Photos aren't uploaded to describe them.

The Mac app works with VoiceOver, and the Windows app with JAWS, NVDA and Narrator. The two work the same wherever the platform allows; [the platform differences](docs/PLATFORM-DIFFERENCES.md) list where they don't.

## Download

Both apps are on the [latest release](https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/releases/latest), signed by Kelly Ford.

- **Mac:** download the `My-Photo-Library-Described-<version>-mac.dmg` disk image, open it and move the app to your Applications folder. It's notarized by Apple. The first time you open it, macOS asks once whether to open an app downloaded from the Internet; choose Open. Help > Check for Updates finds newer versions.
- **Windows:** download and open [MyPhotoLibraryDescribed.appinstaller](https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/releases/latest/download/MyPhotoLibraryDescribed.appinstaller). Windows' App Installer shows the app's name and publisher (Kelly Ford) with an Install button, and installs the Windows App Runtime it needs first if the PC doesn't have it. Windows then checks the same place for updates every few hours and installs them while the app isn't running; Help > Check for Updates checks now.

The [user guide](docs/user-guide.md) covers installing, using and removing the app, with the full keyboard reference.

## What you need

- **Mac:** macOS 27 or later on Apple silicon, with Apple Intelligence turned on.
- **Windows:** Windows 11 version 24H2 or later on a Copilot+ PC.

If the AI isn't available, the app still browses, searches, sorts and shows your photos; it just doesn't describe them. Help > Check System says what your computer has and what to do about anything missing.

## What it does

- **Your library in a sidebar.** On the Mac, your Photos library: All Photos, Favorites and the other smart albums, Dates, My Albums and Shared Albums. On Windows, your Pictures library, by kind, date and folder. Folders you add with File > Add Folder to Library… appear under Folders on both.
- **A list and a gallery.** The list has the columns you choose; the gallery shows one photo at a time, large, with its description. View > Labels… chooses the list's columns and their order, which is the order a screen reader reads each row in, and what the gallery says as you move.
- **Describes photos as you move.** Arrow to a photo and pause to hear it described. Descriptions are saved, so each photo is described once. Describe Photo and Redescribe are there when you want them.
- **Describes in the background, if you turn it on.** Photo > Describe Photos in the Background describes the rest of the library behind the scenes, so descriptions are ready when you get to them.
- **The Description panel** shows the photo you're on with its description, in a box you can read by line, word or character, and correct.
- **Read-Only Mode.** Every launch starts in it, so nothing can change a photo until you turn it off, which asks first.
- **Captions and alt text in your photos.** With changes allowed, Edit Caption writes a caption, and Use Description as Caption writes the description as the caption and, in photo files, as alt text in the IPTC accessibility fields. Save Descriptions in Photos, which asks before it's turned on, does this for each new description, without replacing anything already there.
- **Get Info** lists everything known about a photo, and can describe it. On the Mac, Find Objects and Text lists what's in the photo and reads any text in it; on Windows, Read Text in Image reads any text in it.
- **F6 and Shift-F6** move between the sidebar, the list or gallery, the Description panel, Search and the status bar.
- **The Help menu** has Check for Updates, Report a Bug…, Check System…, Check AI Description…, Open Log Folder, the user guide and the privacy policy.

## Privacy

Photos are described on your computer and never uploaded to describe them, and the app has no accounts, analytics or ads. The [privacy policy](docs/PRIVACY.md) lists everything the app sends over the internet, and when.

## Feedback

Choose Help > Report a Bug… in either app. It files a GitHub issue for you, with a preview of exactly what's sent, and you don't need a GitHub account. Everything in a report is public. You can also [open an issue on GitHub](https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/issues) yourself.

Features planned for a later version and known issues are listed as [issues labelled v-next](https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/issues?q=is%3Aissue+is%3Aopen+label%3Av-next).

## Licence

My Photo Library Described is free and open source, under the [MIT License](LICENSE).

## For developers

### Repository layout

- `PhotoFinder/`: the Mac app (SwiftUI, PhotoKit, Apple Intelligence). Its tests are in `PhotoFinderTests/`, and the Xcode project is generated from `project.yml`.
- `windows/`: the Windows app (WPF, .NET, and the packaged `PhotoFinderAI` helper for Windows AI). See [windows/README.md](windows/README.md).
- `docs/`: the user guide, privacy policy, platform differences, goals and release notes.
- `scripts/`: building and releasing the Mac app.
- `config/`: `services.json`, which the Windows app reads so place-name lookups can be paused for everyone if needed.

### Building the Mac app

Requires Xcode and [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`). `PhotoFinder.xcodeproj` isn't in the repository, so run `xcodegen generate` after cloning, and again after switching branches or pulling.

```bash
xcodegen generate
xcodebuild -project PhotoFinder.xcodeproj -scheme PhotoFinder -configuration Debug -derivedDataPath build build
open "build/Build/Products/Debug/My Photo Library Described.app"
```

The macOS 27 code (Apple Intelligence and Photos' extended metadata) is inside `#if !PF_SDK26`. GitHub's Mac runners don't have Xcode 27 yet, so CI defines that flag and doesn't compile that code; changes to it need a local Xcode 27 build.

The app is signed with an Apple Development certificate. Don't switch it to ad hoc signing: macOS can't remember Photos permission for ad hoc apps and asks over and over. To build it yourself, set `DEVELOPMENT_TEAM` in `project.yml` to your own team.

### Building the Windows app

Requires the .NET 10 SDK on Windows. From `windows/`, in PowerShell or cmd, not Git Bash (tests started from Git Bash get an internal error from the AI helper on every picture):

```powershell
dotnet build PhotoFinder.sln
dotnet test --solution PhotoFinder.sln
```

Windows AI only works from the packaged helper on a Copilot+ PC, so tests use fakes for it. [windows/README.md](windows/README.md) covers the helper and the installer.

### Releasing

One tag, `v<version>`, releases both apps. Pushing it builds and signs the Windows installer into a draft GitHub release; `scripts/release-mac.sh --publish`, run on a Mac with Xcode 27, adds the signed, notarized disk image to the same draft. The Releasing section of [CLAUDE.md](CLAUDE.md) has the steps.
