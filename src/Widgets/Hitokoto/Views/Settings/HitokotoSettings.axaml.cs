using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeskCanvas.Core.Interfaces;
using Hitokoto.Models;

namespace Hitokoto.Views.Settings;

public partial class HitokotoSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private HitokotoModel model;
    private readonly Dictionary<string, CheckBox> categoryChecks = new(StringComparer.OrdinalIgnoreCase);
    private bool initializing = true;

    /// <summary>
    /// Installed font names, enumerated once per process. The settings page used to re-walk
    /// <see cref="FontManager.SystemFonts"/> on every open, which makes Avalonia's font manager
    /// materialise every installed family just to list names.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<string>> CachedSystemFontNames = new(BuildSystemFontNames);

    private static IReadOnlyList<string> BuildSystemFontNames()
    {
        try
        {
            return FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();
        }
        catch
        {
            // Headless environments may not expose a font manager; the default entry still works.
            return [];
        }
    }

    private static bool IsChinese =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    private static (int Minutes, string Label)[] IntervalOptions => IsChinese
        ? [
            (0, "仅手动切换 (双击卡片/按钮)"),
            (5, "每 5 分钟"),
            (15, "每 15 分钟"),
            (30, "每 30 分钟 (推荐)"),
            (60, "每 1 小时"),
            (120, "每 2 小时"),
            (1440, "每日一句 (24 小时)")
        ]
        : [
            (0, "Manual only (double-click / button)"),
            (5, "Every 5 minutes"),
            (15, "Every 15 minutes"),
            (30, "Every 30 minutes (Recommended)"),
            (60, "Every 1 hour"),
            (120, "Every 2 hours"),
            (1440, "Daily quote (24 hours)")
        ];

    private static (HitokotoStyleMode Mode, string Label)[] StyleOptions => IsChinese
        ? [
            (HitokotoStyleMode.ModernQuote, "现代引语 (左对齐 + 引号水印)"),
            (HitokotoStyleMode.PoeticCenter, "古典诗词 (居中衬线 + 留白)"),
            (HitokotoStyleMode.MinimalCard, "极简纯粹 (无装饰水印)")
        ]
        : [
            (HitokotoStyleMode.ModernQuote, "Modern Quote (Left-aligned + Watermark)"),
            (HitokotoStyleMode.PoeticCenter, "Poetic Center (Centered Serif)"),
            (HitokotoStyleMode.MinimalCard, "Minimal Card (No Watermark)")
        ];

    public HitokotoSettings(IWidgetLayoutProvider widgetLayoutProvider)
        : this(widgetLayoutProvider, widgetLayoutProvider.Get().GetModel<HitokotoModel>() ?? new HitokotoModel())
    {
    }

    public HitokotoSettings(IWidgetLayoutProvider widgetLayoutProvider, HitokotoModel model)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.model = model.Normalize();

        InitializeComponent();
        ApplyLocalization();
        PopulateControls();
        initializing = false;
    }

    private void ApplyLocalization()
    {
        if (IsChinese) return;

        AppearanceHeader.Text = "Appearance & Typography";
        StyleModeSetting.Title = "Layout Style";
        StyleModeSetting.Subtitle = "Switch between Modern Quote, Poetic Center, or Minimal layout";
        FramelessSetting.Title = "Frameless Desktop Watermark Mode";
        FramelessSetting.Subtitle = "Hide card background and border so text floats directly over wallpaper";
        AutoHideActionsSetting.Title = "Auto-Hide Action Buttons";
        AutoHideActionsSetting.Subtitle = "Show like, bookmark, copy, and expand buttons only on mouse hover";
        ShowCategorySetting.Title = "Show Category Badge";
        ShowCategorySetting.Subtitle = "Display category pill (Poetry, Literature, Anime, etc.) at top-left";
        ShowAttributionSetting.Title = "Show Attribution & Author";
        ShowAttributionSetting.Subtitle = "Display source work and author attribution at the bottom of the card";
        ShowLikesSetting.Title = "Show Community Likes (Core API)";
        ShowLikesSetting.Subtitle = "Fetch live like count from Hitokoto Core API and support one-click liking";
        FontFamilySetting.Title = "Quote Font";
        FontFamilySetting.Subtitle = "Choose display font for quotes (defaults to layout style recommendation)";
        FontSizeSetting.Title = "Font Size (0 = Auto)";
        FontSizeSetting.Subtitle = "Set to 0 for automatic responsive scaling, or fix between 12–28 px";

        FilterHeader.Text = "Auto Refresh & Filtering";
        IntervalSetting.Title = "Auto Refresh Interval";
        IntervalSetting.Subtitle = "Besides scheduled refresh, double-click the card anytime for a new quote";
        CategoriesSetting.Title = "Category Filter";
        CategoriesSetting.Subtitle = "Selecting all or none means all categories; or pick specific genres";
        ResetCategoriesBtn.Content = "Reset to All";
        MinLengthSetting.Title = "Minimum Quote Length";
        MinLengthSetting.Subtitle = "Filter out very short quotes (default: 0 chars)";
        MaxLengthSetting.Title = "Maximum Quote Length";
        MaxLengthSetting.Subtitle = "Prevent overly long quotes from crowding compact cards (default: 45 chars)";
        CustomApiSetting.Title = "Custom API Endpoint (Optional)";
        CustomApiSetting.Subtitle = "Leave blank to use official https://v1.hitokoto.cn/, or enter a self-hosted URL";
    }

    private void PopulateControls()
    {
        var styleOpts = StyleOptions;
        var intervalOpts = IntervalOptions;

        // 1. Style modes
        StyleModeCombo.ItemsSource = styleOpts.Select(x => x.Label).ToList();
        var styleIdx = Array.FindIndex(styleOpts, x => x.Mode == model.StyleMode);
        StyleModeCombo.SelectedIndex = styleIdx >= 0 ? styleIdx : 0;

        // 2. Toggles
        FramelessToggle.IsChecked = model.IsFrameless;
        AutoHideActionsToggle.IsChecked = model.AutoHideActions;
        ShowCategoryToggle.IsChecked = model.ShowCategoryTag;
        ShowAttributionToggle.IsChecked = model.ShowAttribution;
        ShowLikesToggle.IsChecked = model.ShowLikeCount;

        // 3. Fonts
        var fontItems = new List<string> { IsChinese ? "跟随排版默认" : "Default for layout style" };
        fontItems.AddRange(CachedSystemFontNames.Value);
        FontFamilyCombo.ItemsSource = fontItems;
        if (string.IsNullOrWhiteSpace(model.FontFamily))
        {
            FontFamilyCombo.SelectedIndex = 0;
        }
        else
        {
            var idx = fontItems.FindIndex(f => string.Equals(f, model.FontFamily, StringComparison.OrdinalIgnoreCase));
            FontFamilyCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }

        // 4. Font size & length
        FontSizeBox.Value = model.FontSize;
        MinLengthBox.Value = model.MinLength;
        MaxLengthBox.Value = model.MaxLength;
        CustomApiBox.Text = model.CustomApiUrl ?? string.Empty;

        // 5. Intervals
        IntervalCombo.ItemsSource = intervalOpts.Select(x => x.Label).ToList();
        var intervalIdx = Array.FindIndex(intervalOpts, x => x.Minutes == model.RefreshIntervalMinutes);
        IntervalCombo.SelectedIndex = intervalIdx >= 0 ? intervalIdx : 3;

        // 6. Categories checkboxes
        CategoriesWrap.Children.Clear();
        categoryChecks.Clear();
        var selectedSet = model.Categories is { Count: > 0 }
            ? new HashSet<string>(model.Categories, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var (code, name, desc) in HitokotoCategories.All)
        {
            var cb = new CheckBox
            {
                Classes = { "cat-check" },
                Content = name,
                IsChecked = selectedSet == null || selectedSet.Contains(code)
            };
            ToolTip.SetTip(cb, desc);
            cb.IsCheckedChanged += OnCategoryCheckChanged;
            categoryChecks[code] = cb;
            CategoriesWrap.Children.Add(cb);
        }
    }

    private void OnStyleModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        var idx = StyleModeCombo.SelectedIndex;
        if (idx >= 0 && idx < StyleOptions.Length)
        {
            SaveModel(model with { StyleMode = StyleOptions[idx].Mode });
        }
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (initializing) return;
        SaveModel(model with
        {
            IsFrameless = FramelessToggle.IsChecked == true,
            AutoHideActions = AutoHideActionsToggle.IsChecked == true,
            ShowCategoryTag = ShowCategoryToggle.IsChecked == true,
            ShowAttribution = ShowAttributionToggle.IsChecked == true,
            ShowLikeCount = ShowLikesToggle.IsChecked == true
        });
    }

    private void OnFontFamilyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        var selected = FontFamilyCombo.SelectedItem as string;
        var font = FontFamilyCombo.SelectedIndex <= 0 || string.IsNullOrWhiteSpace(selected)
            ? null
            : selected;
        SaveModel(model with { FontFamily = font });
    }

    private void OnIntervalChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        var idx = IntervalCombo.SelectedIndex;
        if (idx >= 0 && idx < IntervalOptions.Length)
        {
            SaveModel(model with { RefreshIntervalMinutes = IntervalOptions[idx].Minutes });
        }
    }

    private void OnCategoryCheckChanged(object? sender, RoutedEventArgs e)
    {
        if (initializing) return;
        var checkedCodes = categoryChecks
            .Where(kv => kv.Value.IsChecked == true)
            .Select(kv => kv.Key)
            .ToList();

        // Both "all checked" and "none checked" mean all categories
        List<string>? categories = (checkedCodes.Count == 0 || checkedCodes.Count == HitokotoCategories.All.Count)
            ? null
            : checkedCodes;

        SaveModel(model with { Categories = categories });
    }

    private void OnResetCategoriesClicked(object? sender, RoutedEventArgs e)
    {
        initializing = true;
        foreach (var cb in categoryChecks.Values)
            cb.IsChecked = true;
        initializing = false;

        SaveModel(model with { Categories = null });
    }

    private void OnNumericChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (initializing) return;
        var fontSize = (int)Math.Round(FontSizeBox.Value ?? 0);
        var minLen = (int)Math.Round(MinLengthBox.Value ?? HitokotoModel.DefaultMinLength);
        var maxLen = (int)Math.Round(MaxLengthBox.Value ?? HitokotoModel.DefaultMaxLength);
        if (maxLen < minLen + 5)
            maxLen = minLen + 5;

        SaveModel(model with
        {
            FontSize = fontSize,
            MinLength = minLen,
            MaxLength = maxLen
        });
    }

    private void OnCustomApiLostFocus(object? sender, RoutedEventArgs e)
    {
        if (initializing) return;
        var url = string.IsNullOrWhiteSpace(CustomApiBox.Text) ? null : CustomApiBox.Text.Trim();
        SaveModel(model with { CustomApiUrl = url });
    }

    private void SaveModel(HitokotoModel updated)
    {
        model = updated.Normalize();
        var currentLayout = widgetLayoutProvider.Get();
        var serialized = JsonSerializer.SerializeToElement(model);
        widgetLayoutProvider.Save(currentLayout with { Settings = serialized });
    }
}
