# Security policy

## Reporting a vulnerability

**Please don't open a public issue for a security problem.**

Use GitHub's private reporting instead: on the [Security tab](https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/security) of this repository, choose **Report a vulnerability**. If you can't use that, send a private message to [@kellylford](https://github.com/kellylford) on GitHub.

Please include:

- What the problem is, and what someone could do with it
- Steps to reproduce it, or a small proof of concept
- Which app (Mac or Windows) and version, and your operating system and version

You'll get an acknowledgement within a few days. If the report is confirmed, a fix is released as quickly as possible, and you're credited in the release notes unless you'd rather not be.

## What's in scope

My Photo Library Described describes photos on the computer itself. The areas most likely to matter:

- **Privacy.** Photos are never uploaded to be described. Anything that sends photos, descriptions or other personal data off the computer, beyond what [the privacy policy](docs/PRIVACY.md) lists, is in scope.
- **Bug reports.** Help > Report a Bug files a public GitHub issue after showing exactly what will be sent. Anything that sends more than the preview shows is in scope.
- **Updates.** The Windows app installs and updates from this repository's signed releases. Anything that would let it install something else is in scope.
- **Files the app reads.** A crafted photo or metadata file that makes the app run code or write outside its own data folder is in scope.

Problems in Photos, Apple Intelligence, Windows AI or the operating system are out of scope, unless the app makes them worse.
