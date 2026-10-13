import UncloudKit
import UniformTypeIdentifiers

/// What a file looks like in a list, until there are thumbnails: an icon for its kind, told from
/// its extension.
enum FileKind {
    static func symbol(for entry: LibraryEntry) -> String {
        if entry.isDirectory { return "folder.fill" }
        guard !entry.fileExtension.isEmpty, let type = UTType(filenameExtension: entry.fileExtension) else { return "doc" }
        if type.conforms(to: .image) { return "photo" }
        if type.conforms(to: .movie) { return "film" }
        if type.conforms(to: .audio) { return "waveform" }
        if type.conforms(to: .pdf) { return "doc.richtext" }
        if type.conforms(to: .spreadsheet) { return "tablecells" }
        if type.conforms(to: .presentation) { return "rectangle.on.rectangle" }
        if type.conforms(to: .archive) { return "doc.zipper" }
        if type.conforms(to: .sourceCode) { return "chevron.left.forwardslash.chevron.right" }
        if type.conforms(to: .text) { return "doc.text" }
        return "doc"
    }
}
