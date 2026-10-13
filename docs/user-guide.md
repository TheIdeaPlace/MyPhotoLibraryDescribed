# My Photo Library Described User Guide

My Photo Library Described is a photo browser built for screen reader users, on the Mac and on Windows. It shows your photos the way Finder or File Explorer shows files, in a list or a gallery, and describes each photo as you move to it, using the AI built into your computer. "My Photo Library Described" is a working name.

The two apps work the same wherever the platform allows. Where they differ, this guide says so. Keys are given as Mac / Windows.

---

## Contents

- [Getting the app](#getting-the-app)
- [What you need](#what-you-need)
- [Layout](#layout)
- [Hearing photos described](#hearing-photos-described)
- [The Description panel](#the-description-panel)
- [Read-only, and changing your photos](#read-only-and-changing-your-photos)
- [Captions and saving descriptions in photos](#captions-and-saving-descriptions-in-photos)
- [Adding folders](#adding-folders)
- [Columns, labels and sorting](#columns-labels-and-sorting)
- [Get Info](#get-info)
- [Place names](#place-names)
- [Settings](#settings)
- [Help menu](#help-menu)
- [Keyboard shortcut reference](#keyboard-shortcut-reference)
- [Removing the app](#removing-the-app)
- [Privacy](#privacy)

---

## Getting the app

Both apps are on the [releases page](https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/releases/latest), signed by Kelly Ford.

- **Mac:** download the `My-Photo-Library-Described-<version>-mac.dmg` disk image, open it and drag the app to Applications. It's signed by Kelly Ford and notarized by Apple, so it opens without warnings. Describing photos needs macOS 27 on Apple silicon with Apple Intelligence. It's built to open on macOS 15 and later for browsing, but has only been tested on macOS 26 and 27.
- **Windows:** download and open `PhotoFinder.appinstaller`. Windows' App Installer shows the app's name and publisher (Kelly Ford) with an Install button, and installs the Windows App Runtime it needs first if the PC doesn't have it. Windows then checks the same place for updates every few hours and installs them while the app isn't running; Help > Check for Updates checks now.

## What you need

- **Mac:** macOS 27 or later on Apple silicon, with Apple Intelligence turned on, for descriptions. My Photo Library Described browses your Photos library, so it asks for access to Photos the first time. Without that access, folders you add with File > Add Folder to Library still work, and the sidebar's "Photos library: no access" row opens Privacy Settings.
- **Windows:** Windows 11 on a Copilot+ PC, for descriptions. My Photo Library Described browses your Pictures library.

Without the AI, My Photo Library Described still browses, searches, sorts and shows your photos; it just doesn't describe them. **Help > Check System** says what your computer has and what to do about anything missing.

## Layout

The window has a sidebar (the library, albums on the Mac, and folders), the list or gallery of photos, the Description panel, a search field and a status bar. **F6** and **Shift-F6** move between them.

- **List** (Command-2 / Ctrl+2) shows one photo per row, with the columns you choose.
- **Gallery** (Command-4 / Ctrl+4) shows one photo at a time with its description; Left and Right Arrow move between photos, Home and End go to the first and last. Moving to a photo says its position, such as "12 of 400", then its description; View > Labels…, Gallery tab, chooses what's said instead (see [Columns, labels and sorting](#columns-labels-and-sorting)).
- **The status bar** says what's in the list, what's being done, and how background describing is going. Control-F9 / Ctrl+F9 reads it from anywhere, and Control-Shift-F9 / Ctrl+Shift+F9 goes to it (in the Go menu on the Mac, the View menu on Windows). On a Mac laptop, add Fn unless the function keys are set as standard keys.

## Hearing photos described

**Photo > Describe Photos Automatically** is on by default. Arrow to a photo and pause: its name is read first, then its description. Arrowing quickly past photos describes only the one you stop on. Descriptions are saved, so going back to a photo reads its description at once.

While you step through photos one at a time, My Photo Library Described quietly describes the next one in the direction you're going, so it's ready when you get there. It doesn't do this on Low Power Mode / energy saver.

- **Describe Photo** (Command-D / Ctrl+D) describes the photo you're on and reads it, whether or not automatic descriptions are on.
- **Redescribe** (Command-R / Ctrl+R) describes it afresh and replaces the saved description.
- **Describe in the background:** Photo > Describe Photos in the Background describes photos that haven't been described yet behind the scenes, so their descriptions are ready when you get to them. Photo > Stop Background Describing stops it for now.

How photos are described: on the Mac, Settings has the prompt Apple Intelligence is given (leave it empty for the standard one). On Windows, Photo > Description Type chooses Accessible, Detailed, Brief or Diagram.

## The Description panel

In list view, the panel starts with a picture of the photo you're on, as Finder's and File Explorer's preview panes do, for anyone looking at the screen with you; screen readers skip it, since the description says what's in it. (The gallery already shows the photo large.) It never downloads a photo that's only in iCloud or OneDrive; those show their small thumbnail, or a placeholder when there isn't one.

**View > Show Description** (Option-Command-I / Ctrl+Shift+I), on by default, shows the photo you're on with its description in a box you can read by line, word or character, and correct. A correction is saved and marked "Edited by you", and nothing replaces it without asking. The panel also has Redescribe, Use as Caption and Copy, and the photo's caption with Edit Caption. **Go > Go to Description or Back to Photos** (Shift-Command-D / Ctrl+Shift+D) goes straight to the description, showing the panel if it's hidden, and pressed again goes back to the list or gallery. To read descriptions one after another without leaving the box (with a braille display, say), use Go > Next Photo (Command-Right Arrow / Alt+Right Arrow) and Previous Photo (Command-Left / Alt+Left): the photo changes, its name and position are read (in the Mac's gallery, what View > Labels… chooses), and focus stays where it is, with the box showing the new photo's description.

## Read-only, and changing your photos

My Photo Library Described starts in **Read-Only Mode** every time: nothing it does can change a photo. The window title ends with "Read-Only Mode".

To change photos (favorites, ratings, captions), turn off **File > Read-Only Mode**. My Photo Library Described asks "Let My Photo Library Described change your photos?" first; Keep Read-Only Mode is the default. Changes are then allowed until My Photo Library Described quits, and the title ends with "Editing Mode".

## Captions and saving descriptions in photos

- **Edit Caption** (Shift-Command-E / Ctrl+Shift+E) changes a photo's caption.
- **Use Description as Caption** makes the saved description the caption of the photos you choose. It asks before replacing a caption that's already there.
- **Photo > Save Descriptions in Photos** saves each new description in the photo too: as its caption when it has none, and, for files, in its alt text when that's empty. It's off until you turn it on, and asks first. Nothing you or another app wrote is replaced. While My Photo Library Described is in Read-Only Mode, it's paused.

On the Mac, a photo in your Photos library gets a Photos caption, which syncs with iCloud Photos. A file in a folder you added gets its caption, and its accessibility alt text, written in the file itself. Only those fields change; the picture and everything else in the file stay exactly as they were.

## Adding folders

**File > Add Folder to Library…** adds a folder of photos to the sidebar, under Folders, and its photos are described like the rest. **Remove Folder from Library** (or Delete on it in the sidebar) removes it after asking. The folder and its photos stay on your computer.

## Columns, labels and sorting

**View > Labels…** chooses what's read for each photo, in two tabs.

- **List** chooses which columns the list shows and in what order. The order is also the order a screen reader reads each row in, so putting Caption first means hearing it before the name.
- **Gallery** (Mac) chooses what's said, and in what order, when you move to a photo in the gallery: Position ("12 of 400"), Name, Kind, Date Taken, Dimensions, Caption, Keywords, Rating and Place Taken. The default is Position only. The description isn't one of them: it follows after a pause when Describe Photos Automatically is on. The details under the photo stay the same whatever you choose.

Each tab has the same Available and Shown lists, with Add, Remove, Move Up, Move Down, Move to Top, Move to Bottom and Reset to Default, and a preview of how the photo you're on will read. Return in Available adds, Delete or Return in Shown removes, and Option-Up and Option-Down Arrow (Option-Home and Option-End for the top and bottom) move the selected item; each change is announced. OK keeps both tabs' choices and Cancel leaves them. The sheet opens on the tab for the view you're in.

**View > Sort By** sorts by any column; choosing it again reverses the order.

## Get Info

**Get Info** (Command-I / Ctrl+I) lists everything known about a photo: its dates, size, camera settings, caption, keywords, rating, coordinates, and its saved description, with Redescribe. On the Mac you can also ask Apple Intelligence follow-up questions about the photo. Escape closes it.

## Place names

Photos often record where they were taken, as coordinates. Get Info always shows the coordinates. **Look Up Place Names** (in Settings on the Mac, the Photo menu on Windows) also names the place, such as "Pike Place Market, Seattle, Washington, United States". To find the name, the coordinates are sent to a location service (on Windows with the language Windows uses, so the name comes back in it), and nothing about you or the photo when you open Get Info: Apple's on the Mac, and OpenStreetMap's Nominatim on Windows, where each name ends "(© OpenStreetMap contributors)" as its licence asks. Because of that, it's off until you turn it on, and it asks first. On Windows it also reads a small settings file from the app's GitHub repository, at most once a day, so lookups can be paused for everyone without an update if Nominatim asks; Get Info then says place names are paused. The two services sometimes name the same place a little differently.

## Settings

On the Mac, My Photo Library Described > Settings (Command-,) has the description prompt, Save Descriptions in Photos and Look Up Place Names. On Windows, these are in the Photo menu.

## Help menu

- **Check for Updates** says whether there's a newer version. My Photo Library Described also checks quietly once a day, and the item then reads "Update to *version* available…".
- **Report a Bug…** opens a form. Choose Bug or Suggestion, give it a title, and say what happened, what you expected and the steps (or, for a suggestion, what you'd like). Contact is optional. "Include the system check" adds Check System's results, without folder paths, and is on to start with. The preview shows exactly what will be sent.
  - Send files the report as a GitHub issue for you, through The Idea Place's bug-report service, so you don't need a GitHub account. The form then says "Report filed as issue" and the number, with a button to open it.
  - If the report can't be sent that way, the form says so and the whole report is copied; Open in Browser opens a new GitHub issue with the details filled in, for you to submit.
  - **Everything in a report is public on GitHub, including the contact.**
  - Check System and Check AI Description have Report a Bug… buttons too, which open the same form; Check AI Description's adds its result.
  - If the app didn't quit cleanly last time, it asks once when it starts whether to report it. On the Mac, restarting or shutting down the Mac with the app open doesn't count, unless macOS recorded a crash first. Report… opens the form with the end of the log, without folder paths, and on the Mac a summary of macOS's crash report (versions, the kind of crash and where it happened, nothing that identifies your Mac), for you to read, or leave out, before sending.
- **Check System…** checks that your computer can describe photos, and says what to do if it can't.
- **Check AI Description…** describes a sample photo that comes with the app (a red mug on a table beside a sign reading OPEN), to show describing works end to end. It saves nothing.
- **Open Log Folder** shows the diagnostic log: one file a day, kept two weeks, of what went wrong. It stays on your computer unless you attach it to a bug report; it names files and folders, so look at it first.
- **User Guide** opens this guide, and **Privacy Policy** opens the privacy policy, on GitHub.

**About My Photo Library Described** (in the app menu on the Mac, and at the end of the Help menu on Windows) has the version, with links to the source code, this guide and the privacy policy.

## Keyboard shortcut reference

| Action | Mac | Windows |
|---|---|---|
| Open a photo, or go into a folder | Command-O or Command-Down Arrow | Enter, Ctrl+O or Alt+Down Arrow |
| Go to the enclosing folder | Command-Up Arrow | Alt+Up Arrow or Backspace |
| Preview | Command-Y (Quick Look) | Space or Ctrl+Y |
| Get Info | Command-I | Ctrl+I or Alt+Enter |
| Describe Photo | Command-D | Ctrl+D |
| Go to the description, and back to the list or gallery | Shift-Command-D | Ctrl+Shift+D |
| Redescribe | Command-R | Ctrl+R |
| Turn automatic descriptions on or off | Photo menu | Ctrl+Shift+U |
| Export | Command-E | Ctrl+E |
| Toggle Favorite | Shift-Command-F | Ctrl+Shift+F |
| Edit Caption | Shift-Command-E | F2 or Ctrl+Shift+E |
| List / Gallery | Command-2 / Command-4 | Ctrl+2 / Ctrl+4 |
| Search | Command-F | Ctrl+F or F3 |
| Next / previous photo, leaving focus where it is (in the Description panel, say) | Command-Right / Command-Left Arrow | Alt+Right / Alt+Left Arrow |
| Next / previous pane | F6 / Shift-F6 | F6 / Shift+F6 |
| Show or hide the Description panel | Option-Command-I | Ctrl+Shift+I |
| Read the status bar / go to it | Control-F9 / Control-Shift-F9 | Ctrl+F9 / Ctrl+Shift+F9 |
| Go to All Photos | Shift-Command-A | Ctrl+Shift+A |
| Refresh | Shift-Command-R | F5 |

## Removing the app

- **Windows:** open Settings > Apps > Installed apps, find My Photo Library Described, choose More options, then Uninstall. Or find it in the Start menu, open its context menu (Shift+F10 or the Applications key) and choose Uninstall. It isn't in Control Panel's Programs and Features (appwiz.cpl): apps installed with App Installer, as packages, are listed only in Settings. Uninstalling removes the app and its AI helper together.
- **Mac:** quit it and drag My Photo Library Described from Applications to the Trash.

Your photos are never touched by removing the app, and descriptions saved into photos stay in them. The app's own files are left, so reinstalling picks up where you left off; to remove them too:

- **Windows:** delete the folder `%LOCALAPPDATA%\PhotoFinder` (type it into File Explorer's address bar). It holds the descriptions, settings, place names already looked up and the log. If it isn't there, Windows kept it inside the app's package, and uninstalling removed it.
- **Mac:** delete `~/Library/Application Support/PhotoFinder` (descriptions and the log) and `~/Library/Preferences/net.theideaplace.PhotoFinder.plist` (settings and the folders you added). In Finder, Go > Go to Folder reaches them.

## Privacy

Photos are described on your computer and never uploaded to describe them. My Photo Library Described uses the internet only to check for updates, to look up place names if you turn that on, to send a bug report when you choose Send (or open GitHub to file it), and to open this guide. The [privacy policy](PRIVACY.md) has the details.
