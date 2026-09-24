namespace Notchle.Core;

// STUB: agent W1 replaces this. Public API frozen.
public sealed class GameEngine
{
    private readonly IAnswerJudge _judge;
    private ulong _seed;

    public GameState State { get; private set; }

    /// `seed` drives every shuffle (SplitMix64, same algorithm as the Swift engine).
    public GameEngine(GameConfig config, IReadOnlySet<string> clearedTrackIds, IAnswerJudge judge, ulong seed)
    {
        _judge = judge;
        _seed = seed;
        State = new GameState { Config = config, ClearedTrackIds = clearedTrackIds };
    }

    /// Applies the action and returns effects in order. Out-of-phase actions: no change, no effects.
    public IReadOnlyList<GameEffect> Send(GameAction action) => Array.Empty<GameEffect>();
}
