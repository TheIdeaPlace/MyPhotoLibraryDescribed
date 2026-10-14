import Foundation
import os

/// A plain-text diagnostic log (#103), as on Windows (#98): one file a day in the data folder's Logs folder, kept for
/// two weeks, with what My Photo Library Described started with and what went wrong (Apple Intelligence, describing, background
/// describing, saving, Objective-C exceptions no one caught, which AppKit leaves to it off the main thread). It stays on this Mac unless the person sends it; Help > Open Log Folder
/// shows it. The home folder is written as ~, but file names are kept, so a bug report says to look before attaching
/// it. Each line also goes to the unified log, so Console shows the same. Never throws.
enum DiagnosticLog {
    /// Two weeks of daily files are kept.
    static let daysKept = 14

    private static let lock = NSLock()
    private static let logger = Logger(subsystem: "net.theideaplace.PhotoFinder", category: "diagnostic")

    static var folder: URL {
        AppInfo.dataFolder
            .appendingPathComponent("Logs", isDirectory: true)
    }

    static func fileName(for date: Date) -> String {
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.dateFormat = "yyyy-MM-dd"
        return "PhotoFinder-\(format.string(from: date)).log"
    }

    /// One line: the time, the area ("start", "describe", "background", "save") and what happened, on one line.
    static func line(_ area: String, _ message: String, at date: Date = Date(), home: String = NSHomeDirectory()) -> String {
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.dateFormat = "HH:mm:ss.SSS"
        let text = Privacy.redact(message, home: home)
            .replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ")
            .trimmingCharacters(in: .whitespaces)
        return "\(format.string(from: date)) [\(area)] \(text)"
    }

    /// Under unit tests nothing goes into the person's real log file (#143's review); the unified log still has it.
    private static let testing = ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil

    static func write(_ area: String, _ message: String) {
        let line = line(area, message)
        logger.notice("\(line, privacy: .public)")
        guard !testing else { return }
        lock.lock()
        defer { lock.unlock() }
        let url = folder.appendingPathComponent(fileName(for: Date()))
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let data = Data((line + "\n").utf8)
            if let handle = try? FileHandle(forWritingTo: url) {
                defer { try? handle.close() }
                try handle.seekToEnd()
                try handle.write(contentsOf: data)
            } else {
                try data.write(to: url)
            }
        } catch {
            // A log that can't be written mustn't get in the way, whatever the reason.
        }
    }

    /// What My Photo Library Described started with, and removes files older than two weeks. Run once at launch.
    static func started(appleIntelligence: String) {
        removeOld()
        let os = ProcessInfo.processInfo.operatingSystemVersionString
        write("start", "\(AppInfo.name) \(AppInfo.version) on macOS \(os). Apple Intelligence: \(appleIntelligence)")
        NSSetUncaughtExceptionHandler { exception in
            DiagnosticLog.write("crash", "Uncaught \(exception.name.rawValue): \(exception.reason ?? "") | \(exception.callStackSymbols.prefix(8).joined(separator: " | "))")
        }
    }

    static func removeOld(now: Date = Date()) {
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: folder, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        let cutoff = Calendar.current.date(byAdding: .day, value: -daysKept, to: Calendar.current.startOfDay(for: now))!
        for file in files where file.lastPathComponent.hasPrefix("PhotoFinder-") && file.pathExtension == "log" {
            let modified = (try? file.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
            if let modified, modified < cutoff { try? FileManager.default.removeItem(at: file) }
        }
    }
}
