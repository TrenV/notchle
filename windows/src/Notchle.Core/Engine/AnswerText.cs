using System.Globalization;
using System.Text;

namespace Notchle.Core;

/// Text handling for FuzzyAnswerJudge that reproduces what the Swift judge gets from Foundation:
/// `folding([.caseInsensitive, .diacriticInsensitive, .widthInsensitive]).lowercased()`, and
/// Swift's Character (extended grapheme cluster) semantics for lengths, distances and
/// letter/number tests.
///
/// Foundation's folding was measured on macOS 26 (scratch probes, 2026-09-24) and is pinned by
/// the "tokens" vectors in /spec/judge-cases.json. Per grapheme cluster:
/// - width: a halfwidth/fullwidth base (U+FF00–U+FFEF) becomes its normal form ("ＢＴＳ" = "BTS").
/// - case: full Unicode case folding, then lowercase ("ß"/"ẞ" = "ss", "ﬁ" = "fi", "ς" = "σ").
/// - diacritics: after a Latin, Greek or Cyrillic base (below U+0510 once decomposed and
///   folded) every combining mark is dropped. After any other base only U+0300–U+036F marks
///   are dropped, and only when folding left the base unchanged. So Japanese dakuten, Hebrew
///   and Arabic vowel marks, Thai and Devanagari signs are kept ("ポ" is not "ホ"); a plain
///   "strip every non-spacing mark" would change those scripts.
/// Residual differences (measured over every code point, alone and with marks) are Unicode
/// version drift only: characters that .NET's tables do not know yet, and Apple private-use
/// characters.
internal static class AnswerText
{
    /// Case foldings that differ from lowercasing, for characters that survive canonical
    /// decomposition (CaseFolding.txt entries C/F where fold != lower; the rest decompose into
    /// a base + marks first, and the marks are dropped). U+1C80–U+1C88 are left out on purpose:
    /// Foundation (macOS 26) does not fold them.
    private static readonly Dictionary<int, string> CaseFoldExtras = new()
    {
        [0x00B5] = "\u03BC", [0x00DF] = "ss", [0x0149] = "\u02BCn", [0x017F] = "s",
        [0x03C2] = "\u03C3", [0x03D0] = "\u03B2", [0x03D1] = "\u03B8", [0x03D5] = "\u03C6",
        [0x03D6] = "\u03C0", [0x03F0] = "\u03BA", [0x03F1] = "\u03C1", [0x03F5] = "\u03B5",
        [0x0587] = "\u0565\u0582",
        [0x0345] = "\u03B9", [0x1E9A] = "a\u02BE", [0x1E9E] = "ss",
        [0xFB00] = "ff", [0xFB01] = "fi", [0xFB02] = "fl", [0xFB03] = "ffi", [0xFB04] = "ffl",
        [0xFB05] = "st", [0xFB06] = "st",
        [0xFB13] = "\u0574\u0576", [0xFB14] = "\u0574\u0565", [0xFB15] = "\u0574\u056B",
        [0xFB16] = "\u057E\u0576", [0xFB17] = "\u0574\u056D",
    };

    /// Foundation's folding, cluster by cluster.
    public static string Fold(string s)
    {
        var output = new StringBuilder(s.Length);
        foreach (var cluster in Clusters(WellFormed(s)))
        {
            var runes = Normalize(cluster, NormalizationForm.FormD).EnumerateRunes().ToArray();
            if (runes.Length == 0) continue;

            var baseText = runes[0].ToString();
            var foldedBase = FoldCase(Normalize(FoldWidth(runes[0]), NormalizationForm.FormD));
            var folded = new StringBuilder(foldedBase);
            var latinGreekCyrillic = foldedBase.EnumerateRunes().FirstOrDefault().Value < 0x0510;
            var baseChanged = foldedBase != baseText;

            var changed = baseChanged;
            foreach (var mark in runes.AsSpan(1))
            {
                if (latinGreekCyrillic ? IsDroppableMark(mark) : !baseChanged && mark.Value is >= 0x0300 and <= 0x036F)
                {
                    changed = true;
                    continue;
                }
                var lower = Rune.ToLowerInvariant(mark);
                changed |= lower != mark;
                folded.Append(lower.ToString());
            }
            // Untouched clusters keep their original code points (e.g. composition exclusions
            // like U+0958), as Foundation does; equality is the same either way.
            output.Append(changed ? Normalize(folded.ToString(), NormalizationForm.FormC) : cluster);
        }
        return output.ToString();
    }

    /// Swift strings cannot hold lone surrogates (and Normalize throws on them): read them as U+FFFD.
    private static string WellFormed(string s)
    {
        foreach (var c in s)
        {
            if (char.IsSurrogate(c))
            {
                var sb = new StringBuilder(s.Length);
                foreach (var r in s.EnumerateRunes()) sb.Append(r.ToString());
                return sb.ToString();
            }
        }
        return s;
    }

    /// .NET (ICU) refuses to normalize noncharacters such as U+FFFE; such a cluster stays as is
    /// instead of crashing the judge.
    private static string Normalize(string s, NormalizationForm form)
    {
        try { return s.Normalize(form); }
        catch (ArgumentException) { return s; }
    }

    private static string FoldWidth(Rune r) =>
        r.Value is >= 0xFF00 and <= 0xFFEF ? Normalize(r.ToString(), NormalizationForm.FormKC) : r.ToString();

    private static string FoldCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            if (CaseFoldExtras.TryGetValue(r.Value, out var folded)) sb.Append(folded);
            else sb.Append(Rune.ToLowerInvariant(r).ToString());
        }
        return sb.ToString();
    }

    /// Grapheme-extending marks Foundation drops after a Latin/Greek/Cyrillic base. Spacing
    /// marks (Mc) and the zero-width joiner are kept, as measured.
    private static bool IsDroppableMark(Rune r)
    {
        var category = Rune.GetUnicodeCategory(r);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
            || r.Value is 0x200C or 0xFF9E or 0xFF9F or (>= 0x1F3FB and <= 0x1F3FF) or (>= 0xE0020 and <= 0xE007F);
    }

    /// Swift's Character.isLetter (Alphabetic property of the first scalar) || isNumber (has a
    /// numeric type): .NET's letter and number categories plus the marks and symbols Unicode
    /// lists as Other_Alphabetic (a cluster can start with one, e.g. a mark after a space).
    public static bool IsLetterOrNumber(string cluster)
    {
        var r = cluster.EnumerateRunes().FirstOrDefault();
        return Rune.IsLetter(r) || Rune.IsNumber(r) || IsOtherAlphabetic(r.Value);
    }

    private static bool IsOtherAlphabetic(int v)
    {
        // Binary search over [start, end] pairs.
        int lo = 0, hi = OtherAlphabetic.Length / 2 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (v < OtherAlphabetic[2 * mid]) hi = mid - 1;
            else if (v > OtherAlphabetic[2 * mid + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// Inclusive ranges of the Mn, Mc and So code points Swift (Unicode.Scalar.Properties
    /// .isAlphabetic, macOS 26) treats as letters. Generated from Swift; gaps between ranges
    /// that hold only letters and numbers are merged.
    private static readonly int[] OtherAlphabetic =
    [
        0x345, 0x345, 0x363, 0x36F, 0x5B0, 0x5BD, 0x5BF, 0x5BF, 0x5C1, 0x5C2, 0x5C4, 0x5C5,
        0x5C7, 0x5C7, 0x610, 0x61A, 0x64B, 0x657, 0x659, 0x65F, 0x670, 0x670, 0x6D6, 0x6DC,
        0x6E1, 0x6E8, 0x6ED, 0x6ED, 0x711, 0x73F, 0x7A6, 0x7B0, 0x816, 0x817, 0x81B, 0x82C,
        0x897, 0x897, 0x8D4, 0x8DF, 0x8E3, 0x8E9, 0x8F0, 0x93B, 0x93E, 0x94C, 0x94E, 0x94F,
        0x955, 0x963, 0x981, 0x983, 0x9BE, 0x9C4, 0x9C7, 0x9C8, 0x9CB, 0x9CC, 0x9D7, 0x9D7,
        0x9E2, 0x9E3, 0xA01, 0xA03, 0xA3E, 0xA42, 0xA47, 0xA48, 0xA4B, 0xA4C, 0xA51, 0xA51,
        0xA70, 0xA75, 0xA81, 0xA83, 0xABE, 0xAC5, 0xAC7, 0xAC9, 0xACB, 0xACC, 0xAE2, 0xAE3,
        0xAFA, 0xAFC, 0xB01, 0xB03, 0xB3E, 0xB44, 0xB47, 0xB48, 0xB4B, 0xB4C, 0xB56, 0xB57,
        0xB62, 0xB63, 0xB82, 0xB82, 0xBBE, 0xBC2, 0xBC6, 0xBC8, 0xBCA, 0xBCC, 0xBD7, 0xBD7,
        0xC00, 0xC04, 0xC3E, 0xC44, 0xC46, 0xC48, 0xC4A, 0xC4C, 0xC55, 0xC56, 0xC62, 0xC63,
        0xC81, 0xC83, 0xCBE, 0xCC4, 0xCC6, 0xCC8, 0xCCA, 0xCCC, 0xCD5, 0xCD6, 0xCE2, 0xCE3,
        0xCF3, 0xCF3, 0xD00, 0xD03, 0xD3E, 0xD44, 0xD46, 0xD48, 0xD4A, 0xD4C, 0xD57, 0xD63,
        0xD81, 0xD83, 0xDCF, 0xDD4, 0xDD6, 0xDD6, 0xDD8, 0xDDF, 0xDF2, 0xDF3, 0xE31, 0xE3A,
        0xE4D, 0xE4D, 0xEB1, 0xEB9, 0xEBB, 0xEBC, 0xECD, 0xECD, 0xF71, 0xF83, 0xF8D, 0xF97,
        0xF99, 0xFBC, 0x102B, 0x1036, 0x1038, 0x1038, 0x103B, 0x103E, 0x1056, 0x109D, 0x1712, 0x1713,
        0x1732, 0x1733, 0x1752, 0x1753, 0x1772, 0x1773, 0x17B6, 0x17C8, 0x1885, 0x18A9, 0x1920, 0x192B,
        0x1930, 0x1938, 0x1A17, 0x1A1B, 0x1A55, 0x1A5E, 0x1A61, 0x1A74, 0x1ABF, 0x1AC0, 0x1ACC, 0x1ACE,
        0x1B00, 0x1B04, 0x1B35, 0x1B43, 0x1B80, 0x1BA9, 0x1BAC, 0x1BAD, 0x1BE7, 0x1BF1, 0x1C24, 0x1C36,
        0x1DD3, 0x1DF4, 0x24B6, 0x24E9, 0x2DE0, 0x2DFF, 0xA674, 0xA67B, 0xA69E, 0xA69F, 0xA802, 0xA802,
        0xA80B, 0xA827, 0xA880, 0xA8C3, 0xA8C5, 0xA8C5, 0xA8FF, 0xA92A, 0xA947, 0xA952, 0xA980, 0xA983,
        0xA9B4, 0xA9BF, 0xA9E5, 0xA9E5, 0xAA29, 0xAA36, 0xAA43, 0xAA4D, 0xAA7B, 0xAABE, 0xAAEB, 0xAAEF,
        0xAAF5, 0xAAF5, 0xABE3, 0xABEA, 0xFB1E, 0xFB1E, 0x10376, 0x1037A, 0x10A01, 0x10A03,
        0x10A05, 0x10A06, 0x10A0C, 0x10A0F, 0x10D24, 0x10D27, 0x10D69, 0x10D69, 0x10EAB, 0x10EAC,
        0x10EFC, 0x10EFC, 0x11000, 0x11045, 0x11073, 0x11074, 0x11080, 0x110B8, 0x110C2, 0x110C2,
        0x11100, 0x11132, 0x11145, 0x11146, 0x11180, 0x111BF, 0x111CE, 0x111CF, 0x1122C, 0x11234,
        0x11237, 0x11237, 0x1123E, 0x11241, 0x112DF, 0x112E8, 0x11300, 0x11303, 0x1133E, 0x11344,
        0x11347, 0x11348, 0x1134B, 0x1134C, 0x11357, 0x11357, 0x11362, 0x11363, 0x113B8, 0x113C0,
        0x113C2, 0x113C2, 0x113C5, 0x113C5, 0x113C7, 0x113CA, 0x113CC, 0x113CD, 0x11435, 0x11441,
        0x11443, 0x11445, 0x114B0, 0x114C1, 0x115AF, 0x115B5, 0x115B8, 0x115BE, 0x115DC, 0x115DD,
        0x11630, 0x1163E, 0x11640, 0x11640, 0x116AB, 0x116B5, 0x1171D, 0x1172A, 0x1182C, 0x11838,
        0x11930, 0x11935, 0x11937, 0x11938, 0x1193B, 0x1193C, 0x11940, 0x11942, 0x119D1, 0x119D7,
        0x119DA, 0x119DF, 0x119E4, 0x119E4, 0x11A01, 0x11A0A, 0x11A35, 0x11A3E, 0x11A51, 0x11A97,
        0x11C2F, 0x11C36, 0x11C38, 0x11C3E, 0x11C92, 0x11CA7, 0x11CA9, 0x11CB6, 0x11D31, 0x11D36,
        0x11D3A, 0x11D3A, 0x11D3C, 0x11D3D, 0x11D3F, 0x11D41, 0x11D43, 0x11D43, 0x11D47, 0x11D47,
        0x11D8A, 0x11D8E, 0x11D90, 0x11D91, 0x11D93, 0x11D96, 0x11EF3, 0x11EF6, 0x11F00, 0x11F03,
        0x11F34, 0x11F3A, 0x11F3E, 0x11F40, 0x1611E, 0x1612E, 0x16F4F, 0x16F87, 0x16F8F, 0x16F92,
        0x16FF0, 0x16FF1, 0x1BC9E, 0x1BC9E, 0x1E000, 0x1E006, 0x1E008, 0x1E018, 0x1E01B, 0x1E021,
        0x1E023, 0x1E024, 0x1E026, 0x1E02A, 0x1E08F, 0x1E08F, 0x1E947, 0x1E947, 0x1F130, 0x1F149,
        0x1F150, 0x1F169, 0x1F170, 0x1F189,
    ];

    /// Swift's Character.isNumber for the first scalar.
    public static bool IsNumber(string cluster) => Rune.IsNumber(cluster.EnumerateRunes().FirstOrDefault());

    /// Extended grapheme clusters, i.e. Swift's Characters.
    public static string[] Clusters(string s)
    {
        if (s.Length == 0) return [];
        if (IsAscii(s))
        {
            // Fast path; "\r\n" is the only multi-character ASCII cluster.
            if (!s.Contains("\r\n", StringComparison.Ordinal))
            {
                var chars = new string[s.Length];
                for (var i = 0; i < s.Length; i++) chars[i] = s[i].ToString();
                return chars;
            }
        }
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list.ToArray();
    }

    /// Swift's String.count.
    public static int Length(string s) => IsAscii(s) && !s.Contains("\r\n", StringComparison.Ordinal)
        ? s.Length
        : new StringInfo(s).LengthInTextElements;

    private static bool IsAscii(string s)
    {
        foreach (var c in s) if (c > 0x7F) return false;
        return true;
    }
}
