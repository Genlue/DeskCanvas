using System.Globalization;
using System.Resources;

namespace DeskCanvas.WinUI.Settings.Pages;

/// <summary>
/// General 页自己的少量错误文案（保存失败、自启动写入失败）。
/// <para>
/// 这些键共享 <c>src/DeskCanvas/Locales/Locale.resx</c> 里没有，而迁移执行者不允许改那份
/// 共享资源，所以按报告 §3 第二步的约定放在本页目录下；WinUI 工程默认把 .resx 作为
/// EmbeddedResource 嵌入，清单名 = 根命名空间 + 目录 + 文件名（与 SettingsShellStrings 相同）。
/// </para>
/// </summary>
internal static class GeneralStrings
{
    // 实用清单名由 MSBuild 按同目录同名 .cs 的命名空间生成（DeskCanvas.WinUI.Settings.Pages），
    // 不包含页面子目录；用错名字时 GetString 会抛 MissingManifestResourceException。

    private const string ResourceName = "DeskCanvas.WinUI.Settings.Pages.GeneralStrings";

    private static readonly ResourceManager Manager = new(ResourceName, typeof(GeneralStrings).Assembly);

    public static string SaveFailedTitle => Get("SaveFailedTitle");

    public static string SaveFailedMessage => Get("SaveFailedMessage");

    public static string StartupChangeFailedTitle => Get("StartupChangeFailedTitle");

    public static string StartupChangeFailedMessage => Get("StartupChangeFailedMessage");

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
