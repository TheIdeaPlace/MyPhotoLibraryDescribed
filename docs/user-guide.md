# Photo Finder User Guide

Photo Finder is a photo browser built for screen reader users, on the Mac and on Windows. It shows your photos the way Finder or File Explorer shows files, in a list or a gallery, and describes each photo as you move to it, using the AI built into your computer. "Photo Finder" is a working name.

The two apps work the same wherever the platform allows. Where they differ, this guide says so. Keys are given as Mac / Windows.

---

## Contents

- [What you need](#what-you-need)
- [Layout](#layout)
- [Hearing photos described](#hearing-photos-described)
- [The Description panel](#the-description-panel)
- [Read-only, and changing your photos](#read-only-and-changing-your-photos)
- [Captions and saving descriptions in photos](#captions-and-saving-descriptions-in-photos)
- [Adding folders](#adding-folders)
- [Columns and sorting](#columns-and-sorting)
- [Get Info](#get-info)
- [Place names](#place-names)
- [Settings](#settings)
- [Help menu](#help-menu)
- [Keyboard shortcut reference](#keyboard-shortcut-reference)
- [Privacy](#privacy)

---

## What you need

- **Mac:** macOS 27 or later on Apple silicon, with Apple Intelligence turned on, for descriptions. Photo Finder browses your Photos library, so it asks for access to Photos the first time.
- **Windows:** Windows 11 on a Copilot+ PC, for descriptions. Photo Finder browses your Pictures library.

Without the AI, Photo Finder still browses, searches, sorts and shows your photos; it just doesn't describe them. **Help > Check System** says what your computer has and what to do about anything missing.

## Layout

The window has a sidebar (the library, albums on the Mac, and folders), the list or gallery of photos, the Description panel, a search field and a status bar. **F6** and **Shift-F6** move between them.

- **List** (Command-2 / Ctrl+2) shows one photo per row, with the columns you choose.
- **Gallery** (Command-4 / Ctrl+4) shows one photo at a time with its description; Left and Right Arrow move between photos, Home and End go to the first and last.
- **The status bar** says what's in the list, what's being done, and how background describing is going. Control-F9 / Ctrl+F9 reads it from anywhere, and Control-Shift-F9 / Ctrl+Shift+F9 goes to it. On a Mac laptop, add Fn unless the function keys are set as standard keys.

## Hearing photos described

**Photo > Describe Photos Automatically** is on by default. Arrow to a photo and pause: its name is read first, then its description. Arrowing quickly past photos describes only the one you stop on. Descriptions are saved, so going back to a photo reads its description at once.

While you step through photos one at a time, Photo Finder quietly describes the next one in the direction you're going, so it's ready when you get there. It doesn't do this on Low Power Mode / energy saver.

- **Describe Photo** (Shift-Command-D / Ctrl+Shift+D) describes the photo you're on and reads it, whether or not automatic descriptions are on.
- **Describe Again** (Option-Shift-Command-D / Ctrl+Alt+Shift+D) describes it afresh and replaces the saved description.
- **Describe in the background:** Photo > Describe Photos in the Background describes photos that haven't been described yet behind the scenes, so their descriptions are ready when you get to them. Photo > Stop Background Describing stops it for now.

How photos are described: on the Mac, Settings has the prompt Apple Intelligence is given (leave it empty for the standard one). On Windows, Photo > Description Type chooses Accessible, Detailed, Brief or Diagram.

## The Description panel

**View > Show Description** (Option-Command-I / Ctrl+Shift+I), on by default, shows the photo you're on with its description in a box you can read by line, word or character, and correct. A correction is saved and marked "Edited by you", and nothing replaces it without asking. The panel also has Describe Again, Use as Caption and Copy, and the photo's caption with Edit Caption.

## Read-only, and changing your photos

Photo Finder starts **read-only** every time: nothing it does can change a photo. The window title ends with "Read-Only".

To change photos (favorites, ratings, captions), turn off **File > Read-Only**. Photo Finder asks "Let Photo Finder change your photos?" first; Keep Read-Only is the default. Changes are then allowed until Photo Finder quits, and the title ends with "Editing".

## Captions and saving descriptions in photos

- **Edit Caption** (Shift-Command-E / Ctrl+Shift+E) changes a photo's caption.
- **Use Description as Caption** makes the saved description the caption of the photos you choose. It asks before replacing a caption that's already there.
- **Photo > Save Descriptions in Photos** saves each new description in the photo too: as its caption when it has none, and, for files, in its alt text when that's empty. It's off until you turn it on, and asks first. Nothing you or another app wrote is replaced. While Photo Finder is read-only, it's paused.

On the Mac, a photo in your Photos library gets a Photos caption, which syncs with iCloud Photos. A file in a folder you added gets its caption, and its accessibility alt text, written in the file itself. Only those fields change; the picture and everything else in the file stay exactly as they were.

## Adding folders

**File > Add Folder to Library…** adds a folder of photos to the sidebar, under Folders, and its photos are described like the rest. **Remove Folder from Library** (or Delete on it in the sidebar) removes it after asking. The folder and its photos stay on your computer.

## Columns and sorting

**View > Columns…** chooses which columns the list shows and in what order. The order is also the order a screen reader reads each row in, so putting Caption first means hearing it before the name. **View > Sort By** sorts by any column; choosing it again reverses the order.

## Get Info

**Get Info** (Command-I / Ctrl+I) lists everything known about a photo: its dates, size, camera settings, caption, keywords, rating, coordinates, and its saved description, with Describe Again. On the Mac you can also ask Apple Intelligence follow-up questions about the photo. Escape closes it.

## Place names

Photos often record where they were taken, as coordinates. Get Info always shows the coordinates. On the Mac, **Settings > Look Up Place Names** also names the place, such as "Pike Place Market, Seattle, Washington, United States". To find the name, the coordinates (nothing else) are sent to Apple's location service, so it's off until you turn it on, and it asks first. The Windows app shows coordinates only for now.

## Settings

On the Mac, Photo Finder > Settings (Command-,) has the description prompt, Save Descriptions in Photos and Look Up Place Names. On Windows, these are in the Photo menu.

## Help menu

- **Check for Updates** says whether there's a newer version. Photo Finder also checks quietly once a day, and the item then reads "Update to *version* available…".
- **Report a Bug…** opens a new GitHub issue in your browser with the details filled in. GitHub issues are public, and nothing is sent until you submit it.
- **Check System…** checks that your computer can describe photos, and says what to do if it can't.
- **Mac only, for now:** User Guide opens this guide, and Privacy Policy opens the privacy policy.
- **Check AI Description…** describes a sample photo that comes with the app (a red mug on a table beside a sign reading OPEN), to show describing works end to end. It saves nothing.
- **Open Log Folder** shows the diagnostic log: one file a day, kept two weeks, of what went wrong. It stays on your computer unless you attach it to a bug report; it names files and folders, so look at it first.

## Keyboard shortcut reference

| Action | Mac | Windows |
|---|---|---|
| Open a photo, or go into a folder | Command-O or Command-Down Arrow | Enter, Ctrl+O or Alt+Down Arrow |
| Go to the enclosing folder | Command-Up Arrow | Alt+Up Arrow or Backspace |
| Preview | Command-Y (Quick Look) | Space or Ctrl+Y |
| Get Info | Command-I | Ctrl+I or Alt+Enter |
| Describe Photo | Shift-Command-D | Ctrl+Shift+D |
| Describe Again | Option-Shift-Command-D | Ctrl+Alt+Shift+D |
| Turn automatic descriptions on or off | Photo menu | Ctrl+Shift+U |
| Export | Command-E | Ctrl+E |
| Toggle Favorite | Shift-Command-F | Ctrl+Shift+F |
| Edit Caption | Shift-Command-E | F2 or Ctrl+Shift+E |
| List / Gallery | Command-2 / Command-4 | Ctrl+2 / Ctrl+4 |
| Search | Command-F | Ctrl+F or F3 |
| Next / previous pane | F6 / Shift-F6 | F6 / Shift+F6 |
| Show or hide the Description panel | Option-Command-I | Ctrl+Shift+I |
| Read the status bar / go to it | Control-F9 / Control-Shift-F9 | Ctrl+F9 / Ctrl+Shift+F9 |
| Go to All Photos | Shift-Command-A | Ctrl+Shift+A |
| Refresh | Command-R | F5 |

## Privacy

Photos are described on your computer and never uploaded to describe them. Photo Finder uses the internet only to check for updates, to look up place names if you turn that on, and to open GitHub when you report a bug or open this guide. The [privacy policy](PRIVACY.md) has the details.
