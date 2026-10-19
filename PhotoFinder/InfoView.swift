import CoreLocation
import ImageIO
import MapKit
import Photos
import SwiftUI
import UniformTypeIdentifiers
import Vision

struct InfoField: Identifiable {
    let id = UUID()
    let label: String
    let value: String
}

struct InfoView: View {
    let target: InfoTarget
    @Environment(\.dismiss) private var dismiss
    @State private var general: [InfoField] = []
    @State private var details: [InfoField] = []
    @State private var loadingDetails = true
    @State private var imageData: Data?
    @State private var contents: ImageContents?
    @State private var describing = false
    @State private var describeError: String?

    var body: some View {
        VStack(spacing: 0) {
            Text(target.name)
                .font(.title2.bold())
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding([.horizontal, .top])
                .accessibilityAddTraits(.isHeader)
            Form {
                Section("General") { fields(general) }
                Section("Details") {
                    if loadingDetails {
                        ProgressView("Reading details…")
                    } else if details.isEmpty {
                        Text("No additional details available.").foregroundStyle(.secondary)
                    } else {
                        fields(details)
                    }
                }
                if target.isImage {
                    Section("Image Contents") { contentsSection }
                }
            }
            .formStyle(.grouped)
            Divider()
            HStack {
                Spacer()
                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding()
        }
        .frame(minWidth: 460, idealWidth: 520, minHeight: 420, idealHeight: 620)
        .closesWithKeyboard { dismiss() }
        .task { await load() }
    }

    @ViewBuilder private func fields(_ items: [InfoField]) -> some View {
        ForEach(items) { field in
            LabeledContent(field.label) {
                Text(field.value)
                    .textSelection(.enabled)
                    .multilineTextAlignment(.trailing)
            }
        }
    }

    @ViewBuilder private var contentsSection: some View {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            AppleIntelligenceSection(imageData: imageData, descriptionKey: target.descriptionKey,
                                     askNow: target.askNow)
        }
#endif
        if let contents {
            LabeledContent("Detected") {
                Text(contents.labels.isEmpty ? "Nothing recognized" : contents.labels.joined(separator: ", "))
                    .textSelection(.enabled)
                    .multilineTextAlignment(.trailing)
            }
            LabeledContent("Text in Image") {
                Text(contents.text.isEmpty ? "No text found" : contents.text.joined(separator: "\n"))
                    .textSelection(.enabled)
                    .multilineTextAlignment(.trailing)
            }
        } else if describing {
            ProgressView("Finding objects and text…")
        } else {
            Button("Find Objects and Text") { Task { await describe() } }
                .disabled(imageData == nil)
            Text("Lists objects and scenes, and reads any text in the photo, using on-device Vision.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        if let describeError {
            Text(describeError).foregroundStyle(.red)
        }
    }

    private func load() async {
        let result: (fields: [InfoField], imageData: Data?)
        switch target.source {
        case .asset(let asset):
            general = PhotoMetadata.general(for: asset, name: target.name)
            result = await PhotoMetadata.details(for: UncheckedBox(value: asset))
        case .file(let file, let known):
            let metadata = await PhotoMetadata.metadata(of: file, known: known)
            general = PhotoMetadata.general(for: file, row: FileRow(file: file, metadata: metadata))
            result = await PhotoMetadata.details(for: file, metadata: metadata)
        }
        details = result.fields
        imageData = result.imageData
        loadingDetails = false
    }

    private func describe() async {
        guard let imageData else { return }
        describing = true
        describeError = nil
        do {
            let found = try await ImageDescriber.describe(imageData)
            contents = found
            var summary = found.labels.isEmpty ? "Nothing recognized" : "Detected " + found.labels.joined(separator: ", ")
            if !found.text.isEmpty { summary += ". Text: " + found.text.joined(separator: " ") }
            AccessibilityNotification.Announcement(summary).post()
        } catch {
            describeError = error.localizedDescription
        }
        describing = false
    }
}

enum PhotoMetadata {
    static func general(for asset: PHAsset, name: String) -> [InfoField] {
        var fields = [
            InfoField(label: "Name", value: name),
            InfoField(label: "Kind", value: FileRow.kindDescription(for: asset)),
        ]
        if let date = asset.creationDate {
            fields.append(InfoField(label: "Date Taken", value: date.formatted(date: .complete, time: .standard)))
        }
        if let date = asset.modificationDate {
            fields.append(InfoField(label: "Modified", value: date.formatted(date: .complete, time: .standard)))
        }
        if asset.pixelWidth > 0 {
            fields.append(InfoField(label: "Dimensions", value: "\(asset.pixelWidth) × \(asset.pixelHeight) pixels"))
        }
        if asset.mediaType == .video {
            fields.append(InfoField(label: "Duration", value: FileRow.durationText(asset.duration)))
        }
        fields.append(InfoField(label: "Favorite", value: asset.isFavorite ? "Yes" : "No"))
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            let metadata = asset.extendedMetadata
            fields.append(InfoField(label: "Caption", value: metadata.caption ?? "None"))
            if !metadata.keywords.isEmpty {
                fields.append(InfoField(label: "Keywords", value: metadata.keywords.joined(separator: ", ")))
            }
            let rating = asset.rating.rawValue
            if rating > 0 {
                fields.append(InfoField(label: "Rating", value: rating == 1 ? "1 star" : "\(rating) stars"))
            }
        }
#endif
        if let location = asset.location {
            let c = location.coordinate
            fields.append(InfoField(label: "Coordinates", value: String(format: "%.5f, %.5f", c.latitude, c.longitude)))
        }
        return fields
    }

    static func details(for box: UncheckedBox<PHAsset>) async -> (fields: [InfoField], imageData: Data?) {
        let asset = box.value
        var fields: [InfoField] = []
        if let location = asset.location, let place = await placeName(for: location) {
            fields.append(InfoField(label: "Place Taken", value: place))
        }
        var imageData: Data?
        if asset.mediaType == .image, let (data, uti) = await PhotoData.request(box.value, allowNetwork: true) {
            imageData = data
            if let uti, let type = UTType(uti) {
                fields.append(InfoField(label: "File Type", value: type.localizedDescription ?? uti))
            }
            fields.append(InfoField(label: "File Size", value: ByteCountFormatter.string(fromByteCount: Int64(data.count), countStyle: .file)))
            fields += exifFields(data)
        } else if let resource = PhotoExporter.primaryResource(for: asset), let type = UTType(resource.uniformTypeIdentifier) {
            fields.append(InfoField(label: "File Type", value: type.localizedDescription ?? resource.uniformTypeIdentifier))
        }
        return (fields, imageData)
    }


    /// Get Info for a file in an added folder (#36): what the list knows about it, and where it is.
    static func general(for file: MediaFile, row: FileRow) -> [InfoField] {
        var fields = [
            InfoField(label: "Name", value: row.name),
            InfoField(label: "Kind", value: row.kind),
        ]
        if let date = row.date {
            fields.append(InfoField(label: "Date Taken", value: date.formatted(date: .complete, time: .standard)))
        }
        fields.append(InfoField(label: "Modified", value: file.modified.formatted(date: .complete, time: .standard)))
        if !row.dimensions.isEmpty {
            fields.append(InfoField(label: "Dimensions", value: "\(row.dimensions) pixels"))
        }
        if !row.durationText.isEmpty {
            fields.append(InfoField(label: "Duration", value: row.durationText))
        }
        fields.append(InfoField(label: "Caption", value: row.caption.isEmpty ? "None" : row.caption))
        // IPTC's accessibility fields, which Use Description as Caption writes (#31), or another app did.
        if !row.altText.isEmpty {
            fields.append(InfoField(label: "Alt text in the file", value: row.altText))
        }
        if !row.extendedDescription.isEmpty {
            fields.append(InfoField(label: "Extended description in the file", value: row.extendedDescription))
        }
        if !row.keywords.isEmpty {
            fields.append(InfoField(label: "Keywords", value: row.keywords))
        }
        if !row.ratingText.isEmpty {
            fields.append(InfoField(label: "Rating", value: row.ratingText))
        }
        if !row.locationText.isEmpty {
            fields.append(InfoField(label: "Coordinates", value: row.locationText))
        }
        fields.append(InfoField(label: "Where", value: (file.folder as NSString).abbreviatingWithTildeInPath))
        return fields
    }

    /// A file's metadata: what the list already read, or read now off the main actor.
    static func metadata(of file: MediaFile, known: FileMetadata?) async -> FileMetadata {
        if let known { return known }
        let url = file.url
        if file.isVideo { return await FileMetadata.readVideo(url) }
        return await Task.detached(priority: .userInitiated) { FileMetadata.read(url) }.value
    }

    /// The slower details of a file: place name, type, size and camera settings, and its data for describing.
    static func details(for file: MediaFile, metadata: FileMetadata) async -> (fields: [InfoField], imageData: Data?) {
        var fields: [InfoField] = []
        let url = file.url
        let isVideo = file.isVideo
        if let lat = metadata.latitude, let lon = metadata.longitude,
           let place = await placeName(for: CLLocation(latitude: lat, longitude: lon)) {
            fields.append(InfoField(label: "Place Taken", value: place))
        }
        if let type = UTType(filenameExtension: url.pathExtension) {
            fields.append(InfoField(label: "File Type", value: type.localizedDescription ?? url.pathExtension.uppercased()))
        }
        fields.append(InfoField(label: "File Size", value: ByteCountFormatter.string(fromByteCount: file.size, countStyle: .file)))
        guard !isVideo else { return (fields, nil) }
        let data = await Task.detached { try? Data(contentsOf: url, options: .mappedIfSafe) }.value
        if let data { fields += exifFields(data) }
        return (fields, data)
    }

    /// The place's name, only when Look Up Place Names is on: finding it sends the coordinates to Apple (#105). A name
    /// already found (here, or for the list's Place Taken column) is said without asking again, and one found now is
    /// kept (#172).
    private static func placeName(for location: CLLocation) async -> String? {
        guard PlaceNames.isOn(in: .standard),
              let key = PlaceKey(latitude: location.coordinate.latitude, longitude: location.coordinate.longitude) else { return nil }
        return await PlaceNameCache.shared.lookUp(key, using: PlaceNames.geocode)
    }

    private static func exifFields(_ data: Data) -> [InfoField] {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] else { return [] }
        let tiff = props[kCGImagePropertyTIFFDictionary] as? [CFString: Any] ?? [:]
        let exif = props[kCGImagePropertyExifDictionary] as? [CFString: Any] ?? [:]
        var fields: [InfoField] = []

        let make = tiff[kCGImagePropertyTIFFMake] as? String
        if let model = tiff[kCGImagePropertyTIFFModel] as? String {
            let camera = if let make, !model.hasPrefix(make) { "\(make) \(model)" } else { model }
            fields.append(InfoField(label: "Camera", value: camera))
        }
        if let lens = exif[kCGImagePropertyExifLensModel] as? String {
            fields.append(InfoField(label: "Lens", value: lens))
        }
        if let focal = exif[kCGImagePropertyExifFocalLength] as? Double {
            fields.append(InfoField(label: "Focal Length", value: "\(focal.formatted(.number.precision(.fractionLength(0...1)))) mm"))
        }
        if let fNumber = exif[kCGImagePropertyExifFNumber] as? Double {
            fields.append(InfoField(label: "Aperture", value: "f/\(fNumber.formatted(.number.precision(.fractionLength(0...1))))"))
        }
        if let exposure = exif[kCGImagePropertyExifExposureTime] as? Double, exposure > 0 {
            let text = exposure < 1 ? "1/\(Int((1 / exposure).rounded())) second" : "\(exposure.formatted()) seconds"
            fields.append(InfoField(label: "Shutter Speed", value: text))
        }
        if let iso = (exif[kCGImagePropertyExifISOSpeedRatings] as? [Int])?.first {
            fields.append(InfoField(label: "ISO", value: String(iso)))
        }
        if let flash = exif[kCGImagePropertyExifFlash] as? Int {
            fields.append(InfoField(label: "Flash", value: flash & 1 == 1 ? "Fired" : "Did not fire"))
        }
        if let software = tiff[kCGImagePropertyTIFFSoftware] as? String {
            fields.append(InfoField(label: "Software", value: software))
        }
        return fields
    }
}

struct ImageContents: Sendable {
    let labels: [String]
    let text: [String]
}

enum ImageDescriber {
    static func describe(_ data: Data) async throws -> ImageContents {
        try await Task.detached(priority: .userInitiated) {
            let handler = VNImageRequestHandler(data: data, options: [:])
            let classify = VNClassifyImageRequest()
            let recognize = VNRecognizeTextRequest()
            recognize.recognitionLevel = .accurate
            recognize.usesLanguageCorrection = true
            try handler.perform([classify, recognize])

            let labels = (classify.results ?? [])
                .filter { $0.confidence >= 0.3 }
                .sorted { $0.confidence > $1.confidence }
                .prefix(15)
                .map { $0.identifier.replacingOccurrences(of: "_", with: " ") }
            let lines = (recognize.results ?? []).compactMap { $0.topCandidates(1).first?.string }
            return ImageContents(labels: Array(labels), text: lines)
        }.value
    }
}
