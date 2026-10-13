import SwiftUI

/// Who is signed in, where, the way to everything the app doesn't do yet, and signing out.
struct AccountView: View {
    let account: AppModel.Account
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var confirmingSignOut = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    LabeledContent("Name", value: account.user.displayName)
                    LabeledContent("Username", value: account.user.username)
                    LabeledContent("Uncloud", value: account.address.displayHost)
                }
                Section {
                    Link(destination: account.address.url) {
                        Label("Open Uncloud in Safari", systemImage: "safari")
                    }
                } footer: {
                    Text(account.user.isAdmin
                        ? "Adding files, the Bin, People and Settings are in Uncloud in your browser for now."
                        : "Adding files and the Bin are in Uncloud in your browser for now.")
                }
                Section {
                    Button("Sign Out", role: .destructive) { confirmingSignOut = true }
                        .confirmationDialog("Sign out of Uncloud on this device?", isPresented: $confirmingSignOut,
                                            titleVisibility: .visible) {
                            Button("Sign Out", role: .destructive) {
                                Task { await model.signOut() }
                            }
                        } message: {
                            Text("Files this device downloaded are deleted from it. Nothing changes in Uncloud.")
                        }
                } footer: {
                    Text("Uncloud for iPhone and iPad \(Self.version)")
                }
            }
            .navigationTitle("Account")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
    }

    private static var version: String {
        let info = Bundle.main.infoDictionary
        let short = info?["CFBundleShortVersionString"] as? String ?? "?"
        let build = info?["CFBundleVersion"] as? String ?? "?"
        return "\(short) (\(build))"
    }
}
