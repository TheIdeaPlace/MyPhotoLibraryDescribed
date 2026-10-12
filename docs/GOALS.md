# Goals

Kelly's goals for the app, from 11 October 2026. More will be added. Each goal links to the issues that track it, and the "Today" column says where things stand. Update it in the same PR as the work.

**What this is for:** an app people can use today, and a proof of concept for Apple and Microsoft of how an image gallery can be more accessible. The main point of the app is **describing photos automatically**. "Photo Finder" is a working name, not the final one (and "Photo Describer" is out).

| # | Goal | Today | Issues |
|---|---|---|---|
| 1 | Out of the box, look for photos in the platform's default photo library. | Done: the Photos library on the Mac, the Pictures library on Windows. | |
| 2 | Adding folders outside the default location is also nice. | Windows: done (File > Add Folder to Library). Mac: missing. | #36 |
| 3 | By default, as the user moves around photos in any view, descriptions are given, using a default prompt or setting as the platform allows. | Windows: done (Describe Photos Automatically). Mac: in review. | #15, PR #28 |
| 4 | Descriptions are cached. | Done on both (saved descriptions). | |
| 5 | The user can ask for a photo to be described again, and the cache is updated. | Only partly, through Get Info, on both. | #29 |
| — | In list view, the user chooses which columns are shown and in what order. | Done on both (View > Columns). | |
| 6 | Gallery view uses the photo's name and nothing else by default. Customizing it is a nice-to-have. | Name only on both. The Mac gallery doesn't show the description visibly yet. | #37, #38 |
| — | Keep what the app already does well: export, search, the library concept and the rest. | Kept. | |
| 7 | The user can put a description into a field of the photo (the caption). | Both can edit captions, but there's no way to use a description as the caption. | #30 |
| 8 | A setting to use the generated description automatically. | Missing. | #30 |
| 9 | The Help menu, or somewhere else, checks the computer meets the requirements and everything is installed. | Missing. Readiness only shows inside Get Info. | #32 |
| 10 | Some image formats have more fields worth offering, such as IPTC's accessibility Alt Text and Extended Description. IDT already writes descriptions into files. | Caption only. | #31 |
| 11 | Follow the emerging standards for all of Kelly's apps: automatic updates, bug reporting and the rest (The Idea Place app kit). | Windows: MSIX updates, no bug reporting. Mac: neither. | #33, #22 |
| 12 | Built so the name can change: Photo Finder isn't the final name. | The name is hard-coded throughout. | #34 |
| 13 | The app is checked visually. A photo app that looks like garbage isn't good. | Not done yet. | #35 |
| 14 | Usable today, and a proof of concept for Apple and Microsoft. | | All of the above |

The detailed differences between the two apps are in `PLATFORM-DIFFERENCES.md`, and the parity issues are labelled `parity`. The goal issues are labelled `goal`.
