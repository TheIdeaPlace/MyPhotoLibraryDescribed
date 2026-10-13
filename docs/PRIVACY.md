# Privacy Policy for My Photo Library Described

**Last Updated: October 13, 2026**

This policy covers both My Photo Library Described apps: the Mac app and the Windows app. "My Photo Library Described" is a working name.

## Overview

My Photo Library Described is a free, open-source photo browser built for screen reader users. It describes your photos on your own computer. It has no accounts, no analytics and no ads, and it doesn't collect, store or send personal information to its developers.

## Your photos stay on your computer

- **Descriptions are made on your computer.** On the Mac, Apple Intelligence describes photos on the Mac itself. On Windows, Windows AI describes them on the PC's NPU (Copilot+ PCs). Your photos aren't uploaded to describe them.
- **Descriptions are saved on your computer**, in My Photo Library Described's own data folder (`~/Library/Application Support/PhotoFinder` on the Mac; on Windows `%LOCALAPPDATA%\PhotoFinder`, or inside My Photo Library Described's package data on a PC that didn't have that folder before), so each photo is described once.
- **Your photos aren't changed** unless you allow it. Every launch starts in Read-Only Mode. Captions and alt text are saved in your photos only after you turn off File > Read-Only Mode, and then only by commands you choose (Edit Caption, Use Description as Caption) or after you turn on Save Descriptions in Photos, which asks first.
- **Photos in iCloud Photos** (Mac): when you open, preview, export, describe or Get Info a photo that's only in iCloud, macOS downloads it from your own iCloud account, as the Photos app would. Background describing never downloads photos. On Windows, opening, previewing or Get Info on an online-only OneDrive file downloads that file from your OneDrive.

## When My Photo Library Described uses the internet

My Photo Library Described connects to the internet only for these things:

1. **Place names (off until you turn it on).** With Look Up Place Names on, Get Info names the place a photo was taken. To do that, the photo's coordinates (its latitude and longitude, and, as with any web request, your IP address; on Windows also the language Windows uses, so the name comes back in it) are sent to a location service, a photo at a time, only when you open Get Info. Nothing is sent until you turn it on, it asks first, and you can turn it off at any time. Each app uses a different free service:
   - **Mac:** Apple's location service. Apple's privacy policy: https://www.apple.com/legal/privacy/.
   - **Windows:** OpenStreetMap's Nominatim service, run by the OpenStreetMap Foundation. Names it looks up are kept on the PC, so the same spot isn't sent twice. Before asking Nominatim, at most once a day, the app also reads a small settings file from its own GitHub repository (`config/services.json`), so place-name lookups can be paused for everyone without an update if Nominatim asks or the service changes. That request carries nothing about you or your photos beyond what any web request does (your IP address and the app's name and version). GitHub's privacy statement: https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement. Get Info credits OpenStreetMap's contributors with each name, as its licence asks. Nominatim's usage policy: https://operations.osmfoundation.org/policies/nominatim/, and the OpenStreetMap Foundation's privacy policy: https://osmfoundation.org/wiki/Privacy_Policy.
2. **Checking for updates.** On the Mac, My Photo Library Described asks GitHub for the newest release once a day at most, and when you choose Help > Check for Updates. On Windows, Windows' App Installer checks the place My Photo Library Described was installed from (its GitHub releases page) every few hours, and Help > Check for Updates checks it now. The request carries nothing about you or your photos beyond what any web request does (your IP address and the app's name and version). GitHub's privacy statement: https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement.
3. **Reporting a bug.**
   - **On the Mac,** Help > Report a Bug opens a form in the app, with a preview of exactly what will be sent. Nothing is sent until you choose Send. Send posts the report (the kind, the title, what you wrote, the contact if you give one, and, if "Include the system check" is on, the results of Help > Check System without folder paths) to The Idea Place's bug-report relay, a Cloudflare Worker, which files it as a GitHub issue in this app's repository using a bot account, so you don't need a GitHub account. Cloudflare handles the request, so it sees your IP address, which the relay uses only to limit how many reports one address can send; the relay passes the report to GitHub and doesn't keep it. Cloudflare's privacy policy: https://www.cloudflare.com/privacypolicy/. If the relay can't be reached, the app copies the whole report and offers Open in Browser, which opens the prefilled GitHub page, as below, to submit yourself. **Everything in a report is public on GitHub, including the contact.**
   - If the app didn't quit cleanly, the Mac asks once at the next start whether to report it. Only if you choose Report… does it read macOS's crash report for the app (in ~/Library/Logs/DiagnosticReports) and the end of the diagnostic log. From the crash report it takes only the app and macOS versions, the kind of crash and the crashed thread's first 20 steps, never the identifiers macOS records for your Mac; from the log, the last 40 lines; both without folder paths. They go into the form, where you can read them, or leave them out, before sending.
   - **On Windows,** Help > Report a Bug opens a new issue form on GitHub in your web browser, filled in with the results of Help > Check System (the app's version, the operating system and the computer's capabilities, and on Windows the last few description failures, with folder paths left out), and copies the same text to the clipboard. The text travels to GitHub in the page's address so the form can be filled in, but nothing is posted until you submit the issue yourself, and you can edit or remove anything first. **GitHub issues are public.** You need a GitHub account to submit it.
4. **Opening this policy and the user guide** from the Help menu opens them on GitHub in your web browser.

## What's kept on your computer

- Saved descriptions, and which ones you edited.
- Your settings (the description prompt or type, the columns shown, and the switches in the menus and Settings).
- The folders you added to the library.
- On Windows, the place names already looked up, so they aren't sent again.
- A diagnostic log of what went wrong, kept for two weeks (Help > Open Log Folder). On the Mac its lines also go to macOS's own log, which stays on the Mac. It isn't sent anywhere. You can attach it to a bug report if you choose, but it names files and folders, so look at it first: issues are public.

On Windows, all of this is in the data folder, so deleting it removes everything; uninstalling the app leaves it, so a reinstall picks up where you left off (the [user guide](user-guide.md#removing-the-app) says how to remove it too). On the Mac, descriptions and the log are in the data folder, and settings and the list of added folders are in My Photo Library Described's preferences (`~/Library/Preferences`, under its bundle identifier). Your photos are never stored in either.

## Data sharing

My Photo Library Described's developers receive nothing from the app, so there's nothing to share, sell or distribute.

## Children's privacy

My Photo Library Described doesn't knowingly collect information from anyone, including children under 13.

## Changes to this policy

Changes are made in this file, with the "Last Updated" date above.

## Open source

My Photo Library Described is open source. You can review all of its code at https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed.
