using System.Globalization;
using System.Resources;

namespace DeskCanvas.WinUI.Settings.Pages;

/// <summary>
/// 关于页自己的文案。旧 <c>About.axaml.cs</c> 用 <c>IsZh</c> 三元表达式硬编码中英文（例如快捷操作技巧），
/// 共享 <c>Locale.resx</c> 里没有这些键且不允许改，因此按迁移约定放在本页目录下。
/// </summary>
internal static class AboutStrings
{
    // 实用清单名由 MSBuild 按同目录同名 .cs 的命名空间生成（DeskCanvas.WinUI.Settings.Pages），
    // 不包含页面子目录；用错名字时 GetString 会抛 MissingManifestResourceException。

    private const string ResourceName = "DeskCanvas.WinUI.Settings.Pages.AboutStrings";

    private static readonly ResourceManager Manager = new(ResourceName, typeof(AboutStrings).Assembly);

    public static string TipsHeader => Get("TipsHeader");

    public static string QuickMoveTitle => Get("QuickMoveTitle");

    public static string QuickMoveSubtitle => Get("QuickMoveSubtitle");

    public static string QuickMoveShortcut => Get("QuickMoveShortcut");

    public static string SwitchStackTitle => Get("SwitchStackTitle");

    public static string SwitchStackSubtitle => Get("SwitchStackSubtitle");

    public static string SwitchStackShortcut => Get("SwitchStackShortcut");

    public static string OpenLinkFailedTitle => Get("OpenLinkFailedTitle");

    public static string OpenLinkFailedMessage => Get("OpenLinkFailedMessage");

    private static string Get(string key)
    {
        try
        {
            return Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            // 资源名写错/资源未随包发布时回退到键名，页面仍可用（与 SettingsShellStrings 的契约一致）。

            return key;
        }
    }
}
