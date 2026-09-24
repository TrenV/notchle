namespace Notchle.Core.Ui;

/// How island texts fit instead of being cut off: wrap up to a line budget, then shrink the
/// font a little, and if that still is not enough keep wrapping (the open island grows taller,
/// see <see cref="IslandGeometry.ExpandedHeight"/>). Nothing is ever trimmed with an ellipsis.
/// The platform layer supplies the text measurement; the policy lives here so it is tested headless.
public static class IslandTextFit
{
    /// Line budgets before the font shrinks.
    public const int AnswerTitleLines = 3;
    public const int AnswerArtistLines = 2;
    public const int HistoryTitleLines = 2;
    public const int DefaultLines = 2;

    /// Never below this fraction of the design size...
    public const double MinScale = 0.75;
    /// ...and never below this many DIPs (a text already smaller does not shrink at all).
    public const double MinFontSize = 11;
    /// Font sizes tried, in DIPs, from the design size down.
    public const double Step = 0.5;

    /// The smallest size a text designed at <paramref name="designSize"/> may shrink to.
    public static double MinimumSize(double designSize) =>
        Math.Min(designSize, Math.Max(MinFontSize, designSize * MinScale));

    /// The largest font size (design size down to <see cref="MinimumSize"/>) at which the text
    /// takes at most <paramref name="maxLines"/> lines. When even the minimum needs more, the
    /// minimum: the text then wraps onto the extra lines rather than being cut.
    /// <param name="linesAt">Lines the text wraps to at a font size (the available width is the caller's).</param>
    public static double FontSize(double designSize, int maxLines, Func<double, int> linesAt)
    {
        ArgumentNullException.ThrowIfNull(linesAt);
        if (maxLines < 1 || designSize <= 0 || linesAt(designSize) <= maxLines) return designSize;
        var min = MinimumSize(designSize);
        for (var size = designSize - Step; size > min + 1e-9; size -= Step)
            if (linesAt(size) <= maxLines) return size;
        return min;
    }
}
