import Foundation
import NotchleCore

/// Pure presentation rules of the History tab (text, grouping). The spoiler rule itself is
/// `HistorySpoilerFilter` in NotchleCore; views only ever get `NotchUIState.visibleHistory`.
public enum NotchHistoryRules {
    public static let emptyText = "No songs yet: play a set and your results show up here."

    /// "1st try", "2nd try", "3rd try", "4th try"… or "missed".
    public static func badge(_ entry: HistoryEntry) -> String {
        guard entry.correct, let tier = entry.tierIndex else { return "missed" }
        return "\(ordinal(tier + 1)) try"
    }

    public static func ordinal(_ n: Int) -> String {
        let suffix: String
        switch (n % 10, n % 100) {
        case (_, 11...13): suffix = "th"
        case (1, _): suffix = "st"
        case (2, _): suffix = "nd"
        case (3, _): suffix = "rd"
        default: suffix = "th"
        }
        return "\(n)\(suffix)"
    }

    /// "just now", "5m ago", "2h ago", "3d ago", then "12 Aug".
    public static func relativeDate(_ date: Date, now: Date, calendar: Calendar = .current) -> String {
        let s = max(0, now.timeIntervalSince(date))
        switch s {
        case ..<60: return "just now"
        case ..<3600: return "\(Int(s / 60))m ago"
        case ..<86_400: return "\(Int(s / 3600))h ago"
        case ..<(7 * 86_400): return "\(Int(s / 86_400))d ago"
        default: return dayFormatter(calendar, "d MMM").string(from: date)
        }
    }

    public struct DayGroup: Hashable, Sendable, Identifiable {
        public let title: String
        public let entries: [HistoryEntry]
        public var id: String { title + (entries.first?.id.uuidString ?? "") }
    }

    /// Groups newest-first entries by calendar day: "Today", "Yesterday", "Mon 21 Sep".
    public static func dayGroups(_ entries: [HistoryEntry], now: Date, calendar: Calendar = .current) -> [DayGroup] {
        var groups: [DayGroup] = []
        var currentDay: Date?
        var bucket: [HistoryEntry] = []
        func flush() {
            if let day = currentDay, !bucket.isEmpty {
                groups.append(DayGroup(title: dayTitle(day, now: now, calendar: calendar), entries: bucket))
            }
        }
        for entry in entries.sorted(by: { $0.date > $1.date }) {
            let day = calendar.startOfDay(for: entry.date)
            if day != currentDay {
                flush()
                currentDay = day
                bucket = []
            }
            bucket.append(entry)
        }
        flush()
        return groups
    }

    public static func dayTitle(_ day: Date, now: Date, calendar: Calendar = .current) -> String {
        if calendar.isDate(day, inSameDayAs: now) { return "Today" }
        if let y = calendar.date(byAdding: .day, value: -1, to: now), calendar.isDate(day, inSameDayAs: y) {
            return "Yesterday"
        }
        return dayFormatter(calendar, "EEE d MMM").string(from: day)
    }

    /// Stats row values: "12/20", "60%", "1.8" (or "–"), "5".
    public static func statsTexts(_ stats: HistoryStats) -> (score: String, accuracy: String, avgTries: String, bestStreak: String) {
        (score: "\(stats.correct)/\(stats.total)",
         accuracy: "\(Int((stats.accuracy * 100).rounded()))%",
         avgTries: stats.averageTries.map { String(format: "%.1f", $0) } ?? "–",
         bestStreak: "\(stats.bestStreak)")
    }

    private static func dayFormatter(_ calendar: Calendar, _ format: String) -> DateFormatter {
        let f = DateFormatter()
        f.calendar = calendar
        f.timeZone = calendar.timeZone
        f.locale = Locale(identifier: "en_GB")
        f.dateFormat = format
        return f
    }
}
