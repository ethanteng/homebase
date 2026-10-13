import SwiftUI

@main
struct UncloudApp: App {
    @State private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
        }
    }
}

struct RootView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        switch model.phase {
        case .connect:
            ConnectView()
        case .signIn(let address):
            SignInView(address: address)
        case .files(let account):
            FilesView(account: account)
        }
    }
}
