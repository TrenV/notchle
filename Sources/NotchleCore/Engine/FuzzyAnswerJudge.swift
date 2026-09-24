import Foundation

// STUB — Wave 2, agent C replaces this file.
public struct FuzzyAnswerJudge: AnswerJudging {
    public init() {}

    public func judge(_ guess: Guess, against track: Track) -> Verdict {
        Verdict(titleCorrect: false, artistCorrect: false)
    }
}
