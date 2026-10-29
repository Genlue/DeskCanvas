using System.Globalization;
using System.Resources;

namespace DeskCanvas.WinUI.WidgetEditors.Localization;

/// <summary>
/// 编辑窗口外壳自己产生的文案（未迁移页、插件适配提示、保存失败）。键名与 Locale 一样走资源查找。
/// 不写死中英文（计划 §1.6）。
/// <para>
/// 为什么不复用 <c>Settings/Localization/SettingsShellStrings</c>：那是设置窗口执行者的资源。
/// 编辑窗口的 37 个组件页由本执行者独立推进，单独一份资源可以避免并行改同一个文件。
/// 资源清单 = 根命名空间 + 目录 + 文件名；改文件名/移动目录时必须同步改 <see cref="ResourceName"/>。
/// </para>
/// </summary>
internal static class WidgetEditorStrings
{
    private const string ResourceName = "DeskCanvas.WinUI.WidgetEditors.Localization.WidgetEditorStrings";

    private static readonly ResourceManager Manager = new(ResourceName, typeof(WidgetEditorStrings).Assembly);

    public static string NotMigratedTitle => Get("NotMigratedTitle");

    public static string NotMigratedBody => Get("NotMigratedBody");

    public static string NeedsAdapterTitle => Get("NeedsAdapterTitle");

    public static string NeedsAdapterBody => Get("NeedsAdapterBody");

    public static string PageUnavailableTitle => Get("PageUnavailableTitle");

    public static string OriginalPageLabel => Get("OriginalPageLabel");

    public static string SaveFailedTitle => Get("SaveFailedTitle");

    private static string Get(string key)
        => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
