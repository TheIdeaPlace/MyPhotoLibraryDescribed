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
        ColumnInfo(id: "location", title: "Location", visibleByDefault: false),
        ColumnInfo(id: "source", title: "Source", visibleByDefault: false),
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

    /// How a column sorts the list.
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
        default: KeyPathComparator(\FileRow.name)
        }
    }

    static func width(_ id: String) -> (min: CGFloat, ideal: CGFloat) {
        switch id {
        case name: (180, 300)
        case "date", "modified": (120, 170)
        case "kind", "fileType": (70, 120)
        case "caption": (100, 220)
        case "keywords", "location": (80, 160)
        case "dimensions": (80, 110)
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
            case "keywords": return "Keywords: \(value)"
            case "favorite": return "Favorite"
            case "modified": return "Modified \(value)"
            case "location": return "Location \(value)"
            default: return value
            }
        }
        return parts.isEmpty ? name : parts.joined(separator: ", ")
    }
}
