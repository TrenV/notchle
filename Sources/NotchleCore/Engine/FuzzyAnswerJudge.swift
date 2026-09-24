import Foundation

/// Forgiving but not gullible answer checking.
///
/// Both sides are normalized: case, diacritic and width folding ("Beyoncé" = "beyonce",
/// "ＢＴＳ" = "BTS"), "&" and "+" read as "and", apostrophes dropped ("ain't" = "aint"), other
/// punctuation treated as a space, whitespace ignored ("Man child" = "Manchild"), and an
/// optional leading "the".
///
/// - Title: the guess may match the full title or the title without decorations: bracketed
///   parts ("(feat. X)", "[Remastered]", "(From \"Film\")"), a dash suffix (" - Radio Edit",
///   " - Live at…", " - 2015 Mix"), an unbracketed "feat./ft." tail, or a trailing
///   "+ / & / with / x <credited artist>" ("Stateside + Zara Larsson" → "Stateside").
/// - Artist: the guess may match any one credited artist, or be a list ("Dave & Tems",
///   "Dave, Tems") in which every name is a credited artist.
/// - Fuzzy: Levenshtein similarity (1 - distance / longer length) >= 0.85 when the answer is
///   longer than 4 characters; answers of 4 characters or fewer need an exact match. Numbers
///   must match exactly ("Summer of 68" is not "Summer of 69"). No substring acceptance:
///   "love" does not match "Crazy in Love". An empty guess is wrong.
public struct FuzzyAnswerJudge: AnswerJudging {
    /// Levenshtein similarity needed for a fuzzy match. With 0.85, one typo is forgiven from 7
    /// characters on, two from 14.
    let similarityThreshold: Double
    /// Answers this short need an exact match whatever the threshold (so "22" is never "23").
    static let exactMatchMaxLength = 4

    public init() { self.init(similarityThreshold: 0.85) }

    init(similarityThreshold: Double) {
        self.similarityThreshold = similarityThreshold
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

    func artistMatches(_ guess: String, artists: [String]) -> Bool {
        let credited = artists.flatMap { artist -> [[String]] in
            let full = Self.tokens(artist)
            let unbracketed = Self.tokens(Self.removingBrackets(artist))
            return full == unbracketed ? [full] : [full, unbracketed]
        }.filter { !$0.isEmpty }
        let matchesCredited = { (tokens: [String]) in credited.contains { matches(tokens, $0) } }

        let whole = Self.tokens(guess)
        guard !whole.isEmpty else { return false }
        if matchesCredited(whole) { return true }

        // A list of names: every one must be credited.
        let connectors: Set<String> = ["and", "x", "with", "feat", "ft", "featuring"]
        var parts: [[String]] = []
        for piece in guess.split(whereSeparator: { ",;/".contains($0) }) {
            var current: [String] = []
            for token in Self.tokens(String(piece)) {
                if connectors.contains(token) {
                    if !current.isEmpty { parts.append(current) }
                    current = []
                } else {
                    current.append(token)
                }
            }
            if !current.isEmpty { parts.append(current) }
        }
        return parts.count >= 2 && parts.allSatisfy(matchesCredited)
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
        let gChars = Array(gKey), aChars = Array(aKey)
        guard aChars.count > Self.exactMatchMaxLength else { return false }
        let distance = Self.levenshtein(gChars, aChars)
        let similarity = 1 - Double(distance) / Double(max(gChars.count, aChars.count))
        return similarity >= similarityThreshold
    }

    private static func dropLeadingThe(_ tokens: [String]) -> [String] {
        tokens.count > 1 && tokens[0] == "the" ? Array(tokens.dropFirst()) : tokens
    }

    private static func numbers(_ tokens: [String]) -> [String] {
        tokens.joined(separator: " ")
            .split(whereSeparator: { !$0.isNumber })
            .map(String.init)
    }

    static func levenshtein(_ a: [Character], _ b: [Character]) -> Int {
        if a.isEmpty { return b.count }
        if b.isEmpty { return a.count }
        var previous = Array(0...b.count)
        var current = [Int](repeating: 0, count: b.count + 1)
        for i in 1...a.count {
            current[0] = i
            for j in 1...b.count {
                let cost = a[i - 1] == b[j - 1] ? 0 : 1
                current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + cost)
            }
            swap(&previous, &current)
        }
        return previous[b.count]
    }
}
