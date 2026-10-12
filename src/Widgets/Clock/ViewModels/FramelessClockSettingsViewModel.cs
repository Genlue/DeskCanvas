using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Styling;
using Clock.Locales;
using Clock.Models;
using Clock.Services;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace Clock.ViewModels;

public record ClockFontOption(string? FontFamily, string DisplayName, string Description);
public record ClockMaterialOption(SurfaceStyle? Style, string DisplayName, string Description);
public record ClockFontSourceOption(bool IsSystemFont, string DisplayName);

public class FramelessClockSettingsViewModel : ReactiveObject
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;
    private FramelessClockModel model;

    public IReadOnlyList<ClockMaterialOption> MaterialOptions { get; }
    public IReadOnlyList<ClockFontSourceOption> FontSourceOptions { get; }
    public IReadOnlyList<ClockFontOption> ArtisticFontOptions { get; }
    public IReadOnlyList<ClockFontOption> SystemFontOptions { get; }
    public IReadOnlyList<ClockFontOption> FontOptions { get; }
    public IReadOnlyList<string> FontFamilies => FontOptions.Select(o => o.DisplayName).ToList();

    private static readonly HashSet<string> BundledArtisticFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "HarmonyOS Sans Condensed",
        "Bebas Neue",
        "DM Serif Display",
        "Bungee Inline",
        "Black Ops One",
        "Titan One",
        "Righteous",
        "Orbitron"
    };

    public FramelessClockSettingsViewModel(
        IWidgetLayoutProvider widgetLayoutProvider,
        IAppSettingsProvider? appSettingsProvider = null)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.appSettingsProvider = appSettingsProvider;
        model = widgetLayoutProvider.Get().GetModel<FramelessClockModel>() ?? new FramelessClockModel();

        MaterialOptions = new List<ClockMaterialOption>
        {
            new(null, Locale.Clock_Material_FollowGlobal, "自动同步控制中心设置的全局主题材质"),
            new(SurfaceStyle.Acrylic, Locale.Clock_Material_Acrylic, "亚克力磨砂半透明背景"),
            new(SurfaceStyle.LiquidGlass, Locale.Clock_Material_LiquidGlass, "经典流体光泽折射质感"),
            new(SurfaceStyle.LiquidGlassV2, Locale.Clock_Material_LiquidGlassV2, "实时背景折射 · 高光色散字形玻璃"),
            new(SurfaceStyle.Solid, Locale.Clock_Material_Solid, "纯净不透明高对比文字卡片"),
            new(SurfaceStyle.Colorful, Locale.Clock_Material_Colorful, "绚丽灵动渐变色彩材质")
        };

        FontSourceOptions = new List<ClockFontSourceOption>
        {
            new(false, Locale.Clock_FontSource_Artistic),
            new(true, Locale.Clock_FontSource_System)
        };

        ArtisticFontOptions = new List<ClockFontOption>
        {
            new("HarmonyOS Sans Condensed", "🔥 iOS / 鸿蒙修长海报体", "修长窄体海报大字 · 纵向视觉冲击力极强"),
            new("Bebas Neue", "🔥 iOS 经典重型块体 (Bebas Neue)", "iOS 16 经典锁屏硬朗窄体 · 粗犷有力"),
            new("DM Serif Display", "🔥 iOS 高定时尚衬线 (Didone)", "高对比度优雅衬线 · 杂志封面高级感"),
            new("Bungee Inline", "🔥 iOS 复古双线轨体 (Rail)", "iOS 锁屏同款中空内嵌双线 · 摩登复古"),
            new("Black Ops One", "🔥 iOS 重工镂空切角 (Stencil)", "机甲军工切角镂空 · 硬核工业设计感"),
            new("Titan One", "🔥 iOS 圆润泡芙软糖体 (Bubble)", "饱满圆润立体大字 · 活泼柔和视觉"),
            new("Righteous", "🔥 iOS 摩登几何艺术体 (Deco)", "装饰艺术圆弧转角 · 现代时尚几何感"),
            new("Orbitron", "🔥 赛博机甲可变数码体 (Orbitron)", "未来科技方正折角 · 支持滑块无级调节粗细")
        };

        var sysList = new List<ClockFontOption>
        {
            new(null, "⚙ 系统默认字体 (Segoe UI)", "跟随系统默认无衬线字体"),
            new("Segoe UI Variable", "Segoe UI Variable (原生流体现代体)", "微软原生可变字重系统字体 · 推荐配合粗细滑块"),
            new("Bahnschrift", "Bahnschrift (德国工业 DIN 标准体)", "现代工业标准几何字体 · 支持可变字重"),
            new("Impact", "Impact (美式重型块体)", "Windows 经典重型窄体"),
            new("Arial Black", "Arial Black (超粗无衬线黑体)", "宽幅厚重实心黑体"),
            new("Georgia", "Georgia (古典衬线体)", "Windows 经典优雅衬线"),
            new("Century Gothic", "Century Gothic (包豪斯几何体)", "纯净几何圆融线条"),
            new("Cascadia Code", "Cascadia Code (极客等宽终端体)", "微软现代等宽编程字体 · 支持可变字重"),
            new("Consolas", "Consolas (经典等宽数字体)", "等宽规整数字排版")
        };

        var featuredSystemSet = new HashSet<string>(
            sysList.Where(x => x.FontFamily != null).Select(x => x.FontFamily!),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            var systemFonts = SystemFonts.GetFonts();
            foreach (var sf in systemFonts)
            {
                if (!featuredSystemSet.Contains(sf) && !BundledArtisticFamilies.Contains(sf))
                {
                    sysList.Add(new ClockFontOption(sf, sf, "Windows 本地已安装系统字体"));
                }
            }
        }
        catch
        {
            // Keep fallback featured system fonts when headless/test host cannot enumerate system fonts.
        }

        SystemFontOptions = sysList;

        var combined = new List<ClockFontOption>();
        combined.Add(SystemFontOptions[0]);
        combined.AddRange(ArtisticFontOptions);
        combined.AddRange(SystemFontOptions.Skip(1));
        FontOptions = combined;

        if (this.appSettingsProvider != null)
        {
            this.appSettingsProvider.DataChanged += (_, _, _) =>
            {
                this.RaisePropertyChanged(nameof(ShowLiquidGlassOpacity));
            };
        }
    }

    public ClockMaterialOption SelectedMaterialOption
    {
        get => MaterialOptions.FirstOrDefault(m => m.Style == model.SurfaceOverride) ?? MaterialOptions[0];
        set
        {
            if (value == null) return;
            if (model.SurfaceOverride == value.Style) return;
            UpdateModel(model with { SurfaceOverride = value.Style });
            this.RaisePropertyChanged(nameof(SelectedMaterialOption));
            this.RaisePropertyChanged(nameof(SurfaceOverride));
            this.RaisePropertyChanged(nameof(ShowLiquidGlassOpacity));
        }
    }

    public SurfaceStyle? SurfaceOverride
    {
        get => model.SurfaceOverride;
        set
        {
            if (model.SurfaceOverride == value) return;
            UpdateModel(model with { SurfaceOverride = value });
            this.RaisePropertyChanged(nameof(SurfaceOverride));
            this.RaisePropertyChanged(nameof(SelectedMaterialOption));
            this.RaisePropertyChanged(nameof(ShowLiquidGlassOpacity));
        }
    }

    public bool ShowLiquidGlassOpacity
    {
        get
        {
            if (model.SurfaceOverride.HasValue)
                return model.SurfaceOverride.Value == SurfaceStyle.LiquidGlassV2;

            var globalSurface = appSettingsProvider?.Get().Theme.EffectiveSurface;
            return globalSurface == SurfaceStyle.LiquidGlassV2;
        }
    }

    public double LiquidGlassOpacityPercent
    {
        get => Math.Round(Math.Clamp(model.LiquidGlassOpacity, 0.0, 100.0));
        set
        {
            var clamped = Math.Round(Math.Clamp(value, 0.0, 100.0));
            if (Math.Abs(clamped - model.LiquidGlassOpacity) < 0.01) return;
            UpdateModel(model with { LiquidGlassOpacity = clamped });
            this.RaisePropertyChanged(nameof(LiquidGlassOpacityPercent));
        }
    }

    public bool UseSystemFont
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(model.FontFamily))
            {
                if (BundledArtisticFamilies.Contains(model.FontFamily))
                    return false;
                return true;
            }
            return true;
        }
        set
        {
            if (value == UseSystemFont && model.UseSystemFont == value) return;

            string? nextFamily = model.FontFamily;
            if (!value)
            {
                // Switching to Artistic Font category: pick first bundled artistic font if not already one
                if (string.IsNullOrWhiteSpace(nextFamily) || !BundledArtisticFamilies.Contains(nextFamily))
                {
                    nextFamily = ArtisticFontOptions[0].FontFamily;
                }
            }
            else
            {
                // Switching to System Font category: reset to system default if currently an artistic font
                if (!string.IsNullOrWhiteSpace(nextFamily) && BundledArtisticFamilies.Contains(nextFamily))
                {
                    nextFamily = null;
                }
            }

            UpdateModel(model with { UseSystemFont = value, FontFamily = nextFamily });
            RaiseFontPropertiesChanged();
        }
    }

    public ClockFontSourceOption SelectedFontSourceOption
    {
        get => FontSourceOptions.FirstOrDefault(o => o.IsSystemFont == UseSystemFont) ?? FontSourceOptions[0];
        set
        {
            if (value != null)
            {
                UseSystemFont = value.IsSystemFont;
            }
        }
    }

    public bool ShowArtisticFontSelector => !UseSystemFont;
    public bool ShowSystemFontSelector => UseSystemFont;

    public ClockFontOption SelectedArtisticFont
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(model.FontFamily))
            {
                var found = ArtisticFontOptions.FirstOrDefault(f =>
                    string.Equals(f.FontFamily, model.FontFamily, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                    return found;
            }
            return ArtisticFontOptions[0];
        }
        set
        {
            if (value != null)
            {
                UpdateModel(model with { UseSystemFont = false, FontFamily = value.FontFamily });
                RaiseFontPropertiesChanged();
            }
        }
    }

    public ClockFontOption SelectedSystemFont
    {
        get
        {
            if (string.IsNullOrWhiteSpace(model.FontFamily) || BundledArtisticFamilies.Contains(model.FontFamily))
                return SystemFontOptions[0];

            return SystemFontOptions.FirstOrDefault(f =>
                       string.Equals(f.FontFamily, model.FontFamily, StringComparison.OrdinalIgnoreCase))
                   ?? new ClockFontOption(model.FontFamily, model.FontFamily, "自定义系统字体");
        }
        set
        {
            if (value != null)
            {
                UpdateModel(model with { UseSystemFont = true, FontFamily = value.FontFamily });
                RaiseFontPropertiesChanged();
            }
        }
    }

    private void RaiseFontPropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(UseSystemFont));
        this.RaisePropertyChanged(nameof(SelectedFontSourceOption));
        this.RaisePropertyChanged(nameof(ShowArtisticFontSelector));
        this.RaisePropertyChanged(nameof(ShowSystemFontSelector));
        this.RaisePropertyChanged(nameof(SelectedArtisticFont));
        this.RaisePropertyChanged(nameof(SelectedSystemFont));
        this.RaisePropertyChanged(nameof(SelectedFontOption));
        this.RaisePropertyChanged(nameof(SelectedFontFamily));
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

    public bool FixedWidthDigits
    {
        get => model.FixedWidthDigits;
        set
        {
            if (model.FixedWidthDigits == value) return;
            UpdateModel(model with { FixedWidthDigits = value });
            this.RaisePropertyChanged(nameof(FixedWidthDigits));
        }
    }

    /// <summary>
    /// 字体粗细 (100-900). Uses native typeface weights or OpenType 'wght' variation axis without
    /// forced synthetic boolean stroke unions.
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
                bool isArtistic = !string.IsNullOrWhiteSpace(value.FontFamily) && BundledArtisticFamilies.Contains(value.FontFamily);
                UpdateModel(model with { UseSystemFont = !isArtistic, FontFamily = value.FontFamily });
                RaiseFontPropertiesChanged();
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
