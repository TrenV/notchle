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
    // Positives
    TitleCase("hate that i made you love me", "hate that i made you love me", true),         // fixture
    TitleCase("Hate That I Made You Love Me", "hate that i made you love me", true),         // case
    TitleCase("hate that i made u love me", "hate that i made you love me", true),           // fuzzy
    TitleCase("bby wow", "BbY WOW", true),                                                   // fixture, case
    TitleCase("Aint in LA", "Ain't In LA", true),                                            // fixture, apostrophe
    TitleCase("choosing texas", "Choosin' Texas", true),                                     // fixture, fuzzy
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
    TitleCase("dtmf", "DtMF", true),                                                         // fixture, short exact
    TitleCase("Bass Persuade", "Bass Persuades", true),                                      // fixture, fuzzy
    TitleCase("Here Comes the Sun", "Here Comes The Sun - Remastered 2019", true),
    TitleCase("Mr Brightside", "Mr. Brightside [Remastered]", true),
    TitleCase("Heroes", "\"Heroes\" - 2017 Remaster", true),
    TitleCase("Halo", "Halo - Live at Wembley", true),
    TitleCase("Déjà vu", "deja vu", true),                                                   // diacritics
    TitleCase("ＳＷＩＭ", "SWIM", true),                                                       // width folding
    TitleCase("Rock & Roll", "Rock and Roll", true),                                         // "&" = "and"
    // Negatives
    TitleCase("love", "Crazy in Love", false),                                               // no substring
    TitleCase("Midnight", "Midnight Sun", false),                                            // fixture, no partial
    TitleCase("22", "23", false),                                                            // short, exact only
    TitleCase("lose", "Loser", false),                                                       // fixture, 0.8 < 0.85
    TitleCase("swimm", "SWIM", false),                                                       // fixture, short exact
    TitleCase("Summer of 68", "Summer of 69", false),                                        // numbers exact
    TitleCase("Debi Tirar Mas Fotos", "DtMF", false),                                        // fixture, no expansion
    TitleCase("Janice", "Janice STFU", false),                                               // fixture, partial
    TitleCase("", "Animal", false),                                                          // empty
    TitleCase("   ", "Animal", false),                                                       // blank
    TitleCase("Remastered 2011", "Yesterday - Remastered 2011", false),                      // decoration alone
    TitleCase("the", "the cure", false),                                                     // fixture
    TitleCase("petals", "petal", false),                                                     // fixture, 5 chars, 0.83
]

let artistCases: [ArtistCase] = [
    // Positives
    ArtistCase("Ariana Grande", ["Ariana Grande"], true),                                    // fixture
    ArtistCase("ariana grand", ["Ariana Grande"], true),                                     // fuzzy
    ArtistCase("Judeline", ["KAROL G", "Judeline", "rusowsky"], true),                       // fixture, any credited
    ArtistCase("karol g", ["KAROL G", "Judeline", "rusowsky"], true),                        // fixture
    ArtistCase("Rosé", ["ROSÉ"], true),                                                      // fixture
    ArtistCase("rose", ["ROSÉ"], true),                                                      // fixture, diacritics
    ArtistCase("Adela", ["ADÉLA"], true),                                                    // fixture
    ArtistCase("Beyonce", ["Beyoncé"], true),
    ArtistCase("Solto", ["HUGEL", "SOLTO (FR)"], true),                                      // fixture, bracket
    ArtistCase("Dave & Tems", ["Dave", "Tems"], true),                                       // fixture, list
    ArtistCase("Sam Fender, Olivia Dean", ["Sam Fender", "Olivia Dean"], true),              // fixture, list
    ArtistCase("Mumford and Sons", ["Mumford & Sons"], true),
    ArtistCase("Weeknd", ["The Weeknd"], true),                                              // leading "the"
    ArtistCase("B.T.S.", ["BTS"], true),                                                     // fixture, punctuation
    ArtistCase("Bad Buny", ["Bad Bunny"], true),                                             // fixture, fuzzy
    ArtistCase("Tyler the Creator", ["Tyler, The Creator"], true),
    // Negatives
    ArtistCase("Olivia Rodrigo", ["Ariana Grande"], false),
    ArtistCase("Olivia", ["Olivia Dean"], false),                                            // fixture, partial
    ArtistCase("Karol", ["KAROL G", "Judeline", "rusowsky"], false),                         // fixture, 0.83
    ArtistCase("Simon", ["Simon & Garfunkel"], false),                                       // partial
    ArtistCase("Dave & Stormzy", ["Dave", "Tems"], false),                                   // list with an uncredited name
    ArtistCase("Ashe", ["RAYE"], false),                                                     // fixture, wrong artist
    ArtistCase("Ray", ["RAYE"], false),                                                      // fixture, short exact
    ArtistCase("GTA", ["Morgan Wallen", "Grand Theft Auto VI"], false),                      // fixture
    ArtistCase("", ["Drake"], false),
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
        #expect(judge.judge(Guess(title: "bby wow", artist: "rusowsky"), against: t).isCorrect)
        #expect(judge.judge(Guess(title: "bby wow", artist: "Shakira"), against: t)
                == Verdict(titleCorrect: true, artistCorrect: false))
        #expect(judge.judge(Guess(title: "wow", artist: "Karol G"), against: t)
                == Verdict(titleCorrect: false, artistCorrect: true))
        #expect(judge.judge(Guess(title: "", artist: ""), against: t)
                == Verdict(titleCorrect: false, artistCorrect: false))
    }

    @Test func shortAnswersNeedExactMatchEvenWithALooseThreshold() {
        let loose = FuzzyAnswerJudge(similarityThreshold: 0.7)
        let raye = track("Boston", "RAYE")
        #expect(!loose.artistMatches("Ray", artists: raye.artists))           // 4 chars: 0.75 but exact-only
        #expect(!loose.titleMatches("Bos", track: track("Bost", "X")))
        #expect(loose.titleMatches("lose", track: track("Loser", "X")))       // 5 chars: 0.8 >= 0.7
        #expect(!judge.titleMatches("lose", track: track("Loser", "X")))      // default 0.85 rejects
    }

    @Test func levenshtein() {
        #expect(FuzzyAnswerJudge.levenshtein(Array("kitten"), Array("sitting")) == 3)
        #expect(FuzzyAnswerJudge.levenshtein(Array(""), Array("abc")) == 3)
        #expect(FuzzyAnswerJudge.levenshtein(Array("abc"), Array("abc")) == 0)
    }

    @Test func normalization() {
        #expect(FuzzyAnswerJudge.tokens("Beyoncé & Jay-Z") == ["beyonce", "and", "jay", "z"])
        #expect(FuzzyAnswerJudge.tokens("Ain’t  It\u{00A0}Fun!") == ["aint", "it", "fun"])
        #expect(FuzzyAnswerJudge.tokens("Sigur Rós, Mø") == ["sigur", "ros", "mo"])
    }
}
