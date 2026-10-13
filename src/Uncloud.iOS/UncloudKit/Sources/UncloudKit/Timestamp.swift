import Foundation

/// The host's dates, which are .NET's `DateTimeOffset` as System.Text.Json writes it: ISO 8601 with
/// anywhere from no digits to seven after the seconds, and an offset, such as
/// `2026-10-13T22:58:00.1234567+00:00`. `JSONDecoder`'s own `.iso8601` has refused fractions of a
/// second, so this reads them itself rather than depend on which Foundation it gets.
enum Timestamp {
    static func parse(_ text: String) -> Date? {
        let bytes = Array(text.utf8)
        func digits(_ start: Int, _ count: Int) -> Int? {
            guard start + count <= bytes.count else { return nil }
            var value = 0
            for byte in bytes[start..<(start + count)] {
                guard (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(byte) else { return nil }
                value = value * 10 + Int(byte - UInt8(ascii: "0"))
            }
            return value
        }
        func at(_ index: Int, _ character: Character) -> Bool {
            index < bytes.count && bytes[index] == character.asciiValue
        }
        guard at(4, "-"), at(7, "-"), at(10, "T") || at(10, "t"), at(13, ":"), at(16, ":"),
              let year = digits(0, 4), let month = digits(5, 2), let day = digits(8, 2),
              let hour = digits(11, 2), let minute = digits(14, 2), let second = digits(17, 2),
              (1...12).contains(month), (1...31).contains(day),
              hour < 24, minute < 60, second <= 60
        else { return nil }

        var index = 19
        var fraction = 0.0
        if at(index, ".") {
            index += 1
            let start = index
            var scale = 0.1
            while let digit = digits(index, 1) {
                fraction += Double(digit) * scale
                scale /= 10
                index += 1
            }
            guard index > start else { return nil }
        }

        var offset = 0
        if index == bytes.count {
            // No zone at all. The host always sends one; if it ever doesn't, it means UTC there too.
        } else if at(index, "Z") || at(index, "z") {
            index += 1
        } else if at(index, "+") || at(index, "-") {
            guard let hours = digits(index + 1, 2), at(index + 3, ":"), let minutes = digits(index + 4, 2)
            else { return nil }
            offset = (hours * 3600 + minutes * 60) * (at(index, "-") ? -1 : 1)
            index += 6
        } else {
            return nil
        }
        guard index == bytes.count else { return nil }

        let seconds = daysFromCivil(year, month, day) * 86_400 + hour * 3600 + minute * 60 + second - offset
        return Date(timeIntervalSince1970: Double(seconds) + fraction)
    }

    /// Days since 1970-01-01 in the proleptic Gregorian calendar (Howard Hinnant's `days_from_civil`).
    private static func daysFromCivil(_ year: Int, _ month: Int, _ day: Int) -> Int {
        let y = month <= 2 ? year - 1 : year
        let era = (y >= 0 ? y : y - 399) / 400
        let yearOfEra = y - era * 400
        let dayOfYear = (153 * ((month + 9) % 12) + 2) / 5 + day - 1
        let dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear
        return era * 146_097 + dayOfEra - 719_468
    }
}

extension JSONDecoder {
    /// A decoder for what the host sends.
    static func uncloud() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let text = try container.decode(String.self)
            guard let date = Timestamp.parse(text) else {
                throw DecodingError.dataCorruptedError(in: container, debugDescription: "Not a date Uncloud sends: \(text)")
            }
            return date
        }
        return decoder
    }
}
