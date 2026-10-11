import Foundation

/// The places F6 and Shift-F6 move between, in order: the sidebar, the list or gallery, Search, then the status bar.
enum Pane: Hashable {
    case sidebar, content, search
    case status(StatusSection)

    static let ring: [Pane] = [.sidebar, .content, .search, .status(.status)]

    /// Where F6 (direction 1) or Shift-F6 (-1) goes from `current`. Anywhere in the status bar counts as the status bar;
    /// nowhere in particular counts as the list or gallery.
    static func next(from current: Pane?, by direction: Int) -> Pane {
        let at: Int
        switch current {
        case .status: at = 3
        case let pane?: at = ring.firstIndex(of: pane) ?? 1
        case nil: at = 1
        }
        return ring[(at + direction + ring.count) % ring.count]
    }
}

/// The status bar's sections, each its own stop; Left and Right Arrow move between those with something to say.
enum StatusSection: Int, CaseIterable, Hashable {
    case status, activity, background

    /// The section Left (-1) or Right (1) goes to from `current`, wrapping, among `shown`.
    static func next(from current: StatusSection, by direction: Int, shown: [StatusSection]) -> StatusSection {
        guard !shown.isEmpty else { return .status }
        guard let at = shown.firstIndex(of: current) else { return shown[0] }
        return shown[(at + direction + shown.count) % shown.count]
    }
}

/// A request from the Go menu to move to the next or previous pane. The serial makes each request a change.
struct PaneRequest: Equatable {
    var direction = 0
    var serial = 0
}
