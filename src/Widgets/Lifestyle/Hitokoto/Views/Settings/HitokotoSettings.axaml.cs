using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeskCanvas.Core.Interfaces;
using Hitokoto.Locales;
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

    private static (int Minutes, string Label)[] IntervalOptions =>
    [
        (0, Locale.Hitokoto_Interval_Manual),
        (5, Locale.Hitokoto_Interval_5m),
        (15, Locale.Hitokoto_Interval_15m),
        (30, Locale.Hitokoto_Interval_30m),
        (60, Locale.Hitokoto_Interval_1h),
        (120, Locale.Hitokoto_Interval_2h),
        (1440, Locale.Hitokoto_Interval_Daily)
    ];

    private static (HitokotoStyleMode Mode, string Label)[] StyleOptions =>
    [
        (HitokotoStyleMode.ModernQuote, Locale.Hitokoto_Style_ModernQuote),
        (HitokotoStyleMode.PoeticCenter, Locale.Hitokoto_Style_PoeticCenter),
        (HitokotoStyleMode.MinimalCard, Locale.Hitokoto_Style_MinimalCard)
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
        PopulateControls();
        initializing = false;
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
        var fontItems = new List<string> { Locale.Hitokoto_FontDefault };
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
        // 分类名与提示来自 HitokotoCategories 业务数据表（与 API 类型码绑定），随数据原样显示。
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
