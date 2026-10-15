using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace DeskCanvas.Services;

public static class ThemeScopeResolver
{
    /// <summary>
    /// 按照 控件自身与视觉树 -> 所在顶层窗口 (Window) -> Application.Current 逐级查找资源。
    /// 优先读取窗口作用域的 Colorful 或 Normal 隔离资源，避免直接访问 Application.Current 导致失焦切色错乱。
    /// </summary>
    public static bool TryFindResource(Visual? visual, string key, ThemeVariant? variant, out object? value)
    {
        value = null;
        if (string.IsNullOrEmpty(key)) return false;

        var effectiveVariant = variant
            ?? (visual as StyledElement)?.ActualThemeVariant
            ?? (visual != null ? TopLevel.GetTopLevel(visual)?.ActualThemeVariant : null)
            ?? Application.Current?.ActualThemeVariant
            ?? ThemeVariant.Default;

        // 1. 尝试从控件自身及其逻辑父级/窗口向上查找
        if (visual is IResourceHost host)
        {
            if (host.TryFindResource(key, effectiveVariant, out value))
                return true;

            if (host.TryFindResource(key, null, out value))
                return true;
        }

        // 2. 若控件未建立逻辑父级连接，但已挂载视觉树，尝试从 TopLevel (Window) 查找
        var topLevel = visual != null ? TopLevel.GetTopLevel(visual) : null;
        if (topLevel != null && !ReferenceEquals(topLevel, visual))
        {
            if (topLevel.TryFindResource(key, effectiveVariant, out value))
                return true;

            if (topLevel.TryFindResource(key, null, out value))
                return true;
        }

        // 3. 回退至 Application.Current
        if (Application.Current != null)
        {
            if (Application.Current.TryFindResource(key, effectiveVariant, out value))
                return true;

            if (Application.Current.TryFindResource(key, null, out value))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 泛型资源查找，支持 Color 与 Brush 的智能兼容互转。
    /// </summary>
    public static bool TryFindResource<T>(Visual? visual, string key, ThemeVariant? variant, out T? value)
    {
        if (TryFindResource(visual, key, variant, out var raw))
        {
            if (raw is T typed)
            {
                value = typed;
                return true;
            }

            if (typeof(T) == typeof(IBrush) && raw is Color c)
            {
                value = (T)(object)new SolidColorBrush(c);
                return true;
            }

            if (typeof(T) == typeof(Color) && raw is ISolidColorBrush scb)
            {
                value = (T)(object)scb.Color;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 解析画刷资源，若资源为 Color 则自动包装为 SolidColorBrush。
    /// </summary>
    public static IBrush ResolveBrush(Visual? visual, string key, ThemeVariant? variant, IBrush fallback)
    {
        if (TryFindResource(visual, key, variant, out var value))
        {
            if (value is IBrush brush)
                return brush;
            if (value is Color color)
                return new SolidColorBrush(color);
        }
        return fallback;
    }

    /// <summary>
    /// 解析画刷资源（带可选回退），若找不到则返回 null。
    /// </summary>
    public static IBrush? ResolveBrush(Visual? visual, string key, ThemeVariant? variant = null)
    {
        if (TryFindResource(visual, key, variant, out var value))
        {
            if (value is IBrush brush)
                return brush;
            if (value is Color color)
                return new SolidColorBrush(color);
        }
        return null;
    }

    /// <summary>
    /// 解析颜色资源，若资源为 ISolidColorBrush 则自动提取 Color。
    /// </summary>
    public static Color ResolveColor(Visual? visual, string key, ThemeVariant? variant, Color fallback)
    {
        if (TryFindResource(visual, key, variant, out var value))
        {
            if (value is Color color)
                return color;
            if (value is ISolidColorBrush solid)
                return solid.Color;
        }
        return fallback;
    }

    /// <summary>
    /// 解析颜色资源（带可选回退），若找不到则返回 null。
    /// </summary>
    public static Color? ResolveColor(Visual? visual, string key, ThemeVariant? variant = null)
    {
        if (TryFindResource(visual, key, variant, out var value))
        {
            if (value is Color color)
                return color;
            if (value is ISolidColorBrush solid)
                return solid.Color;
        }
        return null;
    }
}
