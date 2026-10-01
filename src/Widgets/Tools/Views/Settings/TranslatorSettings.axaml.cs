using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tools.Models;
using Tools.Services.Translation;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Tools.Views.Settings;

public partial class TranslatorSettings : UserControl
{
    private record EngineOption(string Name, string Code)
    {
        public override string ToString() => Name;
    }

    private static readonly List<EngineOption> Engines =
    [
        new("有道翻译 (国内直连免Key)", "Youdao"),
        new("MyMemory (国际直连免Key)", "MyMemory"),
        new("百度翻译 (个人开放平台)", "Baidu"),
        new("DeepLX / 自定义接口", "DeepLX")
    ];

    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private TranslatorModel model;
    private bool isInitializing = true;

    public TranslatorSettings() : this(null!) { }

    public TranslatorSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider!;
        model = widgetLayoutProvider != null ? (ReadModel(widgetLayoutProvider.Get()) ?? new TranslatorModel()) : new TranslatorModel();

        InitializeComponent();

        DefaultEngineCombo.ItemsSource = Engines;
        DefaultEngineCombo.SelectedItem = Engines.FirstOrDefault(e => e.Code.Equals(model.Engine, StringComparison.OrdinalIgnoreCase)) ?? Engines[0];

        AutoTranslateToggle.IsChecked = model.AutoTranslate;
        BaiduAppIdBox.Text = model.BaiduAppId;
        BaiduSecretBox.Text = model.BaiduSecretKey;
        CustomUrlBox.Text = model.CustomUrl;

        isInitializing = false;
    }

    private void OnEngineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        if (DefaultEngineCombo.SelectedItem is EngineOption opt)
        {
            model = model with { Engine = opt.Code };
            Save();
            TranslationService.Instance.Configure(model);
        }
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        model = model with { AutoTranslate = AutoTranslateToggle.IsChecked ?? true };
        Save();
        TranslationService.Instance.Configure(model);
    }

    private void OnBaiduCredentialsChanged(object? sender, TextChangedEventArgs e)
    {
        if (isInitializing) return;
        model = model with
        {
            BaiduAppId = BaiduAppIdBox.Text?.Trim() ?? string.Empty,
            BaiduSecretKey = BaiduSecretBox.Text?.Trim() ?? string.Empty
        };
        Save();
        TranslationService.Instance.Configure(model);
    }

    private void OnCustomUrlChanged(object? sender, TextChangedEventArgs e)
    {
        if (isInitializing) return;
        model = model with
        {
            CustomUrl = CustomUrlBox.Text?.Trim() ?? "http://127.0.0.1:1188/translate"
        };
        Save();
        TranslationService.Instance.Configure(model);
    }

    private static TranslatorModel? ReadModel(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<TranslatorModel>();
        }
        catch
        {
            return null;
        }
    }

    private void Save()
    {
        if (widgetLayoutProvider == null) return;
        widgetLayoutProvider.Save(widgetLayoutProvider.Get() with
        {
            Settings = JsonSerializer.SerializeToElement(model)
        });
    }
}
