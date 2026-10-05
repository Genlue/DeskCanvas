using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Styling;
using Clock.Models;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;

namespace Clock.ViewModels;

public record ClockFontOption(string? FontFamily, string DisplayName, string Description);

public class FramelessClockSettingsViewModel : ReactiveObject
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private FramelessClockModel model;

    public IReadOnlyList<ClockFontOption> FontOptions { get; }
    public IReadOnlyList<string> FontFamilies => FontOptions.Select(o => o.DisplayName).ToList();

    public FramelessClockSettingsViewModel(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = widgetLayoutProvider.Get().GetModel<FramelessClockModel>() ?? new FramelessClockModel();

        var list = new List<ClockFontOption>
        {
            new(null, "⚙ 默认字体", "跟随系统外观设置"),
            new("HarmonyOS Sans Condensed", "🔥 华为锁屏超窄体", "专为纵向拉伸设计 · 高度满格震撼大字"),
            new("Impact", "🔥 iOS 16 经典厚重块体", "美式重型大字 · 极具冲击力与力量感"),
            new("Bahnschrift", "🔥 德国工业精工 DIN", "现代工业标准窄体 · 比例严谨规整"),
            new("Arial Black", "🔥 超粗硬核无衬线", "超宽实心黑体 · 块状视觉极度醒目"),
            new("Georgia", "🔥 iOS 高定复古衬线", "粗细笔触优雅对比 · 潮流时尚大字"),
            new("Century Gothic", "🔥 包豪斯极简几何", "纯粹几何圆融线条 · 清新平衡美感"),
            new("Cascadia Code", "🔥 赛博极客终端等宽", "代码终端科技质感 · 工整硬派风格"),
            new("Ink Free", "🔥 自由随性自然手写", "灵动生活手写笔触 · 亲和随性自然"),
            new("Palatino Linotype", "🔥 文艺古典罗马体", "人文主义典雅字形 · 历史厚重底蕴"),
            new("Segoe UI Variable", "🔥 原生流体现代体", "微软最新原生流体设计字族")
        };

        var curatedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HarmonyOS Sans Condensed", "Impact", "Bahnschrift", "Arial Black",
            "Georgia", "Century Gothic", "Cascadia Code", "Ink Free",
            "Palatino Linotype", "Segoe UI Variable"
        };

        try
        {
            var systemFonts = SystemFonts.GetFonts();
            foreach (var sf in systemFonts)
            {
                if (!curatedSet.Contains(sf))
                {
                    list.Add(new ClockFontOption(sf, sf, "Windows 本地已安装字体"));
                }
            }
        }
        catch
        {
            list.Add(new ClockFontOption("Segoe UI", "Segoe UI", "Windows 系统字体"));
            list.Add(new ClockFontOption("Consolas", "Consolas", "等宽字体"));
        }

        FontOptions = list;
    }

    public bool ShowSeconds
    {
        get => model.ShowSeconds;
        set => UpdateModel(model with { ShowSeconds = value });
    }

    public bool Use24Hours
    {
        get => model.Use24Hours;
        set => UpdateModel(model with { Use24Hours = value });
    }

    public bool StretchFill
    {
        get => model.StretchFill;
        set => UpdateModel(model with { StretchFill = value });
    }

    /// <summary>
    /// 字体粗细 as a continuous 100-900 position (the slider's range), not a discrete OpenType
    /// weight. The widget resolves the position against the selected family: it uses the real faces
    /// the family provides and fills whatever the family cannot reach with a synthetic stroke, so
    /// the travel always reads 最细 → 最粗 — including for the curated single-face fonts (华为超窄体
    /// ships one Black face, Impact one Regular), where nine discrete weight steps all resolved to
    /// the same face and the old picker looked completely dead.
    /// </summary>
    public double FontWeightSlider
    {
        get => model.FontWeight;
        set
        {
            var position = (int)Math.Round(Math.Clamp(value, 100, 900));
            if (position == model.FontWeight) return;
            UpdateModel(model with { FontWeight = position });
            this.RaisePropertyChanged(nameof(FontWeightSlider));
        }
    }

    public ClockFontOption SelectedFontOption
    {
        get
        {
            if (string.IsNullOrWhiteSpace(model.FontFamily))
                return FontOptions[0];

            return FontOptions.FirstOrDefault(f => string.Equals(f.FontFamily, model.FontFamily, StringComparison.OrdinalIgnoreCase))
                   ?? new ClockFontOption(model.FontFamily, model.FontFamily, "自定义字体");
        }
        set
        {
            if (value != null)
            {
                UpdateModel(model with { FontFamily = value.FontFamily });
                this.RaisePropertyChanged(nameof(SelectedFontOption));
                this.RaisePropertyChanged(nameof(SelectedFontFamily));
            }
        }
    }

    public string SelectedFontFamily
    {
        get => SelectedFontOption.DisplayName;
        set
        {
            var opt = FontOptions.FirstOrDefault(f => f.DisplayName == value || f.FontFamily == value);
            if (opt != null)
                SelectedFontOption = opt;
        }
    }

    public bool EnableOverlay
    {
        get => model.EnableOverlay;
        set
        {
            UpdateModel(model with { EnableOverlay = value });
            this.RaisePropertyChanged(nameof(EnableOverlay));
            this.RaisePropertyChanged(nameof(ShowOverlayOptions));
        }
    }

    public bool ShowOverlayOptions => EnableOverlay;

    public bool FollowAccentColor
    {
        get => model.FollowAccentColor;
        set
        {
            UpdateModel(model with { FollowAccentColor = value });
            this.RaisePropertyChanged(nameof(FollowAccentColor));
            this.RaisePropertyChanged(nameof(ShowCustomColor));
        }
    }

    public bool ShowCustomColor => !FollowAccentColor;

    public string OverlayColor
    {
        get => model.OverlayColor;
        set
        {
            UpdateModel(model with { OverlayColor = value });
            this.RaisePropertyChanged(nameof(OverlayColor));
            this.RaisePropertyChanged(nameof(OverlayColorPickerValue));
        }
    }

    public Color OverlayColorPickerValue
    {
        get => ParseColor(model.OverlayColor) ?? AccentColor();
        set
        {
            var hex = ToHex(value);
            if (hex == model.OverlayColor) return;
            UpdateModel(model with { OverlayColor = hex });
            this.RaisePropertyChanged(nameof(OverlayColor));
            this.RaisePropertyChanged(nameof(OverlayColorPickerValue));
        }
    }

    private static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return Color.Parse(hex); }
        catch (FormatException) { return null; }
    }

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color AccentColor(bool dark = false)
    {
        if (Avalonia.Application.Current is { } app &&
            ((Avalonia.Controls.IResourceHost)app).TryGetResource(
                "SystemAccentColor",
                dark ? ThemeVariant.Dark : ThemeVariant.Light,
                out var value) &&
            value is Color color)
            return color;

        return Color.Parse("#0078D7");
    }

    public double OverlayOpacityPercent
    {
        get => Math.Round(model.OverlayOpacity * 100);
        set
        {
            var clamped = Math.Clamp(value / 100.0, 0.0, 1.0);
            UpdateModel(model with { OverlayOpacity = clamped });
            this.RaisePropertyChanged(nameof(OverlayOpacityPercent));
        }
    }

    public bool ShowTimeZones => !UseLocalTimeZone;

    public bool UseLocalTimeZone
    {
        get => model.TimeZoneId == null;
        set
        {
            UpdateModel(model with { TimeZoneId = value ? null : TimeZoneInfo.Local.Id });
            this.RaisePropertyChanged(nameof(UseLocalTimeZone));
            this.RaisePropertyChanged(nameof(ShowTimeZones));
        }
    }

    public TimeZoneInfo TimeZone
    {
        get => model.TimeZoneId != null
            ? TimeZoneInfo.FindSystemTimeZoneById(model.TimeZoneId)
            : TimeZoneInfo.Local;
        set => UpdateModel(model with { TimeZoneId = value.Id });
    }

    public TimeZoneInfo[] TimeZones => TimeZoneInfo.GetSystemTimeZones().Append(TimeZone).Distinct().ToArray();

    private void UpdateModel(FramelessClockModel newModel)
    {
        model = newModel;
        var widgetSettings = widgetLayoutProvider.Get();
        var newSettings = widgetSettings with { Settings = JsonSerializer.SerializeToElement(model) };
        widgetLayoutProvider.Save(newSettings);
    }
}
