namespace Notchle.Core;

// STUB: agent W1 replaces this.
public sealed class FuzzyAnswerJudge : IAnswerJudge
{
    public Verdict Judge(Guess guess, Track track) => new(false, false);
}
