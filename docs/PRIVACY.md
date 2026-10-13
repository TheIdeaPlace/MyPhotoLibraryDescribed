# Privacy Policy for Photo Finder

**Last Updated: October 12, 2026**

This policy covers both Photo Finder apps: the Mac app and the Windows app. "Photo Finder" is a working name.

## Overview

Photo Finder is a free, open-source photo browser built for screen reader users. It describes your photos on your own computer. It has no accounts, no analytics and no ads, and it doesn't collect, store or send personal information to its developers.

## Your photos stay on your computer

- **Descriptions are made on your computer.** On the Mac, Apple Intelligence describes photos on the Mac itself. On Windows, Windows AI describes them on the PC's NPU (Copilot+ PCs). Your photos aren't uploaded to describe them.
- **Descriptions are saved on your computer**, in Photo Finder's own data folder (`~/Library/Application Support/PhotoFinder` on the Mac; on Windows `%LOCALAPPDATA%\PhotoFinder`, or inside Photo Finder's package data on a PC that didn't have that folder before), so each photo is described once.
- **Your photos aren't changed** unless you allow it. Every launch starts read-only. Captions and alt text are saved in your photos only after you turn off File > Read-Only, and then only by commands you choose (Edit Caption, Use Description as Caption) or after you turn on Save Descriptions in Photos, which asks first.
- **Photos in iCloud Photos** (Mac): when you open, preview, export, describe or Get Info a photo that's only in iCloud, macOS downloads it from your own iCloud account, as the Photos app would. Background describing never downloads photos. On Windows, opening, previewing or Get Info on an online-only OneDrive file downloads that file from your OneDrive.

## When Photo Finder uses the internet

Photo Finder connects to the internet only for these things:

1. **Place names (off until you turn it on).** With Settings > Look Up Place Names on (Mac), Get Info names the place a photo was taken. To do that, the photo's coordinates (its latitude and longitude, nothing else) are sent to Apple's location service. Nothing is sent until you turn it on, and you can turn it off at any time. Apple's privacy policy covers what Apple does with them: https://www.apple.com/legal/privacy/. The Windows app shows coordinates only; if it gains place names, they'll be off until you turn them on in the same way, and this policy will name the service used.
2. **Checking for updates.** On the Mac, Photo Finder asks GitHub for the newest release once a day at most, and when you choose Help > Check for Updates. On Windows, Windows' App Installer checks the place Photo Finder was installed from every few hours, and Help > Check for Updates checks it now. The request carries nothing about you or your photos beyond what any web request does (your IP address and the app's name and version). GitHub's privacy statement: https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement.
3. **Reporting a bug.** Help > Report a Bug opens a new issue form on GitHub in your web browser, filled in with the results of Help > Check System (the app's version, the operating system and the computer's capabilities, and on Windows the last few description failures, with folder paths left out), and copies the same text to the clipboard. The text travels to GitHub in the page's address so the form can be filled in, but nothing is posted until you submit the issue yourself, and you can edit or remove anything first. **GitHub issues are public.** You need a GitHub account to submit it.
4. **Opening this policy and the user guide** from the Help menu (on the Mac, for now) opens them on GitHub in your web browser.

## What's kept on your computer

- Saved descriptions, and which ones you edited.
- Your settings (the description prompt or type, the columns shown, and the switches in the menus and Settings).
- The folders you added to the library.
- On Windows, a diagnostic log of what went wrong, kept for two weeks (Help > Open Log Folder). It isn't sent anywhere. You can attach it to a bug report if you choose, but it names files and folders, so look at it first: issues are public.

On Windows, all of this is in the data folder, so deleting it removes everything. On the Mac, descriptions are in the data folder, and settings and the list of added folders are in Photo Finder's preferences (`~/Library/Preferences`, under its bundle identifier). Your photos are never stored in either.

## Data sharing

Photo Finder's developers receive nothing from the app, so there's nothing to share, sell or distribute.

## Children's privacy

Photo Finder doesn't knowingly collect information from anyone, including children under 13.

## Changes to this policy

Changes are made in this file, with the "Last Updated" date above.

## Open source

Photo Finder is open source. You can review all of its code at https://github.com/kellylford/PhotoFinder.
