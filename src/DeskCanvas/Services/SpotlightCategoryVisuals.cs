using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.ViewModels;

namespace DeskCanvas.Services;

/// <summary>
/// The one mapping from a <see cref="LauncherCategoryKeys"/> storage key to what a category looks
/// like: the filter it locks, its localized label and its artwork.
/// <para>
/// It exists because three surfaces now name the same categories — the filter chips, the 分类圆钮
/// beside the field and the 设置 page's two category lists. Before this, each of them carried its
/// own <c>switch</c> over the keys, and the copies had already drifted: the circles drew 文件 with
/// the <b>folder</b> mark ("a circle is a destination, not an object") while the chips drew the
/// document one, so the same category had two faces depending on where it was shown. One table
/// means a category can only ever have one label and one mark.
/// </para>
/// <para>
/// Storage keys stay strings (they are persisted), never localized labels: a translated chip must
/// not become a dictionary key, or switching the UI language would drop every saved choice.
/// </para>
/// </summary>
public static class SpotlightCategoryVisuals
{
    /// <summary>
    /// The category a storage key names, or <c>null</c> when it is not a category. 全部 resolves
    /// like any other (it is a real filter); callers that need "somewhere to go instead of
    /// filtering" check for it themselves.
    /// </summary>
    public static SpotlightCategoryKind? KindForKey(string? key) => LauncherCategoryKeys.Match(key) switch
    {
        LauncherCategoryKeys.All => SpotlightCategoryKind.All,
        LauncherCategoryKeys.Apps => SpotlightCategoryKind.Apps,
        LauncherCategoryKeys.Images => SpotlightCategoryKind.Images,
        LauncherCategoryKeys.Files => SpotlightCategoryKind.Files,
        LauncherCategoryKeys.Folders => SpotlightCategoryKind.Folders,
        LauncherCategoryKeys.Widgets => SpotlightCategoryKind.Widgets,
        LauncherCategoryKeys.Clipboard => SpotlightCategoryKind.Clipboard,
        LauncherCategoryKeys.Web => SpotlightCategoryKind.Web,
        _ => null,
    };

    /// <summary>The localized chip label of a category.</summary>
    public static string LabelFor(SpotlightCategoryKind kind) => kind switch
    {
        SpotlightCategoryKind.Apps => Locale.Spotlight_Category_Apps,
        SpotlightCategoryKind.Images => Locale.Spotlight_Category_Images,
        SpotlightCategoryKind.Files => Locale.Spotlight_Category_Files,
        SpotlightCategoryKind.Folders => Locale.Spotlight_Category_Folders,
        SpotlightCategoryKind.Widgets => Locale.Spotlight_Category_Widgets,
        SpotlightCategoryKind.Clipboard => Locale.Spotlight_Category_Clipboard,
        SpotlightCategoryKind.Web => Locale.Spotlight_Category_Web,
        _ => Locale.Spotlight_Category_All,
    };

    /// <summary>The localized label of a storage key (never blank: an unknown key reads as 全部).</summary>
    public static string LabelForKey(string? key) => LabelFor(KindForKey(key) ?? SpotlightCategoryKind.All);

    /// <summary>
    /// The artwork of a category. 文件 wears the <b>document</b> mark — the same one the search
    /// field shows while a 文件 filter is locked — and 文件夹 the folder mark: the two categories are
    /// different destinations, so they must not share a face.
    /// </summary>
    public static SpotlightGlyphKind GlyphFor(SpotlightCategoryKind kind) => kind switch
    {
        SpotlightCategoryKind.Apps => SpotlightGlyphKind.Apps,
        SpotlightCategoryKind.Images => SpotlightGlyphKind.Image,
        SpotlightCategoryKind.Files => SpotlightGlyphKind.Files,
        SpotlightCategoryKind.Folders => SpotlightGlyphKind.Folder,
        SpotlightCategoryKind.Widgets => SpotlightGlyphKind.Widget,
        SpotlightCategoryKind.Clipboard => SpotlightGlyphKind.Clipboard,
        SpotlightCategoryKind.Web => SpotlightGlyphKind.Web,
        _ => SpotlightGlyphKind.Search,
    };

    /// <summary>The artwork of a storage key.</summary>
    public static SpotlightGlyphKind GlyphForKey(string? key)
        => GlyphFor(KindForKey(key) ?? SpotlightCategoryKind.All);
}
