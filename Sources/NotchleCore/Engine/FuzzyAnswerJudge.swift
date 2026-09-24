import Foundation

/// Typo-tolerant answer checking: the gist must be right, the spelling need not be.
/// Rules agreed with Tren on 2026-09-24.
///
/// Normalization (both sides): case, diacritic and width folding ("Beyoncé" = "beyonce",
/// "ＢＴＳ" = "BTS"), "&" and "+" read as "and", apostrophes dropped ("ain't" = "aint"), other
/// punctuation treated as a space, and an optional leading "the".
///
/// Matching a guess against an answer (a title, or one artist name):
/// - Word by word, in order, every word covered: no missing and no extra words
///   ("Midnight" is not "Midnight Sun"). Each answer word allows a Damerau-Levenshtein
///   distance (a swap of two neighbouring letters is one edit) of 0 for 1-2 characters,
///   1 for 3-5, 2 for 6-9 and 3 for 10 or more.
/// - Spacing may differ ("Man child" = "Manchild"): a word may match several words joined up
///   on the other side. Such a joined match allows only the tolerance of its shortest word,
///   so a short word cannot silently vanish ("Karol" is not "KAROL G").
/// - Numbers must be identical ("Summer of 68" is not "Summer of 69").
/// - On top of that the whole string, spaces removed, needs a similarity
///   (1 - distance / longer length) of at least 0.75, so typos cannot pile up.
///
/// - Title: the guess may match the full title or the title without decorations: bracketed
///   parts ("(feat. X)", "[Remastered]", "(From \"Film\")"), a dash suffix (" - Radio Edit",
///   " - Live at…", " - 2015 Mix"), an unbracketed "feat./ft." tail, or a trailing
///   "+ / & / with / x <credited artist>" ("Stateside + Zara Larsson" → "Stateside").
/// - Artists: ALL credited artists must be named, in any order, each with typos allowed, and
///   nothing else. Between names only separators may appear (commas, "&", "and", "+", "x",
///   "×", "/", "feat.", "ft.", "featuring", "with", or plain spaces). Names that themselves
///   contain separators ("Simon & Garfunkel", "Earth, Wind & Fire", "Tyler, The Creator") work
///   because the guess is segmented against the credited names, not split blindly.
/// - An empty guess is wrong.
public struct FuzzyAnswerJudge: AnswerJudging {
    /// Minimum whole-string similarity, on top of the per-word tolerance.
    let wholeStringThreshold: Double

    public init() { self.init(wholeStringThreshold: 0.75) }

    init(wholeStringThreshold: Double) {
        self.wholeStringThreshold = wholeStringThreshold
    }

    public func judge(_ guess: Guess, against track: Track) -> Verdict {
        Verdict(titleCorrect: titleMatches(guess.title, track: track),
                artistCorrect: artistMatches(guess.artist, artists: track.artists))
    }

    // MARK: - Title

    func titleMatches(_ guess: String, track: Track) -> Bool {
        let guessTokens = Self.tokens(guess)
        guard !guessTokens.isEmpty else { return false }
        return Self.titleVariants(track.title, artists: track.artists)
            .contains { matches(guessTokens, $0) }
    }

    static func titleVariants(_ title: String, artists: [String]) -> [[String]] {
        var variants: [[String]] = [tokens(title)]
        let unbracketed = removingBrackets(title)
        variants.append(tokens(unbracketed))
        var stripped = tokens(beforeDashSuffix(unbracketed))
        variants.append(stripped)
        stripped = beforeFeaturing(stripped)
        variants.append(stripped)
        variants.append(withoutTrailingArtist(stripped, artists: artists))
        var unique: [[String]] = []
        for v in variants where !v.isEmpty && !unique.contains(v) { unique.append(v) }
        return unique
    }

    /// Drops "( … )" and "[ … ]" parts, nested or not.
    static func removingBrackets(_ s: String) -> String {
        var out = ""
        var depth = 0
        for ch in s {
            switch ch {
            case "(", "[": depth += 1
            case ")", "]": if depth > 0 { depth -= 1 } else { out.append(" ") }
            default: if depth == 0 { out.append(ch) }
            }
        }
        return out
    }

    /// Cuts at the first spaced dash: "Song - Remastered 2011" → "Song".
    static func beforeDashSuffix(_ s: String) -> String {
        let dashes = [" - ", " – ", " — "]
        let cut = dashes.compactMap { s.range(of: $0)?.lowerBound }.min()
        return cut.map { String(s[..<$0]) } ?? s
    }

    static func beforeFeaturing(_ tokens: [String]) -> [String] {
        guard let i = tokens.firstIndex(where: { ["feat", "ft", "featuring"].contains($0) }), i > 0
        else { return tokens }
        return Array(tokens[..<i])
    }

    /// "stateside and zara larsson" → "stateside" when Zara Larsson is a credited artist.
    static func withoutTrailingArtist(_ tokens: [String], artists: [String]) -> [String] {
        for artist in artists {
            let a = self.tokens(removingBrackets(artist))
            guard !a.isEmpty, tokens.count > a.count + 1 else { continue }
            let connectorIndex = tokens.count - a.count - 1
            if Array(tokens[(connectorIndex + 1)...]) == a,
               ["and", "with", "x"].contains(tokens[connectorIndex]) {
                return Array(tokens[..<connectorIndex])
            }
        }
        return tokens
    }

    // MARK: - Artist

    /// Separator words allowed between artist names ("×", commas and "/" are already spaces).
    static let artistSeparators: Set<String> = ["and", "x", "feat", "ft", "featuring", "with"]

    func artistMatches(_ guess: String, artists: [String]) -> Bool {
        // Each distinct credited artist, with a bracket-free variant ("SOLTO (FR)" → "SOLTO").
        var credited: [[[String]]] = []
        for artist in artists {
            let full = Self.tokens(artist)
            let unbracketed = Self.tokens(Self.removingBrackets(artist))
            let variants = (full == unbracketed ? [full] : [full, unbracketed]).filter { !$0.isEmpty }
            if !variants.isEmpty && !credited.contains(variants) { credited.append(variants) }
        }
        let words = Self.tokens(guess)
        guard !words.isEmpty, !credited.isEmpty, credited.count < 64 else { return false }

        // reachable[j]: sets of credited artists (bitmasks) that words[..<j] can be read as,
        // with only separators between them. Each artist is used at most once.
        let all: UInt64 = (1 << UInt64(credited.count)) - 1
        var reachable = [Set<UInt64>](repeating: [], count: words.count + 1)
        reachable[0] = [0]
        for j in 0..<words.count {
            for mask in reachable[j] {
                if Self.artistSeparators.contains(words[j]) { reachable[j + 1].insert(mask) }
                for end in (j + 1)...words.count {
                    let span = Array(words[j..<end])
                    for (i, variants) in credited.enumerated() where mask & (1 << UInt64(i)) == 0 {
                        if variants.contains(where: { matches(span, $0) }) {
                            reachable[end].insert(mask | (1 << UInt64(i)))
                        }
                    }
                }
            }
        }
        return reachable[words.count].contains(all)
    }

    // MARK: - Normalization and matching

    /// Folded, punctuation-free words. "Beyoncé & Jay-Z" → ["beyonce", "and", "jay", "z"].
    static func tokens(_ s: String) -> [String] {
        let folded = s.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive],
                               locale: nil).lowercased()
        var cleaned = ""
        for ch in folded {
            if let replacement = extraFolds[ch] {
                cleaned += replacement
            } else if apostrophes.contains(ch) {
                continue
            } else if ch == "&" || ch == "+" {
                cleaned += " and "
            } else if ch.isLetter || ch.isNumber {
                cleaned.append(ch)
            } else {
                cleaned.append(" ")
            }
        }
        return cleaned.split(whereSeparator: { $0 == " " }).map(String.init)
    }

    /// Letters that are not base letter + combining mark, so diacritic folding keeps them.
    private static let extraFolds: [Character: String] = [
        "ø": "o", "æ": "ae", "œ": "oe", "ß": "ss", "ł": "l", "đ": "d", "ð": "d", "þ": "th", "ı": "i",
    ]
    private static let apostrophes: Set<Character> = ["'", "\u{2019}", "\u{2018}", "`", "\u{00B4}", "\u{02BC}"]

    func matches(_ guess: [String], _ answer: [String]) -> Bool {
        let g = Self.dropLeadingThe(guess), a = Self.dropLeadingThe(answer)
        let gKey = g.joined(), aKey = a.joined()
        guard !gKey.isEmpty, !aKey.isEmpty else { return false }
        if gKey == aKey { return true }
        guard Self.numbers(g) == Self.numbers(a) else { return false }
        guard Self.wordsAlign(g, a) else { return false }
        let gChars = Array(gKey), aChars = Array(aKey)
        let distance = Self.damerauLevenshtein(gChars, aChars)
        return 1 - Double(distance) / Double(max(gChars.count, aChars.count)) >= wholeStringThreshold
    }

    /// Typos allowed in an answer word of `length` characters.
    static func tolerance(_ length: Int) -> Int {
        switch length {
        case ...2: 0
        case 3...5: 1
        case 6...9: 2
        default: 3
        }
    }

    /// Every answer word is matched, in order, by guess words; one word may match several
    /// joined words on the other side (spacing differences), with the shortest word's tolerance.
    static func wordsAlign(_ guess: [String], _ answer: [String]) -> Bool {
        let n = answer.count, m = guess.count
        var ok = [[Bool]](repeating: [Bool](repeating: false, count: m + 1), count: n + 1)
        ok[0][0] = true
        for i in 0..<n {
            for j in 0..<m where ok[i][j] {
                // One answer word ↔ one or more guess words.
                for k in 1...(m - j) {
                    let pieces = Array(guess[j..<(j + k)])
                    let shortest = min(answer[i].count, pieces.map(\.count).min()!)
                    let allowed = k == 1 ? tolerance(answer[i].count) : tolerance(shortest)
                    if withinDistance(pieces.joined(), answer[i], allowed) { ok[i + 1][j + k] = true }
                }
                // Several answer words ↔ one guess word.
                if n - i >= 2 {
                    for k in 2...(n - i) {
                        let pieces = Array(answer[i..<(i + k)])
                        let shortest = min(guess[j].count, pieces.map(\.count).min()!)
                        if withinDistance(guess[j], pieces.joined(), tolerance(shortest)) {
                            ok[i + k][j + 1] = true
                        }
                    }
                }
            }
        }
        return ok[n][m]
    }

    private static func withinDistance(_ a: String, _ b: String, _ allowed: Int) -> Bool {
        if a == b { return true }
        guard allowed > 0 else { return false }
        let x = Array(a), y = Array(b)
        guard abs(x.count - y.count) <= allowed else { return false }
        return damerauLevenshtein(x, y) <= allowed
    }

    private static func dropLeadingThe(_ tokens: [String]) -> [String] {
        tokens.count > 1 && tokens[0] == "the" ? Array(tokens.dropFirst()) : tokens
    }

    private static func numbers(_ tokens: [String]) -> [String] {
        tokens.joined(separator: " ")
            .split(whereSeparator: { !$0.isNumber })
            .map(String.init)
    }

    /// Optimal-string-alignment distance: insert, delete, substitute, or swap two neighbours.
    static func damerauLevenshtein(_ a: [Character], _ b: [Character]) -> Int {
        if a.isEmpty { return b.count }
        if b.isEmpty { return a.count }
        var d = [[Int]](repeating: [Int](repeating: 0, count: b.count + 1), count: a.count + 1)
        for i in 0...a.count { d[i][0] = i }
        for j in 0...b.count { d[0][j] = j }
        for i in 1...a.count {
            for j in 1...b.count {
                let cost = a[i - 1] == b[j - 1] ? 0 : 1
                d[i][j] = min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + cost)
                if i > 1, j > 1, a[i - 1] == b[j - 2], a[i - 2] == b[j - 1] {
                    d[i][j] = min(d[i][j], d[i - 2][j - 2] + 1)
                }
            }
        }
        return d[a.count][b.count]
    }
}
