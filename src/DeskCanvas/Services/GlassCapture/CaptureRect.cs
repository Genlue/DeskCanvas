namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// An integer rectangle in <b>virtual-desktop physical pixels</b> (left/top inclusive,
/// right/bottom exclusive) — the one coordinate space every glass capture talks in.
/// <para>
/// Deliberately not <c>Avalonia.PixelRect</c>: the capture layer is called from worker threads and
/// from the interop paths, and keeping it on a plain struct means the discovery/selection logic can
/// be unit-tested without an Avalonia application. The layout follows Win32's <c>RECT</c> so that
/// <see cref="ToNative"/> and the monitor/local conversions are direct.
/// </para>
/// </summary>
public readonly record struct CaptureRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static CaptureRect FromSize(int left, int top, int width, int height) =>
        new(left, top, left + width, top + height);

    /// <summary>Area in pixels; 0 for an inverted rectangle (which is how "no overlap" is spelled).</summary>
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public CaptureRect Intersect(CaptureRect other)
    {
        var left = System.Math.Max(Left, other.Left);
        var top = System.Math.Max(Top, other.Top);
        var right = System.Math.Min(Right, other.Right);
        var bottom = System.Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? default : new CaptureRect(left, top, right, bottom);
    }

    public CaptureRect Union(CaptureRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new CaptureRect(
            System.Math.Min(Left, other.Left),
            System.Math.Min(Top, other.Top),
            System.Math.Max(Right, other.Right),
            System.Math.Max(Bottom, other.Bottom));
    }

    public CaptureRect Inflate(int dx, int dy) =>
        IsEmpty ? this : new CaptureRect(Left - dx, Top - dy, Right + dx, Bottom + dy);

    /// <summary>True when <paramref name="other"/> lies entirely inside this rectangle.</summary>
    public bool Contains(CaptureRect other) =>
        !other.IsEmpty && other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    public CaptureRect Offset(int dx, int dy) =>
        IsEmpty ? this : new CaptureRect(Left + dx, Top + dy, Right + dx, Bottom + dy);

    /// <summary>Fraction of <paramref name="interest"/> this rectangle covers (0…1).</summary>
    public double CoverageOf(CaptureRect interest)
    {
        var area = interest.Area;
        return area > 0 ? (double)Intersect(interest).Area / area : 0.0;
    }

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}
