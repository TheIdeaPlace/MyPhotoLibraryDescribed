# Goals

Kelly's goals for the app, from 11 October 2026 (status updated after the Mac PRs and the Windows PRs #52 to #55 merged). More will be added. Each goal links to the issues that track it, and the "Today" column says where things stand. Update it in the same PR as the work.

**Two rules (Kelly, 12 October 2026):** never change a person's photos without their permission at least once, and keep it automatic with as few choices as possible (IDT is the tool for people who want options).

**What this is for:** an app people can use today, and a proof of concept for Apple and Microsoft of how an image gallery can be more accessible. The main point of the app is **describing photos automatically**. "My Photo Library Described" is a working name, not the final one (and "Photo Describer" is out).

| # | Goal | Today | Issues |
|---|---|---|---|
| 1 | Out of the box, look for photos in the platform's default photo library. | Done: the Photos library on the Mac, the Pictures library on Windows. | |
| 2 | Adding folders outside the default location is also nice. | Done on both: File > Add Folder to Library (Mac in #44). | #36, #47 |
| 3 | By default, as the user moves around photos in any view, descriptions are given, using a default prompt or setting as the platform allows. | Done on both: Photo > Describe Photos Automatically, on by default (Mac in #28). | #15 |
| 4 | Descriptions are cached. | Done on both (saved descriptions). | |
| 5 | The user can ask for a photo to be described again, and the cache is updated. | Done on both: Photo > Redescribe, Command-R on the Mac (#40, renamed 13 Oct) and Ctrl+R on Windows (#119). | #29 |
| — | In list view, the user chooses which columns are shown and in what order. | Done on both (View > Columns on Windows; View > Labels, List tab, on the Mac). | |
| 6 | Gallery view uses the photo's name and nothing else by default. Customizing it is a nice-to-have. | Mac: customizable in View > Labels, Gallery tab, with Position only ("12 of 400") as the default (Kelly, 13 Oct). Windows: the name, not customizable yet. The description is in the Description panel, or under the photo when the panel is hidden. | #38 |
| — | Sighted people get the descriptions too. | Done on both: View > Show Description, a Description panel beside the list and gallery (corrections are saved and protected), and a Description column (Mac #51, Windows #55). | #50 |
| — | Keep what the app already does well: export, search, the library concept and the rest. | Kept. | |
| 7 | The user can put a description into a field of the photo (the caption). | Done on both: Photo > Use Description as Caption (Mac in #43). On the Mac, files in added folders get it in the file's XMP description (#31). | #30, #31 |
| 8 | A setting to use the generated description automatically. | Done on both: one switch, Photo > Save Descriptions in Photos, off until you agree once; it fills empty captions and alt text and never replaces anyone's words (Windows #62). | #30, #62 |
| 9 | The Help menu, or somewhere else, checks the computer meets the requirements and everything is installed. | Done on both: Help > Check System (Mac in #42). | #32 |
| 10 | Some image formats have more fields worth offering, such as IPTC's accessibility Alt Text and Extended Description. IDT already writes descriptions into files. | Mac: files in added folders (JPEG, PNG, TIFF, HEIC) get IPTC Alt Text and Extended Description (Accessibility) with Use Description as Caption, and with Save Descriptions in Photos when they're empty; Get Info and the Description panel show them. Photos-library photos have a caption only. Windows: JPEG, PNG and TIFF files get the caption in `dc:description` and the same alt text and extended description (#62, #82); HEIC keeps a Title-only caption. | #31 |
| 11 | Follow the emerging standards for all of Kelly's apps: automatic updates, bug reporting and the rest (The Idea Place app kit). | Help > Report a Bug on both (Mac in #42). Updates: Windows through MSIX; the Mac checks GitHub releases (Help > Check for Updates…) and ships signed, notarized disk images (#22). The app kit relay needs Kelly. | #33, #22 |
| 12 | Built so the name can change: My Photo Library Described isn't the final name. | Done on both: the name comes from one place, `AppInfo.name` on the Mac (#42), `AppInfo.Name` on Windows (also used for the package's display names). | #34 |
| 13 | The app is checked visually. A photo app that looks like garbage isn't good. | Not done yet. | #35 |
| 14 | Usable today, and a proof of concept for Apple and Microsoft. | | All of the above |

The detailed differences between the two apps are in `PLATFORM-DIFFERENCES.md`, and the parity issues are labelled `parity`. The goal issues are labelled `goal`.
