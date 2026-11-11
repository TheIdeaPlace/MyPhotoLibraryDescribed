# Contributing to My Photo Library Described

Thank you for wanting to help. My Photo Library Described is two apps, one for the Mac and one for Windows, that should work the same wherever the platform allows. Both are built to be used with a screen reader, so every change is judged by how it works with VoiceOver on the Mac and with JAWS, NVDA and Narrator on Windows.

## Before you start

For anything bigger than a small fix, **open an issue first** and say what you'd like to do. [`docs/GOALS.md`](docs/GOALS.md) lists what the app has to do and where each goal stands, and [issues labelled v-next](https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/issues?q=is%3Aissue+is%3Aopen+label%3Av-next) are what's already planned.

## Getting started

1. Fork the repository and create a branch from `main`.
2. Read the "For developers" section of the [README](README.md), and [`CLAUDE.md`](CLAUDE.md) for the conventions.
3. Code names keep `PhotoFinder` on purpose (namespaces, folders, projects and executables), so leave them out of any find-and-replace.

## Building and testing

**Mac** (Xcode and [XcodeGen](https://github.com/yonaskolb/XcodeGen)). The Xcode project isn't in the repository, so generate it after cloning, and again after switching branches or pulling:

```sh
xcodegen generate
xcodebuild -project PhotoFinder.xcodeproj -scheme PhotoFinder -destination 'platform=macOS,arch=arm64' test
```

Code for macOS 27 (Apple Intelligence and Photos' extended metadata) is inside `#if !PF_SDK26`. CI doesn't compile it yet, so say in your pull request if you changed it.

**Windows** (.NET 10 SDK). From `windows/`, in PowerShell or cmd, not Git Bash:

```powershell
dotnet build PhotoFinder.sln
dotnet test --solution PhotoFinder.sln
```

Windows AI only works on a Copilot+ PC, so tests use fakes for it. New features and fixes should come with tests.

## One change, both apps

When a change to one app should also happen in the other, either do both in the same pull request, or open an issue for the other app and link it. A change that adds or removes a difference between the apps also updates [`docs/PLATFORM-DIFFERENCES.md`](docs/PLATFORM-DIFFERENCES.md).

## Accessibility

- Everything works from the keyboard.
- Controls have clear names and labels.
- Announcements are short and aren't repeated.
- Focus lands somewhere sensible after every action.

If your change needs a screen reader check, list the exact steps in the pull request.

## Privacy

Photos are described on the computer and are never uploaded. Don't add anything that sends photos, descriptions or personal data over the internet. Anything new that the app sends at all needs to be listed in [`docs/PRIVACY.md`](docs/PRIVACY.md) in the same pull request.

## Commits and pull requests

- Commit and pull request titles are one plain sentence about the change as a user would see it. Start with `Mac:` or `Windows:` when only one app changes.
- Keep a pull request to one feature or fix, and link the issue it closes (`Closes #N`).
- CI must be green before a pull request is merged.

## Questions

Open an issue. We're happy to help you find your way around.
