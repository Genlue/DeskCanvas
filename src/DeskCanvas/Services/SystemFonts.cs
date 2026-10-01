using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace DeskCanvas.Services;

/// <summary>
/// Enumerates the font families installed on the Windows system via GDI
/// (<c>EnumFontFamiliesEx</c>), plus the Inter font bundled with the app.
/// <para>
/// Weight/style variants that Windows registers as separate families
/// (e.g. "Arial Bold", "HarmonyOS Sans SC Black") are merged into their base
/// family so the picker shows one entry per font. Vertical ("@…") fonts are skipped.
/// </para>
/// </summary>
public static class SystemFonts
{
    /// <summary>
    /// Font weight/style suffixes that only vary the same base family.
    /// Longer suffixes first so "UltraBold" wins over "Bold".
    /// </summary>
    private static readonly string[] StyleSuffixes =
    [
        "ExtraBold", "ExtraLight", "UltraBold", "UltraLight", "SemiBold", "Semibold",
        "DemiBold", "Bold", "Italic", "Oblique", "Light", "Thin", "Regular",
        "Medium", "Black", "Heavy", "Fat", "Puffy"
    ];

    private static IReadOnlyList<string>? cache;

    /// <summary>
    /// Get the installed font family names, sorted (case-insensitive).
    /// </summary>
    public static IEnumerable<string> GetFonts() => cache ??= LoadFonts();

    private static List<string> LoadFonts()
    {
        var families = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var dc = GetDC(IntPtr.Zero);
        if (dc != IntPtr.Zero)
        {
            try
            {
                var logFont = new LOGFONT { lfCharSet = 1 }; // DEFAULT_CHARSET
                EnumFontFamiliesEx(dc, ref logFont, (ref LOGFONT font, IntPtr _, uint _, IntPtr _) =>
                {
                    var name = (font.lfFaceName ?? "").Trim();
                    // Vertical (vertical writing) variants are not useful for desktop widgets.
                    if (!name.StartsWith('@') && name.Length > 0)
                        families.Add(name);
                    return 1;
                }, IntPtr.Zero);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        }

        // Merge weight/style variants into their base family when the base exists.
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in families)
        {
            var baseName = StripStyleSuffix(name);
            if (baseName == name || !families.Contains(baseName))
                result.Add(name);
        }

        // Inter ships with the app (Avalonia.Fonts.Inter) and is not a system font.
        result.Add("Inter");
        return result.ToList();
    }

    /// <summary>
    /// Remove one trailing weight/style suffix, e.g. "Arial Bold" → "Arial",
    /// "HarmonyOS Sans SC Black" → "HarmonyOS Sans SC".
    /// </summary>
    private static string StripStyleSuffix(string name)
    {
        foreach (var suffix in StyleSuffixes)
        {
            if (name.Length > suffix.Length + 1 &&
                name.EndsWith(" " + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^(suffix.Length + 1)].TrimEnd();
            }
        }

        return name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    private delegate int FontEnumProc(ref LOGFONT logFont, IntPtr textMetric, uint fontType, IntPtr lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesEx(IntPtr hdc, ref LOGFONT logFont, FontEnumProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
