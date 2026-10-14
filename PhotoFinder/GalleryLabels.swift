import Foundation

/// Something the gallery can say about a photo when you move to it, chosen in View > Labels > Gallery (#38).
enum GalleryItem: String, CaseIterable, Sendable {
    case position, name, kind, date, dimensions, caption, keywords, rating, location

    var title: String {
        switch self {
        case .position: "Position"
        case .name: "Name"
        case .kind: "Kind"
        case .date: "Date Taken"
        case .dimensions: "Dimensions"
        case .caption: "Caption"
        case .keywords: "Keywords"
        case .rating: "Rating"
        case .location: "Place Taken"
        }
    }
}

/// What the gallery says when you move to a photo: the chosen items, in order. The description isn't one of them; it
/// follows after the usual pause when Describe Photos Automatically is on.
enum GalleryLabels {
    /// Position only: "12 of 400", then the description (Kelly, 13 Oct).
    static let defaultOrder: [GalleryItem] = [.position]

    static let defaultsKey = "galleryLabels"

    /// A usable choice: known items only, each once, falling back to the default when nothing usable is left.
    static func clean(_ ids: [String]?) -> [GalleryItem] {
        var seen = Set<GalleryItem>()
        let items = (ids ?? []).compactMap(GalleryItem.init(rawValue:)).filter { seen.insert($0).inserted }
        return items.isEmpty ? defaultOrder : items
    }

    /// The photo as the gallery reads it, for example "12 of 400, IMG_1234.HEIC, Caption: At the beach". `position`
    /// counts from 1. Items with nothing to say are left out; if that leaves nothing, the name is read instead, so
    /// moving to a photo never says nothing at all.
    static func text(for row: FileRow, position: (number: Int, count: Int), items: [GalleryItem]) -> String {
        let parts = items.compactMap { item -> String? in
            let value: String = switch item {
            case .position: "\(position.number) of \(position.count)"
            case .name: row.name
            case .kind: row.kind
            case .date: row.dateText
            case .dimensions: row.dimensions
            case .caption: row.caption.isEmpty ? "" : "Caption: \(row.caption)"
            case .keywords: row.keywords.isEmpty ? "" : "Keywords: \(row.keywords)"
            case .rating: row.ratingText
            case .location: row.placeTakenText.isEmpty ? "" : "Place taken \(row.placeTakenText)"
            }
            return value.isEmpty ? nil : value
        }
        return parts.isEmpty ? row.name : parts.joined(separator: ", ")
    }
}
