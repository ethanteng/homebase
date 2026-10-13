import SwiftUI

/// The first screen: which Uncloud to use. Most people paste the address whoever looks after it sent.
struct ConnectView: View {
    @Environment(AppModel.self) private var model
    @State private var address = ""
    @State private var failure: String?
    @State private var working = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("uncloud.example.ts.net", text: $address)
                        .keyboardType(.URL)
                        .textContentType(.URL)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .submitLabel(.continue)
                        .onSubmit(connect)
                } header: {
                    Text("Your Uncloud’s address")
                } footer: {
                    Text("Whoever looks after your Uncloud has it. It’s the address Reach From Anywhere shows, "
                        + "such as https://uncloud.tail1234.ts.net.")
                }
                if let failure {
                    Section {
                        Label(failure, systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.red)
                    }
                }
                Section {
                    Button(action: connect) {
                        HStack {
                            Text("Continue")
                            Spacer()
                            if working { ProgressView() }
                        }
                    }
                    .disabled(working || address.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            .navigationTitle("Uncloud")
        }
    }

    private func connect() {
        guard !working, !address.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        working = true
        failure = nil
        Task {
            defer { working = false }
            do {
                try await model.connect(to: address)
            } catch is CancellationError {
            } catch {
                failure = error.localizedDescription
            }
        }
    }
}
