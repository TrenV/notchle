import SwiftUI
import NotchleCore

/// Play / History switch in the header. ⌘1 / ⌘2 do the same.
struct NotchTabSwitch: View {
    let ui: NotchUIState

    var body: some View {
        HStack(spacing: 1) {
            item(.play, symbol: "gamecontroller.fill", help: "Play (⌘1)")
            item(.history, symbol: "clock.arrow.circlepath", help: "History (⌘2)")
        }
        .padding(2)
        .background(Capsule().fill(Color.white.opacity(0.09)))
    }

    private func item(_ tab: NotchTab, symbol: String, help: String) -> some View {
        let selected = ui.tab == tab && !ui.showingSettings
        return Button { ui.showTab(tab) } label: {
            Image(systemName: symbol)
                .font(.system(size: 9.5, weight: .bold))
                .foregroundStyle(selected ? Color.black : NotchPalette.secondaryText)
                .frame(width: 22, height: 16)
                .background(Capsule().fill(selected ? NotchPalette.green : Color.clear))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help(help)
        .accessibilityLabel(tab == .play ? "Play" : "History")
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// The History tab: stats, then every visible entry newest first, grouped by day.
/// Only reads `ui.visibleHistory` (the spoiler-filtered list), never `model.history`.
struct NotchHistoryView: View {
    let ui: NotchUIState
    var now: Date = Date()

    var body: some View {
        let entries = ui.visibleHistory
        VStack(alignment: .leading, spacing: 0) {
            if entries.isEmpty {
                emptyState
            } else {
                statsRow(HistoryStats(entries))
                list(entries)
                    .padding(.top, 8)
            }
            footer(count: entries.count)
                .padding(.top, 6)
        }
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            Image(systemName: "clock.arrow.circlepath")
                .font(.system(size: 24, weight: .semibold))
                .foregroundStyle(NotchPalette.tertiaryText)
            Text(NotchHistoryRules.emptyText)
                .font(.system(size: 12.5, weight: .medium))
                .foregroundStyle(NotchPalette.secondaryText)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 260)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func statsRow(_ stats: HistoryStats) -> some View {
        let t = NotchHistoryRules.statsTexts(stats)
        return HStack(spacing: 6) {
            stat(t.score, "correct", symbol: "checkmark", tint: NotchPalette.green)
            stat(t.accuracy, "accuracy", symbol: "scope", tint: .white)
            stat(t.avgTries, "avg tries", symbol: "waveform", tint: .white)
            stat(t.bestStreak, "best streak", symbol: "flame.fill", tint: Color(red: 1, green: 0.62, blue: 0.25))
        }
    }

    private func stat(_ value: String, _ label: String, symbol: String, tint: Color) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            HStack(spacing: 4) {
                Image(systemName: symbol)
                    .font(.system(size: 9, weight: .heavy))
                    .foregroundStyle(tint.opacity(0.9))
                Text(value)
                    .font(.system(size: 14, weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(NotchPalette.primaryText)
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
            }
            Text(label)
                .font(.system(size: 9.5, weight: .medium))
                .foregroundStyle(NotchPalette.tertiaryText)
                .lineLimit(1)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.white.opacity(0.07)))
    }

    private func list(_ entries: [HistoryEntry]) -> some View {
        ScrollView(.vertical, showsIndicators: false) {
            LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                ForEach(NotchHistoryRules.dayGroups(entries, now: now)) { group in
                    Section {
                        ForEach(group.entries) { HistoryRow(entry: $0, now: now) }
                    } header: {
                        Text(group.title.uppercased())
                            .font(.system(size: 9, weight: .bold))
                            .kerning(0.6)
                            .foregroundStyle(NotchPalette.tertiaryText)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.vertical, 3)
                            .background(Color.black)
                    }
                }
            }
        }
        .frame(maxHeight: .infinity)
    }

    private func footer(count: Int) -> some View {
        HStack {
            Text(count == 0 ? " " : count == 1 ? "1 song" : "\(count) songs")
                .font(.system(size: 10.5, weight: .medium))
                .foregroundStyle(NotchPalette.tertiaryText)
            Spacer()
            if !ui.model.history.isEmpty {
                let armed = ui.isClearHistoryArmed()
                Button { ui.clearHistoryPressed() } label: {
                    HStack(spacing: 4) {
                        Image(systemName: "trash").font(.system(size: 9.5, weight: .bold))
                        Text(armed ? "Clear history?" : "Clear history")
                    }
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(armed ? Color.white : NotchPalette.secondaryText)
                    .padding(.horizontal, 10)
                    .frame(height: 22)
                    .background(Capsule().fill(armed ? NotchPalette.red.opacity(0.85) : Color.white.opacity(0.08)))
                    .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .help(armed ? "Click again to delete every entry" : "Delete the play history")
            }
        }
    }
}

struct HistoryRow: View {
    let entry: HistoryEntry
    let now: Date

    var body: some View {
        HStack(spacing: 8) {
            CoverArtView(trackID: entry.trackID, url: entry.artworkURL, size: 28, cornerRadius: 5)
                .overlay(alignment: .bottomTrailing) {
                    Image(systemName: entry.correct ? "checkmark.circle.fill" : "xmark.circle.fill")
                        .font(.system(size: 10, weight: .bold))
                        .foregroundStyle(entry.correct ? NotchPalette.green : NotchPalette.red)
                        .background(Circle().fill(Color.black).padding(-1))
                        .offset(x: 3, y: 3)
                }
            VStack(alignment: .leading, spacing: 1) {
                (Text(entry.title).foregroundStyle(NotchPalette.primaryText)
                 + Text("  " + entry.artists.joined(separator: ", ")).foregroundStyle(NotchPalette.secondaryText))
                    .font(.system(size: 12, weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(.tail)
                Text([entry.listingName, NotchHistoryRules.relativeDate(entry.date, now: now)]
                        .filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.system(size: 10, weight: .medium))
                    .foregroundStyle(NotchPalette.tertiaryText)
                    .lineLimit(1)
            }
            Spacer(minLength: 4)
            if entry.skips > 0 { counter("forward.end.fill", entry.skips, help: "Skips") }
            if entry.wrongGuesses > 0 { counter("xmark", entry.wrongGuesses, help: "Wrong guesses") }
            Text(NotchHistoryRules.badge(entry))
                .font(.system(size: 10, weight: .bold, design: .rounded))
                .foregroundStyle(entry.correct ? NotchPalette.green : NotchPalette.red)
                .padding(.horizontal, 7)
                .frame(height: 18)
                .background(Capsule().fill((entry.correct ? NotchPalette.green : NotchPalette.red).opacity(0.15)))
                .fixedSize()
        }
        .padding(.vertical, 4)
    }

    private func counter(_ symbol: String, _ n: Int, help: String) -> some View {
        HStack(spacing: 2) {
            Image(systemName: symbol).font(.system(size: 8, weight: .heavy))
            Text("\(n)").font(.system(size: 10, weight: .semibold, design: .rounded)).monospacedDigit()
        }
        .foregroundStyle(NotchPalette.tertiaryText)
        .help(help)
        .fixedSize()
    }
}
