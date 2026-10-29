using System.Globalization;
using System.Resources;

namespace DeskCanvas.WinUI.Settings.Pages.Appearance;

/// <summary>
/// 本页自己的少量文案（资源名 = 根命名空间 + 目录 + 文件名）。
/// <para>
/// 为什么不用共享 <c>DeskCanvas.Locales.Locale</c>：外观页需要的「当前材质」「壁纸对齐尚未迁移」
/// 「红绿灯标题栏暂由原生标题栏呈现」这几条在外壳资源里没有，而页面执行者不能改共享
/// <c>src/DeskCanvas/Locales/*.resx</c>。按迁移约定把缺键放本页目录，WinUI 工程会自动嵌入。
/// </para>
/// </summary>
internal static class AppearancePageStrings
{
    private const string ResourceName = "DeskCanvas.WinUI.Settings.Pages.Appearance.AppearancePageStrings";

    private static readonly ResourceManager Manager = new(ResourceName, typeof(AppearancePageStrings).Assembly);

    /// <summary>「当前材质：{0}」——材质样片下方的说明。</summary>
    public static string CurrentMaterialFormat => Get("CurrentMaterialFormat");

    /// <summary>「壁纸对齐尚不可用」标题。</summary>
    public static string WallpaperAlignUnavailableTitle => Get("WallpaperAlignUnavailableTitle");

    /// <summary>壁纸对齐窗口尚未迁移的说明。</summary>
    public static string WallpaperAlignUnavailable => Get("WallpaperAlignUnavailable");

    /// <summary>红绿灯标题栏在 WinUI 设置窗口里暂未呈现的说明。</summary>
    public static string TitleBarStyleWinUiNote => Get("TitleBarStyleWinUiNote");

    /// <summary>页面直接写盘失败时的错误文案（不经过 VM 草稿会话，没有可重试的草稿）。</summary>
    public static string SettingsSaveFailed => Get("SettingsSaveFailed");

    private static string Get(string key)
        => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
