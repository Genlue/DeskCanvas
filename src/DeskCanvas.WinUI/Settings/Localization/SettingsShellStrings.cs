using System.Globalization;
using System.Resources;

namespace DeskCanvas.WinUI.Settings.Localization;

/// <summary>
/// 设置外壳自己产生的少量文案（占位页、窗口标题）。
/// <para>
/// 为什么不直接用宿主 <c>DeskCanvas.Locales.Locale</c>：占位页是迁移期脚手架，宿主资源里没有
/// 「该页面尚未迁移到 WinUI」这类键，而本执行者不能改 <c>src/DeskCanvas/Locales/*.resx</c>。
/// 因此把外壳文案放在 WinUI 工程自己的资源里，键名与 Locale 一样走资源查找，不写死文案；
/// 迁移收尾删除占位页时可一并删掉本资源。
/// </para>
/// <para>
/// 资源清单名 = 根命名空间 + 目录 + 文件名。改文件名/移动目录时要同步改 <see cref="ResourceName"/>，
/// 否则运行时全部回退成键名（占位页会明显显示成键，便于发现）。
/// </para>
/// </summary>
internal static class SettingsShellStrings
{
    private const string ResourceName = "DeskCanvas.WinUI.Settings.Localization.SettingsShellStrings";

    private static readonly ResourceManager Manager = new(ResourceName, typeof(SettingsShellStrings).Assembly);

    public static string SettingsWindowTitle => Get("SettingsWindowTitle");

    public static string PlaceholderTitle => Get("PlaceholderTitle");

    public static string PlaceholderBody => Get("PlaceholderBody");

    public static string PlaceholderPageKeyLabel => Get("PlaceholderPageKeyLabel");

    public static string PlaceholderPacksLabel => Get("PlaceholderPacksLabel");

    private static string Get(string key)
        => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
