import Foundation
import Testing
@testable import UncloudKit

struct HostAddressTests {
    @Test(arguments: [
        ("uncloud.tail1234.ts.net", "https://uncloud.tail1234.ts.net"),
        ("  https://uncloud.tail1234.ts.net/  ", "https://uncloud.tail1234.ts.net"),
        // Copied out of the browser with a page on the end, and in capitals.
        ("https://Uncloud.Tail1234.TS.net/?pair=abc#top", "https://uncloud.tail1234.ts.net"),
        ("https://files.example.com:8443/some/page", "https://files.example.com:8443"),
        ("https://someone:secret@files.example.com", "https://files.example.com"),
        // A simulator talking to a server run from source.
        ("http://127.0.0.1:5210", "http://127.0.0.1:5210"),
        ("http://localhost:5210/", "http://localhost:5210"),
    ])
    func readsWhatPeopleTypeOrPaste(text: String, expected: String) throws {
        #expect(try HostAddress(parsing: text).url.absoluteString == expected)
    }

    @Test func refusesPlainHTTPAnywhereButThisComputer() {
        #expect(throws: HostAddressError.notSecure) { try HostAddress(parsing: "http://uncloud.local:5210") }
        #expect(throws: HostAddressError.notSecure) { try HostAddress(parsing: "http://192.168.1.10") }
    }

    @Test(arguments: ["", "   ", "https://", "https:", "https:/files.example.com", "ftp://files.example.com", "not an address at all"])
    func refusesWhatIsNoAddress(text: String) {
        #expect(throws: HostAddressError.self) { try HostAddress(parsing: text) }
    }

    @Test func saysTheHostThePortOnlyWhenItIsntTheUsualOne() throws {
        #expect(try HostAddress(parsing: "uncloud.tail1234.ts.net").displayHost == "uncloud.tail1234.ts.net")
        #expect(try HostAddress(parsing: "http://127.0.0.1:5210").displayHost == "127.0.0.1:5210")
    }

    @Test func keepsAsTheAddressItself() throws {
        let address = try HostAddress(parsing: "uncloud.tail1234.ts.net")
        let data = try JSONEncoder().encode(address)
        #expect(try JSONDecoder().decode(String.self, from: data) == "https://uncloud.tail1234.ts.net")
        #expect(try JSONDecoder().decode(HostAddress.self, from: data) == address)
    }
}
