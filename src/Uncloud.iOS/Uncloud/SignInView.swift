import SwiftUI
import UncloudKit

/// Username and password, the same ones as in the browser.
struct SignInView: View {
    let address: HostAddress
    @Environment(AppModel.self) private var model
    @State private var username = ""
    @State private var password = ""
    @State private var failure: String?
    @State private var working = false
    @FocusState private var focus: Field?

    private enum Field {
        case username, password
    }

    var body: some View {
        NavigationStack {
            Form {
                if let notice = model.signInNotice {
                    Section {
                        Label(notice, systemImage: "info.circle")
                    }
                }
                Section {
                    TextField("Username", text: $username)
                        .textContentType(.username)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .submitLabel(.next)
                        .focused($focus, equals: .username)
                        .onSubmit { focus = .password }
                    SecureField("Password", text: $password)
                        .textContentType(.password)
                        .submitLabel(.go)
                        .focused($focus, equals: .password)
                        .onSubmit(signIn)
                } header: {
                    Text(address.displayHost)
                }
                if let failure {
                    Section {
                        Label(failure, systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.red)
                    }
                }
                Section {
                    Button(action: signIn) {
                        HStack {
                            Text("Sign In")
                            Spacer()
                            if working { ProgressView() }
                        }
                    }
                    .disabled(working || username.isEmpty || password.isEmpty)
                } footer: {
                    Text("Forgotten your password? Ask whoever looks after your Uncloud. They can give you a new one under People.")
                }
                Section {
                    Button("Use a Different Uncloud") { model.useAnotherHost() }
                }
            }
            .navigationTitle("Sign In")
            .onAppear {
                if username.isEmpty { username = model.rememberedUsername }
                focus = username.isEmpty ? .username : .password
            }
        }
    }

    private func signIn() {
        guard !working, !username.isEmpty, !password.isEmpty else { return }
        working = true
        failure = nil
        Task {
            defer { working = false }
            do {
                try await model.signIn(username: username.trimmingCharacters(in: .whitespaces), password: password)
            } catch is CancellationError {
            } catch {
                failure = error.localizedDescription
            }
        }
    }
}
