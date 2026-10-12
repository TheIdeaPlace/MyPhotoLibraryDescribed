import Foundation
import SwiftUI

/// A column in the list. Name is always shown; the rest can be turned on and off and put in any order.
struct ColumnInfo: Identifiable, Hashable, Sendable {
    let id: String
    let title: String
    let visibleByDefault: Bool
}

/// Which columns the list shows and in what order, which is also the order VoiceOver reads each row's cells in.
enum ColumnOrder {
    static let name = "name"

    static let nameColumn = ColumnInfo(id: name, title: "Name", visibleByDefault: true)

    /// Every column except Name, in their original order.
    static let optional: [ColumnInfo] = [
        ColumnInfo(id: "date", title: "Date Taken", visibleByDefault: true),
        ColumnInfo(id: "kind", title: "Kind", visibleByDefault: true),
        ColumnInfo(id: "caption", title: "Caption", visibleByDefault: true),
        ColumnInfo(id: "dimensions", title: "Dimensions", visibleByDefault: true),
        ColumnInfo(id: "duration", title: "Duration", visibleByDefault: true),
        ColumnInfo(id: "favorite", title: "Favorite", visibleByDefault: true),
        ColumnInfo(id: "keywords", title: "Keywords", visibleByDefault: false),
        ColumnInfo(id: "rating", title: "Rating", visibleByDefault: false),
        ColumnInfo(id: "modified", title: "Date Modified", visibleByDefault: false),
        ColumnInfo(id: "fileType", title: "File Type", visibleByDefault: false),
        ColumnInfo(id: "orientation", title: "Orientation", visibleByDefault: false),
        ColumnInfo(id: "megapixels", title: "Megapixels", visibleByDefault: false),
        ColumnInfo(id: "location", title: "Place Taken", visibleByDefault: false),
        ColumnInfo(id: "source", title: "Source", visibleByDefault: false),
        ColumnInfo(id: "description", title: "Description", visibleByDefault: false),
    ]

    static let all: [ColumnInfo] = [nameColumn] + optional

    /// The columns shown until someone chooses otherwise: Name, then the defaults.
    static let defaultOrder: [String] = all.filter(\.visibleByDefault).map(\.id)

    static func info(_ id: String) -> ColumnInfo? { all.first { $0.id == id } }

    static func title(_ id: String) -> String { info(id)?.title ?? id }

    /// A usable order: known columns only, each once, always with Name (put first if missing), falling back
    /// to the defaults when nothing usable is left.
    static func clean(_ ids: [String]?) -> [String] {
        var seen = Set<String>()
        var order = (ids ?? []).filter { info($0) != nil && seen.insert($0).inserted }
        if order.isEmpty { return defaultOrder }
        if !order.contains(name) { order.insert(name, at: 0) }
        return order
    }

    /// The order from before columns could be reordered, when only visibility could be changed.
    static func fromVisibility(_ isVisible: (ColumnInfo) -> Bool?) -> [String] {
        clean(all.filter { $0.id == name || (isVisible($0) ?? $0.visibleByDefault) }.map(\.id))
    }

    /// Carries over the column choices from before columns could be reordered, when SwiftUI's table kept only
    /// which columns were visible. Nil when the data can't be read.
    static func migrate(customization data: Data) -> [String]? {
        guard let saved = try? JSONDecoder().decode(TableColumnCustomization<FileRow>.self, from: data) else { return nil }
        return fromVisibility { column in
            switch saved[visibility: column.id] {
            case .visible: true
            case .hidden: false
            default: nil
            }
        }
    }

    /// How a column sorts the list.
    /// Which way a column sorts when first chosen: dates and sizes read best newest or largest first, text A to Z.
    /// The same columns as Windows' SortBy.
    static func sortsDescendingFirst(_ id: String) -> Bool {
        ["date", "modified", "dimensions", "megapixels", "rating", "favorite", "duration"].contains(id)
    }

    static let sortColumnKey = "sortColumn", sortDescendingKey = "sortDescending"

    /// The sort saved by `saveSort`, or Date Taken, newest first, as Windows starts.
    static func savedSort(_ defaults: UserDefaults) -> [KeyPathComparator<FileRow>] {
        let id = defaults.string(forKey: sortColumnKey).flatMap { info($0)?.id } ?? "date"
        var comparator = comparator(id)
        let descending = defaults.object(forKey: sortDescendingKey) as? Bool ?? (id == "date")
        comparator.order = descending ? .reverse : .forward
        return [comparator]
    }

    static func saveSort(_ order: [KeyPathComparator<FileRow>], to defaults: UserDefaults) {
        guard let id = sortColumn(of: order) else { return }
        defaults.set(id, forKey: sortColumnKey)
        defaults.set(order.first?.order == .reverse, forKey: sortDescendingKey)
    }

    /// The column a sort order is by, or nil if it isn't one of the columns.
    static func sortColumn(of order: [KeyPathComparator<FileRow>]) -> String? {
        guard var first = order.first else { return nil }
        first.order = .forward
        return all.first { comparator($0.id) == first }?.id
    }

    static func comparator(_ id: String) -> KeyPathComparator<FileRow> {
        switch id {
        case "date": KeyPathComparator(\FileRow.sortDate)
        case "kind": KeyPathComparator(\FileRow.kind)
        case "caption": KeyPathComparator(\FileRow.caption)
        case "dimensions": KeyPathComparator(\FileRow.pixelCount)
        case "duration": KeyPathComparator(\FileRow.duration)
        case "favorite": KeyPathComparator(\FileRow.favoriteSort)
        case "keywords": KeyPathComparator(\FileRow.keywords)
        case "rating": KeyPathComparator(\FileRow.rating)
        case "modified": KeyPathComparator(\FileRow.sortModified)
        case "fileType": KeyPathComparator(\FileRow.fileType)
        case "orientation": KeyPathComparator(\FileRow.orientation)
        case "megapixels": KeyPathComparator(\FileRow.megapixels)
        case "location": KeyPathComparator(\FileRow.locationText)
        case "source": KeyPathComparator(\FileRow.source)
        case "description": KeyPathComparator(\FileRow.descriptionText)
        default: KeyPathComparator(\FileRow.name)
        }
    }

    /// The widths the columns had before they could be reordered.
    static func width(_ id: String) -> (min: CGFloat, ideal: CGFloat) {
        switch id {
        case name: (180, 300)
        case "date", "modified": (120, 170)
        case "kind": (80, 130)
        case "caption": (100, 220)
        case "dimensions": (80, 110)
        case "duration": (60, 70)
        case "favorite", "rating": (50, 60)
        case "keywords": (80, 160)
        case "fileType": (70, 110)
        case "orientation": (70, 90)
        case "megapixels": (60, 80)
        case "location": (100, 150)
        case "source": (70, 100)
        case "description": (160, 320)
        default: (50, 80)
        }
    }
}

extension FileRow {
    /// The text a column shows for this row.
    func text(for column: String) -> String {
        switch column {
        case ColumnOrder.name: name
        case "date": dateText
        case "kind": kind
        case "caption": caption
        case "dimensions": dimensions
        case "duration": durationText
        case "favorite": isFavorite ? "Yes" : ""
        case "keywords": keywords
        case "rating": ratingText
        case "modified": modifiedText
        case "fileType": fileType
        case "orientation": orientation
        case "megapixels": megapixelsText
        case "location": locationText
        case "source": source
        case "description": descriptionText
        default: ""
        }
    }

    /// The row as VoiceOver reads it with these columns in this order: the Columns sheet's preview.
    func line(for columns: [String]) -> String {
        let parts = columns.compactMap { id -> String? in
            let value = text(for: id)
            guard !value.isEmpty else { return nil }
            switch id {
            case "caption": return "Caption: \(value)"
            case "description": return "Description: \(value)"
            case "keywords": return "Keywords: \(value)"
            case "favorite": return "Favorite"
            case "modified": return "Modified \(value)"
            case "location": return "Place taken \(value)"
            default: return value
            }
        }
        return parts.isEmpty ? name : parts.joined(separator: ", ")
    }
}
