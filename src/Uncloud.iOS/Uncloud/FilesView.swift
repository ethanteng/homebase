import QuickLook
import SwiftUI
import UncloudKit

/// A folder in My files, as the navigation stack holds it.
struct Folder: Hashable {
    let path: String
    let name: String

    static let top = Folder(path: "", name: "My files")
}

/// My files: a folder per screen, a file opened in Quick Look, and the account behind the button.
struct FilesView: View {
    let account: AppModel.Account
    @Environment(AppModel.self) private var model
    @State private var folders: [Folder] = []
    @State private var showingAccount = false

    var body: some View {
        @Bindable var model = model
        NavigationStack(path: $folders) {
            FolderView(folder: .top)
                .toolbar {
                    ToolbarItem(placement: .topBarLeading) {
                        Button {
                            showingAccount = true
                        } label: {
                            Label("Account", systemImage: "person.crop.circle")
                        }
                    }
                }
                .navigationDestination(for: Folder.self) { folder in
                    FolderView(folder: folder)
                }
        }
        .safeAreaInset(edge: .bottom) {
            if let opening = model.opening {
                DownloadBanner(opening: opening) { model.cancelOpening() }
            }
        }
        .quickLookPreview($model.preview)
        .alert("Can’t Open This File", isPresented: $model.showingOpeningFailure, presenting: model.openingFailure) { _ in
            Button("OK", role: .cancel) {}
        } message: { failure in
            Text(failure)
        }
        .sheet(isPresented: $showingAccount) {
            AccountView(account: account)
        }
    }
}

struct FolderView: View {
    let folder: Folder
    @Environment(AppModel.self) private var model
    @State private var listing: DirectoryListing?
    @State private var failure: String?
    @State private var filter = ""
    @AppStorage("sort") private var sort: Sort = .name

    var body: some View {
        List {
            // A folder already on screen stays there when asking again fails; this says why it may be out of date.
            if let failure, listing != nil {
                Label(failure, systemImage: "exclamationmark.triangle")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            Section {
                ForEach(shown) { entry in
                    if entry.isDirectory {
                        NavigationLink(value: Folder(path: entry.path, name: entry.name)) {
                            EntryRow(entry: entry)
                        }
                    } else {
                        Button {
                            model.open(entry)
                        } label: {
                            EntryRow(entry: entry)
                        }
                        .tint(.primary)
                    }
                }
            } footer: {
                if let skipped = listing?.skippedCount, skipped > 0, filter.isEmpty {
                    Text(skipped == 1 ? "1 item here can’t be shown." : "\(skipped) items here can’t be shown.")
                }
            }
        }
        .listStyle(.plain)
        .navigationTitle(folder.name)
        .searchable(text: $filter, prompt: "Filter this folder")
        .refreshable { await load() }
        .task { await load() }
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Picker("Sort By", selection: $sort) {
                        ForEach(Sort.allCases, id: \.self) { order in
                            Label(order.title, systemImage: order.symbol).tag(order)
                        }
                    }
                } label: {
                    Label("Sort", systemImage: "arrow.up.arrow.down")
                }
            }
        }
        .overlay { placeholder }
    }

    @ViewBuilder
    private var placeholder: some View {
        if listing == nil {
            if let failure {
                ContentUnavailableView {
                    Label("Can’t Show This Folder", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(failure)
                } actions: {
                    Button("Try Again") { Task { await load() } }
                }
            } else {
                ProgressView()
            }
        } else if shown.isEmpty {
            if filter.isEmpty {
                ContentUnavailableView("Nothing Here Yet", systemImage: "folder",
                                       description: Text("Files added to this folder in Uncloud show up here."))
            } else {
                ContentUnavailableView.search(text: filter)
            }
        }
    }

    private var shown: [LibraryEntry] {
        (listing?.entries ?? [])
            .filter { filter.isEmpty || $0.name.localizedStandardContains(filter) }
            .sorted(by: sort.ordersBefore)
    }

    private func load() async {
        if listing == nil { listing = model.lastListing(of: folder.path) }
        do {
            listing = try await model.listing(of: folder.path)
            failure = nil
        } catch is CancellationError {
        } catch {
            failure = error.localizedDescription
        }
    }
}

/// How a folder is sorted. Folders come first whichever it is, as on the host.
enum Sort: String, CaseIterable {
    case name, newest, largest

    var title: String {
        switch self {
        case .name: "Name"
        case .newest: "Newest First"
        case .largest: "Largest First"
        }
    }

    var symbol: String {
        switch self {
        case .name: "textformat"
        case .newest: "clock"
        case .largest: "externaldrive"
        }
    }

    func ordersBefore(_ a: LibraryEntry, _ b: LibraryEntry) -> Bool {
        if a.isDirectory != b.isDirectory { return a.isDirectory }
        switch self {
        case .name:
            break
        case .newest:
            if a.modifiedAt != b.modifiedAt { return a.modifiedAt > b.modifiedAt }
        case .largest:
            if (a.size ?? 0) != (b.size ?? 0) { return (a.size ?? 0) > (b.size ?? 0) }
        }
        return a.name.localizedStandardCompare(b.name) == .orderedAscending
    }
}

struct EntryRow: View {
    let entry: LibraryEntry

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: FileKind.symbol(for: entry))
                .font(.title2)
                .foregroundStyle(entry.isDirectory ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                .frame(width: 32)
            VStack(alignment: .leading, spacing: 2) {
                Text(entry.name)
                    .lineLimit(2)
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .accessibilityElement(children: .combine)
    }

    private var detail: String {
        let date = entry.modifiedAt.formatted(date: .abbreviated, time: .shortened)
        guard let size = entry.size else { return date }
        return "\(size.formatted(.byteCount(style: .file))) · \(date)"
    }
}

/// The file being downloaded to open, over the bottom of the list, with a way to stop.
struct DownloadBanner: View {
    let opening: AppModel.Opening
    let cancel: () -> Void

    var body: some View {
        HStack(spacing: 16) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Downloading “\(opening.entry.name)”")
                    .font(.subheadline)
                    .lineLimit(1)
                    .truncationMode(.middle)
                ProgressView(value: fraction)
                Text(amount)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            Button("Cancel", action: cancel)
        }
        .padding()
        .background(.regularMaterial, in: .rect(cornerRadius: 16))
        .padding(.horizontal)
        .padding(.bottom, 8)
    }

    private var fraction: Double? {
        guard let expected = opening.expected, expected > 0 else { return nil }
        return min(1, Double(opening.received) / Double(expected))
    }

    private var amount: String {
        let received = opening.received.formatted(.byteCount(style: .file))
        guard let expected = opening.expected else { return received }
        return "\(received) of \(expected.formatted(.byteCount(style: .file)))"
    }
}
