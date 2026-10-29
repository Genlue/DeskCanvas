namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// "May this captured frame be reused for that request?" — the rule that keeps a region-limited
/// capture honest.
/// <para>
/// A live frame holds the rectangle it was cropped from (a patch of the virtual desktop), not the
/// whole screen. Handing that frame to a surface whose rectangle lies outside it does not fail
/// loudly: the material addresses texels that are not in the texture, the sampler clamps to the
/// patch's edge, and the glass refracts a piece of desktop that is not behind it. That is the
/// reported 聚焦搜索 "按 Alt 出来的四个圆钮采样的是壁纸" — the circles appear to the right of the
/// card, outside the patch the card's own prepare had captured, so they sampled the patch's edge
/// column instead of the screen under them.
/// </para>
/// <para>
/// Pure arithmetic on purpose (no SkiaSharp, no window handles): the check programs link this file
/// and pin the rule without a desktop.
/// </para>
/// </summary>
public static class SnapshotCoverage
{
    /// <summary>
    /// True when a frame cropped at <paramref name="regionX"/>/<paramref name="regionY"/> with the
    /// given pixel size contains every part of <paramref name="interest"/>. An empty interest is
    /// always satisfied; a frame with no pixels satisfies nothing.
    /// </summary>
    public static bool Covers(int regionX, int regionY, int width, int height, CaptureRect interest)
    {
        if (interest.IsEmpty) return true;
        if (width <= 0 || height <= 0) return false;
        return CaptureRect.FromSize(regionX, regionY, width, height).Contains(interest);
    }
}
