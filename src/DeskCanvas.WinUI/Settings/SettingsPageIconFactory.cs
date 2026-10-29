using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace DeskCanvas.WinUI.Settings;

/// <summary>
/// 把目录里的路径字符串转成 WinUI 的 <see cref="PathIcon"/>。
/// <para>
/// WinUI 没有 <c>Geometry.Parse</c>：XAML 的类型转换器是唯一不需要控件树就能把路径字符串变成
/// <see cref="Geometry"/> 的入口，<see cref="XamlBindingHelper.ConvertValue"/> 用的就是同一套转换。
/// 解析失败（第三方/脏数据路径）时返回 null，导航项退化成纯文字，而不是让整个设置窗口炸掉。
/// </para>
/// </summary>
internal static class SettingsPageIconFactory
{
    public static IconElement? Create(string? pathData)
    {
        if (string.IsNullOrWhiteSpace(pathData)) return null;

        try
        {
            if (XamlBindingHelper.ConvertValue(typeof(Geometry), pathData) is Geometry geometry)
            {
                return new PathIcon { Data = geometry };
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] 导航图标路径解析失败，已退化为纯文字：{ex.Message}");
        }

        return null;
    }
}
