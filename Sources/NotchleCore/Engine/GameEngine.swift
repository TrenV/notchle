import Foundation

// STUB — Wave 2, agent C replaces this file. Public API below is frozen.
public struct GameEngine: Sendable {
    public private(set) var state: GameState
    private let judge: AnswerJudging
    private var seed: UInt64

    /// `seed` drives every shuffle so tests are deterministic; the app passes a random one.
    public init(config: GameConfig = GameConfig(), clearedTrackIDs: Set<String> = [],
                judge: AnswerJudging = FuzzyAnswerJudge(), seed: UInt64) {
        self.state = GameState(config: config, clearedTrackIDs: clearedTrackIDs)
        self.judge = judge
        self.seed = seed
    }

    /// Applies `action` and returns the effects the platform layer must run, in order.
    /// Actions that make no sense in the current phase are ignored (no state change, no effects).
    public mutating func send(_ action: GameAction) -> [GameEffect] {
        []
    }
}
