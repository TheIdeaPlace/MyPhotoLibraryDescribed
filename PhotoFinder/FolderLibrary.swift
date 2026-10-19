import Foundation

// Folders added to the library from outside Photos (#36): File > Add Folder to Library…, as on Windows. Everything
// here is plain file-system logic with no PhotoKit, so it's tested on its own.

/// Which files in an added folder count as photos and videos, and what kind each is. The same extensions and kinds
/// as Windows' MediaTypes, so a folder lists the same files on both.
enum MediaFiles {
    static let rawExtensions: Set<String> = [
        "dng", "cr2", "cr3", "nef", "nrw", "arw", "srf", "sr2", "orf", "rw2", "raf", "pef", "srw", "x3f", "3fr", "iiq", "rwl",
    ]
    static let imageExtensions: Set<String> = [
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "heic", "heif", "hif", "avif", "webp",
        "jxl", "ico",
    ]
    static let videoExtensions: Set<String> = [
        "mp4", "m4v", "mov", "avi", "wmv", "mkv", "webm", "3gp", "3g2", "mts", "m2ts", "mpg", "mpeg",
    ]

    private static func ext(_ name: String) -> String { (name as NSString).pathExtension.lowercased() }

    static func isMedia(_ name: String) -> Bool {
        let e = ext(name)
        return imageExtensions.contains(e) || videoExtensions.contains(e) || rawExtensions.contains(e)
    }

    static func isVideo(_ name: String) -> Bool { videoExtensions.contains(ext(name)) }

    static func isRaw(_ name: String) -> Bool { rawExtensions.contains(ext(name)) }

    /// The Kind column, as on Windows: Video, Screen Recording, Screenshot, RAW Photo, Animated Image, Panorama
    /// (by shape) or Photo. macOS names screenshots "Screenshot …" (older versions "Screen Shot …").
    static func kind(name: String, width: Int, height: Int) -> String {
        if isVideo(name) { return name.hasPrefix("Screen Recording") ? "Screen Recording" : "Video" }
        if name.hasPrefix("Screenshot") || name.hasPrefix("Screen Shot") { return "Screenshot" }
        if isRaw(name) { return "RAW Photo" }
        if ext(name) == "gif" { return "Animated Image" }
        if width > 0, height > 0, Double(max(width, height)) / Double(min(width, height)) >= 2.5 { return "Panorama" }
        return "Photo"
    }
}

/// Ids for folders and files, kept apart from Photos' identifiers and album folders ("folder:…").
enum FolderID {
    static func folder(_ path: String) -> String { "dir:" + path }
    static func file(_ path: String) -> String { "file:" + path }
}

/// The key a file's description is saved under in the description cache, next to Photos' identifiers:
/// `file:<PATH IN CAPITALS>|<size in bytes>|<modified time in .NET ticks, UTC>`. The same parts as Windows'
/// DescriptionStore, path capitalized like its keys since Mac disks usually ignore case too (without its
/// description-kind suffix, since the Mac has one prompt), so an edited or replaced file is described
/// again while a file left alone keeps its description. Ticks are 100-nanosecond steps since 1 January 0001.
enum FileDescriptionKey {
    static func key(path: String, size: Int64, modified: Date) -> String {
        "file:\(path.uppercased())|\(size)|\(ticks(modified))"
    }

    static func ticks(_ date: Date) -> Int64 {
        let interval = date.timeIntervalSince1970
        let seconds = interval.rounded(.down)
        return 621_355_968_000_000_000 + Int64(seconds) * 10_000_000 + Int64(((interval - seconds) * 10_000_000).rounded())
    }
}

/// The folders added to the library, remembered by path. The app isn't sandboxed, so a path is all it needs to read
/// a folder again next time (no security-scoped bookmarks).
enum FolderRoots {
    static let defaultsKey = "addedFolders"

    /// An absolute path without a trailing slash or `..`, so the same folder is always written the same way.
    static func normalize(_ path: String) -> String {
        var standardized = (path as NSString).standardizingPath
        while standardized.count > 1, standardized.hasSuffix("/") { standardized.removeLast() }
        return standardized
    }

    /// Mac disks usually ignore case, so folders are compared without it, as on Windows.
    static func same(_ a: String, _ b: String) -> Bool { a.caseInsensitiveCompare(b) == .orderedSame }

    static func isInside(_ path: String, _ folder: String) -> Bool {
        let prefix = folder == "/" ? "/" : folder + "/"
        return path.count > prefix.count && path.lowercased().hasPrefix(prefix.lowercased())
    }

    /// The root that `path` is or is inside, if any.
    static func root(containing path: String, in roots: [String]) -> String? {
        roots.first { same($0, path) || isInside(path, $0) }
    }

    /// The list after adding a folder. A folder already in the library (or inside one) changes nothing; a folder
    /// holding ones already added takes their place, so no folder is listed twice.
    static func adding(_ path: String, to roots: [String]) -> (roots: [String], added: Bool) {
        let folder = normalize(path)
        guard root(containing: folder, in: roots) == nil else { return (roots, false) }
        return (roots.filter { !isInside($0, folder) } + [folder], true)
    }

    static func removing(_ path: String, from roots: [String]) -> [String] {
        roots.filter { !same($0, path) }
    }

    static func load(from defaults: UserDefaults = .standard) -> [String] {
        (defaults.stringArray(forKey: defaultsKey) ?? []).reduce(into: []) { roots, path in
            roots = adding(path, to: roots).roots
        }
    }

    static func save(_ roots: [String], to defaults: UserDefaults = .standard) {
        defaults.set(roots, forKey: defaultsKey)
    }

    /// The folder's name, for announcements and the sidebar.
    static func displayName(_ path: String) -> String {
        let name = (path as NSString).lastPathComponent
        return name.isEmpty || name == "/" ? path : name
    }

    /// Where to go when the folder showing drops out of the sidebar (its last photo deleted in Finder): the nearest
    /// folder above it that's still listed, up to and including its added folder. Nil when none is (the added folder
    /// itself was removed), so the caller falls back to All Photos.
    static func nearestListed(above path: String, roots: [String], isListed: (String) -> Bool) -> String? {
        guard let root = root(containing: path, in: roots) else { return nil }
        var folder = path
        while !same(folder, root) {
            folder = (folder as NSString).deletingLastPathComponent
            if isListed(folder) { return folder }
            if folder.isEmpty || folder == "/" { break }
        }
        return nil
    }

    /// The sidebar place to show instead of one that's gone (#47): the nearest folder above `previousPath` that's still
    /// listed; otherwise All Photos, or without Photos access the first added folder still listed, and failing that the
    /// "Photos library: no access" row.
    static func fallbackPlace(previousPath: String?, roots: [String], photosAllowed: Bool,
                              isListed: (String) -> Bool) -> String {
        if let path = previousPath, let parent = nearestListed(above: path, roots: roots, isListed: isListed) {
            return FolderID.folder(parent)
        }
        if photosAllowed { return SidebarID.allPhotos }
        if let root = roots.first(where: isListed) { return FolderID.folder(root) }
        return SidebarID.photosNoAccess
    }
}

/// Which photos in an added folder to describe in the background after it's scanned (#47): every photo in a folder
/// just added (as Windows does), and afterwards the photos that weren't there at the last scan. The first scan after
/// launch only sets the baseline, so opening My Photo Library Described doesn't describe whole folders again. Videos aren't
/// described.
enum FolderChanges {
    static func photosToDescribe(before: Set<String>?, after: [String], justAdded: Bool) -> [String] {
        let photos = after.filter { !MediaFiles.isVideo(($0 as NSString).lastPathComponent) }
        if justAdded { return photos }
        guard let before else { return [] }
        return photos.filter { !before.contains($0) }
    }
}

/// One scan of an added folder: its sidebar tree, every photo and video path in it (nil when the folder isn't
/// there, an unplugged disk, so plugging it back in doesn't look like every photo was just added), and the photos
/// to describe in the background.
struct FolderScan: Sendable {
    let node: FolderNode
    let paths: Set<String>?
    let newPhotos: [String]

    static func scan(_ root: String, before: Set<String>?, justAdded: Bool) -> FolderScan {
        var isFolder: ObjCBool = false
        guard FileManager.default.fileExists(atPath: root, isDirectory: &isFolder), isFolder.boolValue else {
            return FolderScan(node: FolderScanner.tree(root: root, mediaPaths: []), paths: nil, newPhotos: [])
        }
        let paths = FolderScanner.mediaPaths(under: root)
        return FolderScan(node: FolderScanner.tree(root: root, mediaPaths: paths), paths: Set(paths),
                          newPhotos: FolderChanges.photosToDescribe(before: before, after: paths, justAdded: justAdded))
    }
}

/// A photo or video file in an added folder, with what the file system says about it. Its metadata comes later.
struct MediaFile: Hashable, Sendable {
    let path: String
    let size: Int64
    let created: Date?
    let modified: Date

    var url: URL { URL(fileURLWithPath: path) }
    var name: String { (path as NSString).lastPathComponent }
    var folder: String { (path as NSString).deletingLastPathComponent }
    var isVideo: Bool { MediaFiles.isVideo(name) }
    var descriptionKey: String { FileDescriptionKey.key(path: path, size: size, modified: modified) }
}

/// A folder in the sidebar's Folders section: how many photos and videos are in it and below it, and the subfolders
/// that have any.
struct FolderNode: Hashable, Sendable {
    let path: String
    var count: Int
    var children: [FolderNode]
}

enum FolderScanner {
    private static let fileKeys: [URLResourceKey] = [
        .isRegularFileKey, .fileSizeKey, .creationDateKey, .contentModificationDateKey,
    ]

    /// Every photo and video under the folder, skipping hidden files and packages: a Pictures folder usually holds
    /// the Photos library, which is already browsed through Photos.
    static func mediaPaths(under root: String) -> [String] {
        guard let enumerator = FileManager.default.enumerator(
            at: URL(fileURLWithPath: root, isDirectory: true), includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles, .skipsPackageDescendants]) else { return [] }
        // The enumerator hands back paths with links resolved (/var becomes /private/var); they're put back under the
        // root as it was added, so they match the sidebar's folders.
        let resolved = URL(fileURLWithPath: root).resolvingSymlinksInPath().path
        var paths: [String] = []
        for case let url as URL in enumerator {
            // A newer scan of this folder replaced this one: stop rather than walk a big folder for nothing.
            if Task.isCancelled { return [] }
            guard MediaFiles.isMedia(url.lastPathComponent),
                  (try? url.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true else { continue }
            let path = url.path
            for base in [root, resolved, "/private" + resolved] where FolderRoots.isInside(path, base) {
                paths.append(root + path.dropFirst(base.count))
                break
            }
        }
        return paths
    }

    /// The folder tree for a root, as Windows builds it: a subfolder is shown only when it has photos or videos
    /// somewhere inside, and each folder's count is everything in it and below. The root itself is always there, even
    /// when empty or missing (an unplugged disk), so it can still be removed.
    static func tree(root: String, mediaPaths: [String]) -> FolderNode {
        var totals: [String: Int] = [:]
        var childFolders: [String: Set<String>] = [:]
        for path in mediaPaths where FolderRoots.isInside(path, root) {
            var folder = (path as NSString).deletingLastPathComponent
            while true {
                totals[folder, default: 0] += 1
                if folder == root || !FolderRoots.isInside(folder, root) { break }
                let parent = (folder as NSString).deletingLastPathComponent
                childFolders[parent, default: []].insert(folder)
                folder = parent
            }
        }
        func node(_ path: String) -> FolderNode {
            let children = (childFolders[path] ?? []).sorted {
                ($0 as NSString).lastPathComponent.localizedStandardCompare(($1 as NSString).lastPathComponent) == .orderedAscending
            }
            return FolderNode(path: path, count: totals[path] ?? 0, children: children.map(node))
        }
        return node(root)
    }

    static func scan(_ root: String) -> FolderNode { tree(root: root, mediaPaths: mediaPaths(under: root)) }
    /// The photos and videos directly in a folder (not its subfolders), from the file system alone, which is fast.
    static func files(in folder: String) -> [MediaFile] {
        guard let urls = try? FileManager.default.contentsOfDirectory(
            at: URL(fileURLWithPath: folder, isDirectory: true), includingPropertiesForKeys: fileKeys,
            options: [.skipsHiddenFiles]) else { return [] }
        return urls.compactMap { url in
            guard MediaFiles.isMedia(url.lastPathComponent),
                  let values = try? url.resourceValues(forKeys: Set(fileKeys)), values.isRegularFile == true else { return nil }
            return MediaFile(path: (folder as NSString).appendingPathComponent(url.lastPathComponent),
                             size: Int64(values.fileSize ?? 0), created: values.creationDate,
                             modified: values.contentModificationDate ?? values.creationDate ?? .distantPast)
        }
    }

    /// One file as the file system has it now, after My Photo Library Described wrote into it (#31).
    static func file(at path: String) -> MediaFile? {
        guard let values = try? URL(fileURLWithPath: path).resourceValues(forKeys: Set(fileKeys)),
              values.isRegularFile == true else { return nil }
        return MediaFile(path: path, size: Int64(values.fileSize ?? 0), created: values.creationDate,
                         modified: values.contentModificationDate ?? values.creationDate ?? .distantPast)
    }

    /// The Folders section's items. Roots are named for their folder, or by their path when two share a name.
    static func sidebarItems(_ roots: [FolderNode]) -> [SidebarItem] {
        let names = roots.map { FolderRoots.displayName($0.path) }
        func item(_ node: FolderNode, title: String, parentID: String?) -> SidebarItem {
            let id = FolderID.folder(node.path)
            let children = node.children.map { item($0, title: FolderRoots.displayName($0.path), parentID: id) }
            return SidebarItem(id: id, title: title, kind: .directory(node.path), systemImage: "folder", count: node.count,
                               children: children.isEmpty ? nil : children, parentID: parentID, containerKind: "Folder")
        }
        return roots.enumerated().map { index, root in
            let shared = names.filter { $0.localizedCaseInsensitiveCompare(names[index]) == .orderedSame }.count > 1
            return item(root, title: shared ? (root.path as NSString).abbreviatingWithTildeInPath : names[index], parentID: nil)
        }
    }
}
