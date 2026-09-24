import Foundation
import Testing
@testable import NotchleCore

private func track(_ title: String, _ artists: String...) -> Track {
    Track(id: "id", uri: "spotify:track:id", title: title, artists: artists, durationMs: 1, previewURL: nil)
}

struct TitleCase: CustomTestStringConvertible, Sendable {
    let guess: String
    let title: String
    let artists: [String]
    let expected: Bool
    var testDescription: String { "\"\(guess)\" vs \"\(title)\" → \(expected)" }

    init(_ guess: String, _ title: String, artists: [String] = ["X"], _ expected: Bool) {
        self.guess = guess; self.title = title; self.artists = artists; self.expected = expected
    }
}

struct ArtistCase: CustomTestStringConvertible, Sendable {
    let guess: String
    let artists: [String]
    let expected: Bool
    var testDescription: String { "\"\(guess)\" vs \(artists) → \(expected)" }

    init(_ guess: String, _ artists: [String], _ expected: Bool) {
        self.guess = guess; self.artists = artists; self.expected = expected
    }
}

// Titles marked (fixture) come from Tests/NotchleCoreTests/Fixtures/embed-playlist-todays-top-hits.html.
let titleCases: [TitleCase] = [
    // Positives: normalization
    TitleCase("hate that i made you love me", "hate that i made you love me", true),         // fixture
    TitleCase("Hate That I Made You Love Me", "hate that i made you love me", true),         // case
    TitleCase("bby wow", "BbY WOW", true),                                                   // fixture, case
    TitleCase("Aint in LA", "Ain't In LA", true),                                            // fixture, apostrophe
    TitleCase("where is my husband", "WHERE IS MY HUSBAND!", true),                          // fixture, punctuation
    TitleCase("oh yeah", "oh yeah?", true),                                                  // fixture
    TitleCase("Raindance", "Raindance (feat. Tems)", true),                                  // fixture, feat
    TitleCase("raindance feat tems", "Raindance (feat. Tems)", true),                        // full title
    TitleCase("Talk to you", "Talk To You (ft. 54 Ultra)", true),                            // fixture
    TitleCase("Rein me in", "Rein Me In (with Olivia Dean)", true),                          // fixture
    TitleCase("last thing you need", "Last Thing You Need (from GTAVI: The Album)", true),   // fixture
    TitleCase("I knew it I knew you", "I Knew It, I Knew You - From \"Toy Story 5\"", true), // fixture, dash
    TitleCase("Stateside", "Stateside + Zara Larsson", artists: ["PinkPantheress", "Zara Larsson"], true), // fixture
    TitleCase("stateside and zara larsson", "Stateside + Zara Larsson", true),               // "+" = "and"
    TitleCase("So easy", "So Easy (To Fall In Love)", true),                                 // fixture
    TitleCase("fate of ophelia", "The Fate of Ophelia", true),                               // fixture, leading "the"
    TitleCase("Man child", "Manchild", true),                                                // fixture, spacing
    TitleCase("i love it i love it i love it", "iloveitiloveitiloveit", true),               // fixture, spacing
    TitleCase("dtmf", "DtMF", true),                                                         // fixture
    TitleCase("Here Comes the Sun", "Here Comes The Sun - Remastered 2019", true),
    TitleCase("Mr Brightside", "Mr. Brightside [Remastered]", true),
    TitleCase("Heroes", "\"Heroes\" - 2017 Remaster", true),
    TitleCase("Halo", "Halo - Live at Wembley", true),
    TitleCase("Déjà vu", "deja vu", true),                                                   // diacritics
    TitleCase("ＳＷＩＭ", "SWIM", true),                                                       // width folding
    TitleCase("Rock & Roll", "Rock and Roll", true),                                         // "&" = "and"
    // Positives: typos
    TitleCase("hate that i made you lvoe me", "hate that i made you love me", true),         // fixture, swap = 1 edit
    TitleCase("hat that i mde you love me", "hate that i made you love me", true),           // fixture, two words
    TitleCase("choosing texas", "Choosin' Texas", true),                                     // fixture
    TitleCase("Bass Persuade", "Bass Persuades", true),                                      // fixture
    TitleCase("petals", "petal", true),                                                      // fixture, 5 chars: 1
    TitleCase("Animl", "Animal", true),                                                      // fixture, 6 chars: 1
    TitleCase("Anmial", "Animal", true),                                                     // fixture, swap
    TitleCase("lose", "Loser", true),                                                        // fixture
    TitleCase("swimm", "SWIM", true),                                                        // fixture, 4 chars: 1
    TitleCase("Bby wwo", "BbY WOW", true),                                                   // fixture, 3 chars: 1
    TitleCase("the fate of ophilia", "The Fate of Ophelia", true),                           // fixture
    TitleCase("Iloveitiloveitilovit", "iloveitiloveitiloveit", true),                        // fixture, 21 chars
    TitleCase("Man chld", "Manchild", true),                                                 // spacing + typo
    TitleCase("Love", "Lose", true),                                                         // chosen: 4 chars, 1 edit
    TitleCase("Manchi", "Manchild", true),                                                   // chosen: 2 edits on 8 chars
    // Negatives
    TitleCase("love", "Crazy in Love", false),                                               // no substring
    TitleCase("Midnight", "Midnight Sun", false),                                            // fixture, missing word
    TitleCase("hate that i made you love", "hate that i made you love me", false),           // fixture, missing word
    TitleCase("Janice", "Janice STFU", false),                                               // fixture, partial
    TitleCase("Midnight Sun Rises", "Midnight Sun", false),                                  // extra word
    TitleCase("hate that i made u love me", "hate that i made you love me", false),          // fixture, u↔you = 2 edits
    TitleCase("22", "23", false),                                                            // numbers
    TitleCase("Summer of 68", "Summer of 69", false),                                        // numbers
    TitleCase("Me", "Mi", false),                                                            // 1-2 chars exact
    TitleCase("ok yeah", "oh yeah?", false),                                                 // fixture, 2-char word exact
    TitleCase("Hello", "Halo", false),                                                       // 2 edits on 4 chars
    TitleCase("Dia Dia", "Dai Dai", false),                                                  // fixture, 2 edits / 6: whole < 0.75
    TitleCase("Debi Tirar Mas Fotos", "DtMF", false),                                        // fixture, no expansion
    TitleCase("Man chi", "Manchild", false),                                                 // joined: shortest word's tolerance
    TitleCase("", "Animal", false),                                                          // empty
    TitleCase("   ", "Animal", false),                                                       // blank
    TitleCase("Remastered 2011", "Yesterday - Remastered 2011", false),                      // decoration alone
    TitleCase("the", "the cure", false),                                                     // fixture
    TitleCase("Stupid", "stupid song", false),                                               // fixture, missing word
]

let bbyWow = ["KAROL G", "Judeline", "rusowsky"]

let artistCases: [ArtistCase] = [
    // Single artist
    ArtistCase("Ariana Grande", ["Ariana Grande"], true),                                    // fixture
    ArtistCase("Ariana Grnade", ["Ariana Grande"], true),                                    // swap
    ArtistCase("ariana grand", ["Ariana Grande"], true),
    ArtistCase("Beyonse", ["Beyoncé"], true),
    ArtistCase("Rosé", ["ROSÉ"], true),                                                      // fixture
    ArtistCase("rose", ["ROSÉ"], true),                                                      // fixture, diacritics
    ArtistCase("Adela", ["ADÉLA"], true),                                                    // fixture
    ArtistCase("Mumford and Sons", ["Mumford & Sons"], true),
    ArtistCase("Weeknd", ["The Weeknd"], true),                                              // leading "the"
    ArtistCase("B.T.S.", ["BTS"], true),                                                     // fixture, punctuation
    ArtistCase("Bad Buny", ["Bad Bunny"], true),                                             // fixture
    ArtistCase("Ray", ["RAYE"], true),                                                       // fixture, 4 chars: 1 edit
    ArtistCase("Olivia Rodrigo", ["Ariana Grande"], false),
    ArtistCase("Olivia", ["Olivia Dean"], false),                                            // fixture, missing word
    ArtistCase("Karol", ["KAROL G"], false),                                                 // "g" cannot vanish
    ArtistCase("Karol J", ["KAROL G"], false),                                               // 1-char word exact
    ArtistCase("Ashe", ["RAYE"], false),                                                     // fixture
    ArtistCase("Simon", ["Simon & Garfunkel"], false),                                       // partial
    ArtistCase("", ["Drake"], false),
    ArtistCase("and", ["Drake"], false),                                                     // separators only
    // Names containing separators
    ArtistCase("Simon and Garfunkel", ["Simon & Garfunkel"], true),
    ArtistCase("Earth Wind and Fire", ["Earth, Wind & Fire"], true),
    ArtistCase("Tyler the Creator", ["Tyler, The Creator"], true),
    ArtistCase("Tyler, the Creator & Kali Uchis", ["Tyler, The Creator", "Kali Uchis"], true),
    ArtistCase("Earth Wind & Fire with Tems", ["Earth, Wind & Fire", "Tems"], true),
    // Multiple artists: all required, any order, separators or spaces
    ArtistCase("karol g, judeline & rusowsky", bbyWow, true),                                // fixture
    ArtistCase("rusowsky judeline karol g", bbyWow, true),                                   // fixture, spaces only
    ArtistCase("Judeline x KAROL G feat. rusowsky", bbyWow, true),                           // fixture
    ArtistCase("Judeline × Karol G / Rusowski", bbyWow, true),                               // fixture, typo, ×, /
    ArtistCase("Karol G + Judeline and rusowsky", bbyWow, true),                             // fixture, "+"
    ArtistCase("Dave & Tems", ["Dave", "Tems"], true),                                       // fixture
    ArtistCase("Tems ft Dave", ["Dave", "Tems"], true),                                      // fixture, order
    ArtistCase("Sam Fender, Olivia Dean", ["Sam Fender", "Olivia Dean"], true),              // fixture
    ArtistCase("hugel & solto", ["HUGEL", "SOLTO (FR)"], true),                              // fixture, bracket
    ArtistCase("KAROL G", bbyWow, false),                                                    // fixture, one of three
    ArtistCase("Judeline", bbyWow, false),                                                   // fixture
    ArtistCase("karol g, judeline", bbyWow, false),                                          // fixture, one missing
    ArtistCase("karol g, judeline, rusowsky, bad bunny", bbyWow, false),                     // fixture, extra name
    ArtistCase("karol g judeline rusowsky rusowsky", bbyWow, false),                         // repeated name is extra
    ArtistCase("Dave & Stormzy", ["Dave", "Tems"], false),                                   // uncredited name
    ArtistCase("Dave", ["Dave", "Tems"], false),                                             // fixture
    ArtistCase("Morgan Wallen", ["Morgan Wallen", "Grand Theft Auto VI"], false),            // fixture, all required
    ArtistCase("Morgan Wallen & GTA VI", ["Morgan Wallen", "Grand Theft Auto VI"], false),   // fixture, no abbreviation
    ArtistCase("Morgan Wallen & Grand Theft Auto VI", ["Morgan Wallen", "Grand Theft Auto VI"], true), // fixture
]

@Suite struct JudgeTests {
    let judge = FuzzyAnswerJudge()

    @Test(arguments: titleCases)
    func title(_ c: TitleCase) {
        let t = Track(id: "i", uri: "spotify:track:i", title: c.title, artists: c.artists, durationMs: 1, previewURL: nil)
        #expect(judge.titleMatches(c.guess, track: t) == c.expected)
    }

    @Test(arguments: artistCases)
    func artist(_ c: ArtistCase) {
        #expect(judge.artistMatches(c.guess, artists: c.artists) == c.expected)
    }

    @Test func verdictNeedsBothParts() {
        let t = track("BbY WOW", "KAROL G", "Judeline", "rusowsky")
        #expect(judge.judge(Guess(title: "bby wow", artist: "rusowsky, karol g & judeline"), against: t).isCorrect)
        #expect(judge.judge(Guess(title: "bby wow", artist: "rusowsky"), against: t)
                == Verdict(titleCorrect: true, artistCorrect: false))
        #expect(judge.judge(Guess(title: "wow", artist: "Karol G, Judeline, rusowsky"), against: t)
                == Verdict(titleCorrect: false, artistCorrect: true))
        #expect(judge.judge(Guess(title: "", artist: ""), against: t)
                == Verdict(titleCorrect: false, artistCorrect: false))
    }

    @Test func wholeStringGuardStopsTyposPilingUp() {
        // Each word is within its own tolerance, but together too much is wrong.
        #expect(FuzzyAnswerJudge.wordsAlign(["dia", "dia"], ["dai", "dai"]))
        #expect(!judge.titleMatches("Dia Dia", track: track("Dai Dai", "X")))
        #expect(FuzzyAnswerJudge(wholeStringThreshold: 0.6).titleMatches("Dia Dia", track: track("Dai Dai", "X")))
    }

    @Test func tolerancePerWordLength() {
        #expect([1, 2, 3, 5, 6, 9, 10, 20].map(FuzzyAnswerJudge.tolerance) == [0, 0, 1, 1, 2, 2, 3, 3])
    }

    @Test func damerauLevenshtein() {
        #expect(FuzzyAnswerJudge.damerauLevenshtein(Array("kitten"), Array("sitting")) == 3)
        #expect(FuzzyAnswerJudge.damerauLevenshtein(Array("grande"), Array("grnade")) == 1)
        #expect(FuzzyAnswerJudge.damerauLevenshtein(Array(""), Array("abc")) == 3)
        #expect(FuzzyAnswerJudge.damerauLevenshtein(Array("abc"), Array("abc")) == 0)
    }

    @Test func normalization() {
        #expect(FuzzyAnswerJudge.tokens("Beyoncé & Jay-Z") == ["beyonce", "and", "jay", "z"])
        #expect(FuzzyAnswerJudge.tokens("Ain’t  It\u{00A0}Fun!") == ["aint", "it", "fun"])
        #expect(FuzzyAnswerJudge.tokens("Sigur Rós, Mø") == ["sigur", "ros", "mo"])
    }
}
