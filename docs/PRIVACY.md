# Privacy Policy for My Photo Library Described

**Last Updated: October 14, 2026**

This policy covers both My Photo Library Described apps: the Mac app and the Windows app.

## Overview

My Photo Library Described is a free, open-source, screen-reader-friendly photo browser. It describes your photos on your own computer. It has no accounts, no analytics and no ads, and it doesn't collect, store or send personal information to its developers.

## Your photos stay on your computer

- **Descriptions are made on your computer.** On the Mac, Apple Intelligence describes photos on the Mac itself. On Windows, Windows AI describes them on the PC's NPU (Copilot+ PCs). Your photos aren't uploaded to describe them.
- **Descriptions are saved on your computer**, in My Photo Library Described's own data folder (`~/Library/Application Support/My Photo Library Described` on the Mac; on Windows `%LOCALAPPDATA%\My Photo Library Described`, or inside My Photo Library Described's package data on a PC that didn't have that folder before), so each photo is described once.
- **Your photos aren't changed** unless you allow it. Every launch starts in Read-Only Mode. Captions and alt text are saved in your photos only after you turn off File > Read-Only Mode, and then only by commands you choose (Edit Caption, Use Description as Caption) or after you turn on Save Descriptions in Photos, which asks first.
- **Photos in iCloud Photos** (Mac): macOS downloads a photo that's only in iCloud from your own iCloud account, as the Photos app would, when you open, preview, copy, drag, export or Get Info it, when you choose Describe Photo or Redescribe, and when the gallery shows it (the gallery also fetches the photo before it and the two after it, so moving is quick). Describing as you arrow, describing the next photo ahead of you, background describing and the Description panel's picture in the list never download photos from iCloud Photos.
- **Files kept in iCloud Drive or another cloud folder** (Mac): a file in a folder you added that's only in the cloud (in iCloud Drive, or in a OneDrive, Dropbox or Google Drive folder) is downloaded from that service when you preview, export or Get Info it, when you choose Describe Photo or Redescribe, when the gallery shows it, and when a caption or alt text is written into it with Edit Caption or Use Description as Caption, which asks first. Copying, dragging or opening it hands it to another app, which downloads it when it reads it. Showing its folder doesn't download it: the list shows its name, size and dates from the file system, a placeholder instead of its small picture, and its other details (such as its caption and when it was taken) once it's on your Mac. Describing as you arrow, describing the next photo ahead of you, background describing, Save Descriptions in Photos and the Description panel's picture in the list leave it alone.
- **Online-only OneDrive files** (Windows): a file that's only in OneDrive is downloaded from your OneDrive when you open, preview, export or Get Info it, when the gallery shows it (or the photo either side of it, fetched so moving is quick), and when a caption, alt text, rating or favorite is written into it, which asks first, and when Get Info's Describe Image describes it. Copying or dragging it hands it to another app, which downloads it when it reads it. Describe Photo and Redescribe refuse an online-only file; describing as you arrow, ahead of you and in the background skip it; and the Description panel's picture never downloads it.

## When My Photo Library Described uses the internet

My Photo Library Described connects to the internet only for these things:

1. **Place names (off until you turn it on).** With Look Up Place Names on, Get Info names the place a photo was taken. To do that, the photo's coordinates (its latitude and longitude, and, as with any web request, your IP address; on Windows also the language Windows uses, so the name comes back in it) are sent to a location service, a photo at a time, when you open Get Info. They're also sent for the photos showing when Place Taken is a list column or a gallery label, so the column and label can name the place: on the Mac up to ten a minute, a second and a half apart, and then one every five seconds at most, and on Windows ten and then one every five seconds at most, for the rows on screen and the photo you're on (in the gallery only once you've stopped on it). Nothing is sent until you turn it on, it asks first, and you can turn it off at any time. Each app uses a different free service:
   - **Mac:** Apple's location service. Names it looks up are kept on the Mac, so the same spot isn't sent twice. Apple's privacy policy: https://www.apple.com/legal/privacy/.
   - **Windows:** OpenStreetMap's Nominatim service, run by the OpenStreetMap Foundation. Names it looks up are kept on the PC, so the same spot isn't sent twice. Before asking Nominatim, at most once a day, the app also reads a small settings file from its own GitHub repository (`config/services.json`), so place-name lookups can be paused for everyone without an update if Nominatim asks or the service changes. That request carries nothing about you or your photos beyond what any web request does (your IP address and the app's name and version). GitHub's privacy statement: https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement. Get Info credits OpenStreetMap's contributors with each name, as its licence asks. Nominatim's usage policy: https://operations.osmfoundation.org/policies/nominatim/, and the OpenStreetMap Foundation's privacy policy: https://osmfoundation.org/wiki/Privacy_Policy.
2. **Checking for updates.** On the Mac, My Photo Library Described asks GitHub for the newest release once a day at most, and when you choose Help > Check for Updates. On Windows, Windows' App Installer checks the place My Photo Library Described was installed from (its GitHub releases page) every few hours, and Help > Check for Updates checks it now. The request carries nothing about you or your photos beyond what any web request does: your IP address, and the app's name and version (on the Mac, the app's name and build number, and the versions of macOS and its networking software, which macOS adds to every app's requests). GitHub's privacy statement: https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement.
3. **Reporting a bug.**
   - Help > Report a Bug opens a form in the app, with a preview of exactly what will be sent. Nothing is sent until you choose Send. Send posts the report (the kind, the title, what you wrote, the contact if you give one, and, if "Include the system check" is on, the results of Help > Check System, including the last few description failures; the system check names no folders, and on Windows the library's folders are counted, not named) to The Idea Place's bug-report relay, a Cloudflare Worker, which files it as a GitHub issue in this app's repository using a bot account, so you don't need a GitHub account. Cloudflare handles the request, so it sees your IP address, which the relay uses only to limit how many reports one address can send; the relay passes the report to GitHub and doesn't keep it. Cloudflare's privacy policy: https://www.cloudflare.com/privacypolicy/. If the relay can't be reached, the app copies the whole report and offers Open in Browser, which opens the prefilled GitHub page, as below, to submit yourself. **Everything in a report is public on GitHub, including the contact.**
   - If the app didn't quit cleanly, it asks once at the next start whether to report it. On Windows, choosing Report… adds the last 40 lines of the diagnostic log to the form, with your user folder written as %USERPROFILE%; the lines can still name photos and other folders, so read the preview before sending. On the Mac, only if you choose Report… does it read macOS's crash report for the app (in ~/Library/Logs/DiagnosticReports) and the end of the diagnostic log. From the crash report it takes only the app and macOS versions, the kind of crash and the crashed thread's first 20 steps, never the identifiers macOS records for your Mac; from the log, the last 40 lines. Both leave out your user folder, written as ~, but the log's lines can still name photos and other folders, so read the preview before sending. They go into the form, where you can read them, or leave them out, before sending.
   - The prefilled GitHub page (Open in Browser) carries the report in the page's address so GitHub's form can be filled in, but nothing is posted until you submit the issue yourself, and you can edit or remove anything first. **GitHub issues are public.** You need a GitHub account to submit it that way.
4. **Opening pages in your web browser.** The Help menu's User Guide and Privacy Policy, the links in About My Photo Library Described, an update's release pages (See what's new, and on the Mac the Download button), and the bug-report form's Open Issue button open on GitHub in your web browser.

## What's kept on your computer

- Saved descriptions, and which ones you edited. Each is kept with where it came from: the AI (Apple Intelligence on the Mac, Windows AI on Windows) or you, the prompt it was asked with (on Windows, the description type), the model and the language, the versions of macOS or Windows and of My Photo Library Described, when it was described (and corrected), how long it took, and, when you corrected it, the AI's words before. This stays on your computer with the description.
- Which photos couldn't be described in the background, so they aren't tried again.
- Your settings (the description prompt or type, the columns shown, and the switches in the menus and Settings).
- The folders you added to the library.
- On the Mac, the original file names of your Photos library's photos, so lists open quickly.
- The place names already looked up, so they aren't sent again.
- A diagnostic log of what went wrong, kept for two weeks (Help > Open Log Folder). On the Mac its lines also go to macOS's own log, which stays on the Mac. It isn't sent anywhere. You can attach it to a bug report if you choose, but it names files and folders, so look at it first: issues are public.

On Windows, all of this is in the data folder, so deleting it removes everything; uninstalling the app leaves it, so a reinstall picks up where you left off (the [user guide](user-guide.md#removing-the-app) says how to remove it too). On the Mac, descriptions, the photos that couldn't be described, place names, original file names and the log are in the data folder, and settings and the list of added folders are in My Photo Library Described's preferences (`~/Library/Preferences`, under its bundle identifier). Your photos are never stored in either.

## Data sharing

My Photo Library Described's developers receive nothing from the app, so there's nothing to share, sell or distribute.

## Children's privacy

My Photo Library Described doesn't knowingly collect information from anyone, including children under 13.

## Changes to this policy

Changes are made in this file, with the "Last Updated" date above.

## Open source

My Photo Library Described is open source. You can review all of its code at https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed.
