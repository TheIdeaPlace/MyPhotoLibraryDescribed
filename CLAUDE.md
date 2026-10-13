# My Photo Library Described

A screen-reader-first, Finder-style browser for photos, built as two separate apps that should **work the same wherever the platform allows**. "My Photo Library Described" is the name for now (Kelly, 13 Oct; it was "Photo Finder", and the repo moved to TheIdeaPlace/MyPhotoLibraryDescribed). It may change again, so don't build the name into anything that's hard to change (#34): identifiers such as the package and bundle IDs, the data folders, the `PhotoFinder` code namespaces and the photofinder-ai alias keep the old name on purpose.

- **Mac:** `PhotoFinder/` (SwiftUI, PhotoKit, Apple Intelligence), tests in `PhotoFinderTests/`, project generated from `project.yml` by XcodeGen.
- **Windows:** `windows/` (WPF, .NET, Windows AI through the packaged `PhotoFinderAI` helper). See `windows/README.md`.

On-device image description (Apple Intelligence on the Mac, Windows AI on Copilot+ PCs) is fast enough on the right hardware to be an everyday feature, not something kept for an explicit command. Design with that in mind.

**`docs/GOALS.md` is Kelly's list of what the app must do**, with where each goal stands; read it before choosing work. `docs/PLATFORM-DIFFERENCES.md` lists every way the two apps differ, and is updated in the same PR as any change that adds or closes a difference.

## Working across two sessions

Work is usually split between a Claude Code session on Kelly's Mac (the **Mac session**) and one on Kelly's Windows PC (the **Windows session**). Remote Control messages between them are unreliable (presence is wrong, connections drop, delivery is never confirmed), so **GitHub is how sessions coordinate**:

1. **At the start of a session**, read the pinned issue **#13 (Coordination)** for comments addressed to you, and list open issues: `gh issue list --label goal`, `gh issue list --label parity` and `gh issue list --label needs-kelly`.
2. **Every task is an issue**, labelled `mac`, `windows` or `both` (a parity gap also gets `parity`). Before starting one, check nobody has claimed it, then comment `Mac session taking this` or `Windows session taking this` and add the `claimed` label. If you stop partway, say so in a comment and remove `claimed`.
3. **Anything that isn't a single task goes in #13**, with a first line like `Mac → Windows: …`, `Windows → Mac: …` or `Mac → Kelly: …`. Reply `ACK <link>` once you've read a comment addressed to you and taken it on. Post there whenever a change affects the other app: shared behaviour or wording read aloud, keyboard shortcuts, description prompts or the saved-description format, or a check only one side can do (the Mac's Xcode 27 build).
4. **One change, both apps.** When a change to one app should also happen in the other, either do both in the same PR, or open an issue for the other side (`parity` plus its platform label) and link it from your PR.
5. **Remote Control messages are only for pings** ("posted on #13, please look"). Acknowledge one with a short reply starting `ACK`. Never rely on a message alone for anything that matters.
6. **When Kelly is needed** (a decision, a VoiceOver/JAWS/NVDA check, a credential), add `needs-kelly` to the issue and say exactly what's needed in a comment.

## Branches, PRs and merging

- Work on a branch, never directly on `main`. Open a PR to `main` with `Closes #N` for the issues it finishes. Kelly merges routine PRs once CI is green.
- Commit and PR titles are one plain sentence about the change as a user would see it, with the PR number appended on merge, for example `Gallery names the photo only; status bar in the F6 loop (Windows and Mac) (#10)`. Prefix with `Mac:` or `Windows:` when only one app changes.
- Pull before starting work. The other session may have merged since you last looked.

## Building and testing

**Mac** (Xcode 27, macOS 27 SDK):

```sh
xcodegen generate
xcodebuild -project PhotoFinder.xcodeproj -scheme PhotoFinder -destination 'platform=macOS,arch=arm64' test
```

The macOS 27 code (Apple Intelligence images, Photos' extended metadata) is fenced with `#if !PF_SDK26`. While the CI runner's SDK is older than macOS 27, Mac CI builds with `-DPF_SDK26` and **doesn't compile that code**. Any PR touching it needs a local Xcode 27 build on the Mac before merging: the Windows session asks for one in #13. `PhotoFinder.xcodeproj` is generated from `project.yml`, not committed (#23): run `xcodegen generate` after cloning, and again after switching branches or pulling (Kelly's Mac has git hooks that do it).

**Windows** (.NET, from `windows/`), in PowerShell or cmd, not Git Bash: tests started from Git Bash get an internal error from the AI helper on every picture (see `windows/README.md`):

```powershell
dotnet build PhotoFinder.sln
dotnet test --solution PhotoFinder.sln
```

Windows AI only works from the packaged helper on a Copilot+ PC, so tests use fakes for it.

## Accessibility is the product

Every change is judged by how it works with VoiceOver on the Mac and with JAWS, NVDA and Narrator on Windows. That means keyboard access for everything, clear names and labels, announcements that are short and not repeated, and focus that lands somewhere sensible. Kelly checks with a screen reader. When a change needs that check, list the exact steps in the PR.
