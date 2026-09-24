namespace Notchle.Core;

/// Typo-tolerant answer checking: the gist must be right, the spelling need not be.
/// Port of Sources/NotchleCore/Engine/FuzzyAnswerJudge.swift (rules agreed with Tren on
/// 2026-09-24); both implementations must pass the shared vectors in /spec/judge-cases.json.
///
/// Normalization (both sides): case, diacritic and width folding ("Beyoncé" = "beyonce",
/// "ＢＴＳ" = "BTS"; see AnswerText), "&amp;" and "+" read as "and", apostrophes dropped
/// ("ain't" = "aint"), other punctuation treated as a space, and an optional leading "the".
///
/// Matching a guess against an answer (a title, or one artist name):
/// - Word by word, in order, every word covered: no missing and no extra words. Each answer
///   word allows a Damerau-Levenshtein distance of 0 for 1-2 characters, 1 for 3-5, 2 for 6-9
///   and 3 for 10 or more.
/// - Spacing may differ ("Man child" = "Manchild"); a joined match allows only the tolerance of
///   its shortest word, so a short word cannot silently vanish ("Karol" is not "KAROL G").
/// - Numbers must be identical ("Summer of 68" is not "Summer of 69").
/// - On top of that the whole string, spaces removed, needs a similarity
///   (1 - distance / longer length) of at least 0.75, so typos cannot pile up.
/// - Title: the full title or the title without decorations (brackets, dash suffix, "feat."
///   tail, trailing "+ / &amp; / with / x &lt;credited artist&gt;").
/// - Artists: ALL credited artists, any order, typos allowed, nothing else; only separators
///   between names; the guess is segmented against the credited names.
/// - An empty guess is wrong.
///
/// Lengths and distances count grapheme clusters, like Swift's Characters.
public sealed class FuzzyAnswerJudge : IAnswerJudge
{
    /// Minimum whole-string similarity, on top of the per-word tolerance.
    private readonly double _wholeStringThreshold;

    public FuzzyAnswerJudge() : this(0.75) { }

    internal FuzzyAnswerJudge(double wholeStringThreshold) => _wholeStringThreshold = wholeStringThreshold;

    public Verdict Judge(Guess guess, Track track) =>
        new(TitleMatches(guess.Title, track), ArtistMatches(guess.Artist, track.Artists));

    // Title

    internal bool TitleMatches(string guess, Track track)
    {
        var guessTokens = Tokens(guess);
        if (guessTokens.Length == 0) return false;
        return TitleVariants(track.Title, track.Artists).Any(variant => Matches(guessTokens, variant));
    }

    internal static List<string[]> TitleVariants(string title, IReadOnlyList<string> artists)
    {
        var variants = new List<string[]> { Tokens(title) };
        var unbracketed = RemovingBrackets(title);
        variants.Add(Tokens(unbracketed));
        var stripped = Tokens(BeforeDashSuffix(unbracketed));
        variants.Add(stripped);
        stripped = BeforeFeaturing(stripped);
        variants.Add(stripped);
        variants.Add(WithoutTrailingArtist(stripped, artists));
        var unique = new List<string[]>();
        foreach (var v in variants)
        {
            if (v.Length > 0 && !unique.Any(u => u.SequenceEqual(v))) unique.Add(v);
        }
        return unique;
    }

    /// Drops "( … )" and "[ … ]" parts, nested or not.
    internal static string RemovingBrackets(string s)
    {
        var output = new System.Text.StringBuilder(s.Length);
        var depth = 0;
        foreach (var ch in AnswerText.Clusters(s))
        {
            switch (ch)
            {
                case "(" or "[": depth++; break;
                case ")" or "]": if (depth > 0) depth--; else output.Append(' '); break;
                default: if (depth == 0) output.Append(ch); break;
            }
        }
        return output.ToString();
    }

    /// Cuts at the first spaced dash: "Song - Remastered 2011" → "Song".
    internal static string BeforeDashSuffix(string s)
    {
        var cut = new[] { " - ", " \u2013 ", " \u2014 " }
            .Select(dash => s.IndexOf(dash, StringComparison.Ordinal))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        return cut >= 0 ? s[..cut] : s;
    }

    internal static string[] BeforeFeaturing(string[] tokens)
    {
        var i = Array.FindIndex(tokens, t => t is "feat" or "ft" or "featuring");
        return i > 0 ? tokens[..i] : tokens;
    }

    /// "stateside and zara larsson" → "stateside" when Zara Larsson is a credited artist.
    internal static string[] WithoutTrailingArtist(string[] tokens, IReadOnlyList<string> artists)
    {
        foreach (var artist in artists)
        {
            var a = Tokens(RemovingBrackets(artist));
            if (a.Length == 0 || tokens.Length <= a.Length + 1) continue;
            var connectorIndex = tokens.Length - a.Length - 1;
            if (tokens[(connectorIndex + 1)..].SequenceEqual(a) && tokens[connectorIndex] is "and" or "with" or "x")
                return tokens[..connectorIndex];
        }
        return tokens;
    }

    // Artist

    /// Separator words allowed between artist names ("×", commas and "/" are already spaces).
    internal static readonly IReadOnlySet<string> ArtistSeparators =
        new HashSet<string> { "and", "x", "feat", "ft", "featuring", "with" };

    internal bool ArtistMatches(string guess, IReadOnlyList<string> artists)
    {
        // Each distinct credited artist, with a bracket-free variant ("SOLTO (FR)" → "SOLTO").
        var credited = new List<string[][]>();
        foreach (var artist in artists)
        {
            var full = Tokens(artist);
            var unbracketed = Tokens(RemovingBrackets(artist));
            var variants = (full.SequenceEqual(unbracketed) ? new[] { full } : new[] { full, unbracketed })
                .Where(v => v.Length > 0).ToArray();
            if (variants.Length > 0 && !credited.Any(c => SameVariants(c, variants))) credited.Add(variants);
        }
        var words = Tokens(guess);
        if (words.Length == 0 || credited.Count == 0 || credited.Count >= 64) return false;

        // reachable[j]: sets of credited artists (bitmasks) that words[..j] can be read as, with
        // only separators between them. Each artist is used at most once.
        var all = (1UL << credited.Count) - 1;
        var reachable = new HashSet<ulong>[words.Length + 1];
        for (var k = 0; k < reachable.Length; k++) reachable[k] = [];
        reachable[0].Add(0);
        for (var j = 0; j < words.Length; j++)
        {
            foreach (var mask in reachable[j])
            {
                if (ArtistSeparators.Contains(words[j])) reachable[j + 1].Add(mask);
                for (var end = j + 1; end <= words.Length; end++)
                {
                    var span = words[j..end];
                    for (var i = 0; i < credited.Count; i++)
                    {
                        if ((mask & (1UL << i)) != 0) continue;
                        if (credited[i].Any(variant => Matches(span, variant))) reachable[end].Add(mask | (1UL << i));
                    }
                }
            }
        }
        return reachable[words.Length].Contains(all);
    }

    private static bool SameVariants(string[][] a, string[][] b) =>
        a.Length == b.Length && a.Zip(b).All(pair => pair.First.SequenceEqual(pair.Second));

    // Normalization and matching

    /// Folded, punctuation-free words. "Beyoncé &amp; Jay-Z" → ["beyonce", "and", "jay", "z"].
    internal static string[] Tokens(string s)
    {
        var cleaned = new System.Text.StringBuilder(s.Length);
        foreach (var ch in AnswerText.Clusters(AnswerText.Fold(s)))
        {
            if (ExtraFolds.TryGetValue(ch, out var replacement)) cleaned.Append(replacement);
            else if (Apostrophes.Contains(ch)) continue;
            else if (ch is "&" or "+") cleaned.Append(" and ");
            else if (AnswerText.IsLetterOrNumber(ch)) cleaned.Append(ch);
            else cleaned.Append(' ');
        }
        return cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// Letters that are not base letter + combining mark, so diacritic folding keeps them.
    private static readonly Dictionary<string, string> ExtraFolds = new()
    {
        ["\u00F8"] = "o", ["\u00E6"] = "ae", ["\u0153"] = "oe", ["\u00DF"] = "ss", ["\u0142"] = "l", ["\u0111"] = "d", ["\u00F0"] = "d", ["\u00FE"] = "th", ["\u0131"] = "i",
    };

    private static readonly HashSet<string> Apostrophes = ["'", "\u2019", "\u2018", "`", "\u00B4", "\u02BC"];

    internal bool Matches(string[] guess, string[] answer)
    {
        var g = DropLeadingThe(guess);
        var a = DropLeadingThe(answer);
        var gKey = string.Concat(g);
        var aKey = string.Concat(a);
        if (gKey.Length == 0 || aKey.Length == 0) return false;
        if (gKey == aKey) return true;
        if (!Numbers(g).SequenceEqual(Numbers(a))) return false;
        if (!WordsAlign(g, a)) return false;
        var gChars = AnswerText.Clusters(gKey);
        var aChars = AnswerText.Clusters(aKey);
        var distance = DamerauLevenshtein(gChars, aChars);
        return 1 - (double)distance / Math.Max(gChars.Length, aChars.Length) >= _wholeStringThreshold;
    }

    /// Typos allowed in an answer word of `length` characters.
    internal static int Tolerance(int length) => length switch
    {
        <= 2 => 0,
        <= 5 => 1,
        <= 9 => 2,
        _ => 3,
    };

    /// Every answer word is matched, in order, by guess words; one word may match several joined
    /// words on the other side (spacing differences), with the shortest word's tolerance.
    internal static bool WordsAlign(string[] guess, string[] answer)
    {
        int n = answer.Length, m = guess.Length;
        var ok = new bool[n + 1, m + 1];
        ok[0, 0] = true;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < m; j++)
            {
                if (!ok[i, j]) continue;
                // One answer word <-> one or more guess words.
                for (var k = 1; k <= m - j; k++)
                {
                    var pieces = guess[j..(j + k)];
                    var shortest = Math.Min(AnswerText.Length(answer[i]), pieces.Min(AnswerText.Length));
                    var allowed = k == 1 ? Tolerance(AnswerText.Length(answer[i])) : Tolerance(shortest);
                    if (WithinDistance(string.Concat(pieces), answer[i], allowed)) ok[i + 1, j + k] = true;
                }
                // Several answer words <-> one guess word.
                for (var k = 2; k <= n - i; k++)
                {
                    var pieces = answer[i..(i + k)];
                    var shortest = Math.Min(AnswerText.Length(guess[j]), pieces.Min(AnswerText.Length));
                    if (WithinDistance(guess[j], string.Concat(pieces), Tolerance(shortest))) ok[i + k, j + 1] = true;
                }
            }
        }
        return ok[n, m];
    }

    private static bool WithinDistance(string a, string b, int allowed)
    {
        if (a == b) return true;
        if (allowed <= 0) return false;
        var x = AnswerText.Clusters(a);
        var y = AnswerText.Clusters(b);
        if (Math.Abs(x.Length - y.Length) > allowed) return false;
        return DamerauLevenshtein(x, y) <= allowed;
    }

    private static string[] DropLeadingThe(string[] tokens) =>
        tokens.Length > 1 && tokens[0] == "the" ? tokens[1..] : tokens;

    /// Runs of number characters, e.g. ["summer", "of", "69"] → ["69"].
    private static List<string> Numbers(string[] tokens)
    {
        var runs = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in AnswerText.Clusters(string.Join(' ', tokens)))
        {
            if (AnswerText.IsNumber(ch)) current.Append(ch);
            else if (current.Length > 0) { runs.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) runs.Add(current.ToString());
        return runs;
    }

    /// Optimal-string-alignment distance over grapheme clusters: insert, delete, substitute, or
    /// swap two neighbours.
    internal static int DamerauLevenshtein(string[] a, string[] b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }

    internal static int DamerauLevenshtein(string a, string b) =>
        DamerauLevenshtein(AnswerText.Clusters(a), AnswerText.Clusters(b));
}
