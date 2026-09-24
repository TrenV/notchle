import Foundation
import Testing
@testable import NotchleCore

// Runs the shared, data-driven spec in /spec (see spec/README.md) against the Swift reference.
// The Windows port (windows/tests/Notchle.Core.Tests/Spec*Tests.cs) runs the same files, so a
// green run on both sides means both judges and both shuffles agree case for case.

enum Spec {
    /// <repo>/spec, found from this file so it works from any working directory.
    static let directory = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()  // NotchleCoreTests
        .deletingLastPathComponent()  // Tests
        .deletingLastPathComponent()  // repository root
        .appendingPathComponent("spec", isDirectory: true)

    static func load<T: Decodable>(_ name: String, as type: T.Type = T.self) throws -> T {
        try JSONDecoder().decode(T.self, from: Data(contentsOf: directory.appendingPathComponent(name)))
    }

    /// Loaded once for the parameterized tests; `specFilesLoad` fails loudly if this is empty.
    static let judge: JudgeSpec = (try? load("judge-cases.json")) ?? JudgeSpec(title: [], artist: [], verdict: [], tokens: [])
}

struct JudgeSpec: Decodable, Sendable {
    struct TitleCase: Decodable, Sendable, CustomTestStringConvertible {
        let guess: String
        let title: String
        let artists: [String]
        let expected: Bool
        let note: String
        var testDescription: String { "\"\(guess)\" vs \"\(title)\" → \(expected) (\(note))" }
    }

    struct ArtistCase: Decodable, Sendable, CustomTestStringConvertible {
        let guess: String
        let artists: [String]
        let expected: Bool
        let note: String
        var testDescription: String { "\"\(guess)\" vs \(artists) → \(expected) (\(note))" }
    }

    struct VerdictCase: Decodable, Sendable, CustomTestStringConvertible {
        let guessTitle: String
        let guessArtist: String
        let title: String
        let artists: [String]
        let titleCorrect: Bool
        let artistCorrect: Bool
        let note: String
        var testDescription: String { note }
    }

    struct TokensCase: Decodable, Sendable, CustomTestStringConvertible {
        let input: String
        let tokens: [String]
        let note: String
        var testDescription: String { "\"\(input)\" (\(note))" }
    }

    let title: [TitleCase]
    let artist: [ArtistCase]
    let verdict: [VerdictCase]
    let tokens: [TokensCase]
}

struct ShuffleSpec: Decodable, Sendable {
    struct Generator: Decodable, Sendable {
        let seed: String
        let outputs: [String]
    }

    struct Shuffle: Decodable, Sendable {
        let seed: String
        let count: Int
        let order: [Int]
    }

    struct Engine: Decodable, Sendable {
        let seed: String
        let tracks: Int
        let setSize: Int
        let firstSet: [String]
        let replaySet: [String]
        let secondSet: [String]
    }

    let splitmix64: [Generator]
    let shuffle: [Shuffle]
    let engine: [Engine]
}

@Suite struct SpecJudgeCasesTests {
    let judge = FuzzyAnswerJudge()

    @Test func specFilesLoad() throws {
        let spec = try Spec.load("judge-cases.json", as: JudgeSpec.self)
        #expect(!spec.title.isEmpty && !spec.artist.isEmpty && !spec.verdict.isEmpty && !spec.tokens.isEmpty)
        #expect(spec.title.count == Spec.judge.title.count)
        _ = try Spec.load("shuffle-vectors.json", as: ShuffleSpec.self)
    }

    /// The JSON was extracted from the tables in JudgeTests.swift; it must stay a faithful copy.
    @Test func specMirrorsTheJudgeTestTables() throws {
        let spec = try Spec.load("judge-cases.json", as: JudgeSpec.self)
        #expect(spec.title.map { [$0.guess, $0.title, $0.artists.joined(separator: "\n"), "\($0.expected)"] }
                == titleCases.map { [$0.guess, $0.title, $0.artists.joined(separator: "\n"), "\($0.expected)"] })
        #expect(spec.artist.map { [$0.guess, $0.artists.joined(separator: "\n"), "\($0.expected)"] }
                == artistCases.map { [$0.guess, $0.artists.joined(separator: "\n"), "\($0.expected)"] })
    }

    @Test(arguments: Spec.judge.title)
    func title(_ c: JudgeSpec.TitleCase) {
        let t = Track(id: "i", uri: "spotify:track:i", title: c.title, artists: c.artists, durationMs: 1, previewURL: nil)
        #expect(judge.titleMatches(c.guess, track: t) == c.expected)
    }

    @Test(arguments: Spec.judge.artist)
    func artist(_ c: JudgeSpec.ArtistCase) {
        #expect(judge.artistMatches(c.guess, artists: c.artists) == c.expected)
    }

    @Test(arguments: Spec.judge.verdict)
    func verdict(_ c: JudgeSpec.VerdictCase) {
        let t = Track(id: "i", uri: "spotify:track:i", title: c.title, artists: c.artists, durationMs: 1, previewURL: nil)
        #expect(judge.judge(Guess(title: c.guessTitle, artist: c.guessArtist), against: t)
                == Verdict(titleCorrect: c.titleCorrect, artistCorrect: c.artistCorrect))
    }

    @Test(arguments: Spec.judge.tokens)
    func tokens(_ c: JudgeSpec.TokensCase) {
        #expect(FuzzyAnswerJudge.tokens(c.input) == c.tokens)
    }
}

@Suite struct SpecShuffleVectorTests {
    let spec: ShuffleSpec

    init() throws {
        spec = try Spec.load("shuffle-vectors.json")
    }

    @Test func splitMix64Outputs() throws {
        #expect(!spec.splitmix64.isEmpty)
        for vector in spec.splitmix64 {
            var rng = SplitMix64(seed: try #require(UInt64(vector.seed)))
            let expected = try vector.outputs.map { try #require(UInt64($0)) }
            #expect(expected.map { _ in rng.next() } == expected)
        }
    }

    @Test func shuffleOrder() throws {
        #expect(!spec.shuffle.isEmpty)
        for vector in spec.shuffle {
            var rng = SplitMix64(seed: try #require(UInt64(vector.seed)))
            #expect(rng.shuffled(Array(0..<vector.count)) == vector.order)
        }
    }

    /// Same scenario as the C# SpecShuffleVectorTests: first set, replay after 0/20, next set after 20/20.
    @Test func engineSets() throws {
        #expect(!spec.engine.isEmpty)
        for vector in spec.engine {
            let ref = SourceRef(kind: .playlist, id: "37i9dQZF1DXcBWIGoYBM5M")
            let listing = SourceListing(ref: ref, name: "Spec", tracks: (0..<vector.tracks).map { i in
                Track(id: "t\(i)", uri: "spotify:track:t\(i)", title: "Song \(i)", artists: ["Artist"],
                      durationMs: 1, previewURL: nil)
            })
            var e = GameEngine(config: GameConfig(setSize: vector.setSize), seed: try #require(UInt64(vector.seed)))
            _ = e.send(.load(ref))
            _ = e.send(.loaded(listing))
            #expect(e.state.currentSet.map(\.id) == vector.firstSet)

            for _ in vector.firstSet {
                _ = e.send(.giveUp)
                _ = e.send(.next)
            }
            #expect(e.state.phase == .setFailed(correctCount: 0))
            _ = e.send(.replaySet)
            #expect(e.state.currentSet.map(\.id) == vector.replaySet)

            for _ in vector.replaySet {
                _ = e.send(.submit(Guess(title: try #require(e.state.currentTrack).title, artist: "Artist")))
                _ = e.send(.next)
            }
            #expect(e.state.phase == .setComplete(correctCount: vector.replaySet.count))
            _ = e.send(.nextSet)
            #expect(e.state.currentSet.map(\.id) == vector.secondSet)
        }
    }
}
