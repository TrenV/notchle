using System.Text.Json;
using Notchle.Core;

namespace Notchle.Core.Tests;

/// /spec/shuffle-vectors.json, computed from the Swift SplitMix64 and GameEngine: the same seed
/// must give the same numbers, the same shuffle and the same sets on both platforms.
public class SpecShuffleVectorTests
{
    private static readonly JsonElement Vectors = Spec.Load("shuffle-vectors.json");

    [Fact]
    public void SplitMix64MatchesSwiftOutputs()
    {
        Assert.NotEqual(0, Vectors.GetProperty("splitmix64").GetArrayLength());
        foreach (var vector in Vectors.GetProperty("splitmix64").EnumerateArray())
        {
            var rng = new SplitMix64(ulong.Parse(vector.GetProperty("seed").GetString()!));
            var expected = vector.GetProperty("outputs").Strings().Select(ulong.Parse).ToArray();
            Assert.Equal(expected, expected.Select(_ => rng.Next()).ToArray());
        }
    }

    [Fact]
    public void ShuffleMatchesSwiftOrder()
    {
        foreach (var vector in Vectors.GetProperty("shuffle").EnumerateArray())
        {
            var rng = new SplitMix64(ulong.Parse(vector.GetProperty("seed").GetString()!));
            var order = vector.GetProperty("order").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.Equal(order, rng.Shuffled(Enumerable.Range(0, vector.GetProperty("count").GetInt32())));
        }
    }

    /// Same scenario as the Swift SpecShuffleVectorTests.engineSets.
    [Fact]
    public void EngineSetsMatchSwift()
    {
        foreach (var vector in Vectors.GetProperty("engine").EnumerateArray())
        {
            var source = new SourceRef(SourceKind.Playlist, "37i9dQZF1DXcBWIGoYBM5M");
            var tracks = Enumerable.Range(0, vector.GetProperty("tracks").GetInt32())
                .Select(i => new Track($"t{i}", $"spotify:track:t{i}", $"Song {i}", ["Artist"], 1, null))
                .ToArray();
            var config = GameConfig.Default with { SetSize = vector.GetProperty("setSize").GetInt32() };
            var e = new GameEngine(config, new HashSet<string>(), new FuzzyAnswerJudge(),
                ulong.Parse(vector.GetProperty("seed").GetString()!));
            e.Send(new GameAction.Load(source));
            e.Send(new GameAction.Loaded(new SourceListing(source, "Spec", tracks)));
            Assert.Equal(vector.GetProperty("firstSet").Strings(), e.State.CurrentSet.Select(t => t.Id));

            for (var i = 0; i < vector.GetProperty("firstSet").GetArrayLength(); i++)
            {
                e.Send(new GameAction.GiveUp());
                e.Send(new GameAction.Next());
            }
            Assert.Equal(new GamePhase.SetFailed(0), e.State.Phase);
            e.Send(new GameAction.ReplaySet());
            Assert.Equal(vector.GetProperty("replaySet").Strings(), e.State.CurrentSet.Select(t => t.Id));

            for (var i = 0; i < vector.GetProperty("replaySet").GetArrayLength(); i++)
            {
                e.Send(new GameAction.Submit(new Guess(e.State.CurrentTrack!.Title, "Artist")));
                e.Send(new GameAction.Next());
            }
            Assert.Equal(new GamePhase.SetComplete(vector.GetProperty("replaySet").GetArrayLength()), e.State.Phase);
            e.Send(new GameAction.NextSet());
            Assert.Equal(vector.GetProperty("secondSet").Strings(), e.State.CurrentSet.Select(t => t.Id));
        }
    }
}
