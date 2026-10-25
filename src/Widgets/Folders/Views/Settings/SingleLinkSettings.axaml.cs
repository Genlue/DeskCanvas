using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;
using DeskCanvas.Views.Controls;
using Folders.Locales;
using Folders.Models;
using Folders.Services;

#pragma warning disable CA1416

namespace Folders.Views.Settings;

public sealed record BrowserOption(string Name, string? Path)
{
    public override string ToString() => Name;
}

public partial class SingleLinkSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private SingleLinkModel model;
    private bool syncing;

    public SingleLinkSettings() : this(null!) { }

    public SingleLinkSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = widgetLayoutProvider != null ? ReadModel(widgetLayoutProvider.Get()) ?? new SingleLinkModel() : new SingleLinkModel();

        InitializeComponent();

        syncing = true;

        UrlBox.Text = model.Url ?? "";
        TitleBox.Text = model.Title ?? "";
        IconPercentSlider.Value = model.IconPercent;
        HoverZoomSlider.Value = model.HoverZoom;

        UrlBox.TextChanged += OnUrlTextChanged;
        TitleBox.TextChanged += OnTitleTextChanged;
        BrowserComboBox.SelectionChanged += OnBrowserSelectionChanged;

        PopulateBrowsers();
        UpdateIconPreview();

        syncing = false;
    }

    private static SingleLinkModel? ReadModel(WidgetLayout layout)
    {
        if (!layout.Settings.HasValue) return null;
        try
        {
            return JsonSerializer.Deserialize<SingleLinkModel>(layout.Settings.Value.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    private void Save(SingleLinkModel newModel)
    {
        model = newModel;
        if (widgetLayoutProvider == null) return;
        var element = JsonSerializer.SerializeToElement(newModel);
        var layout = widgetLayoutProvider.Get() with { Settings = element };
        widgetLayoutProvider.Save(layout);
    }

    private void PopulateBrowsers()
    {
        var options = new List<BrowserOption>
        {
            new(Locale.Folders_SingleLink_Browser_Default, null)
        };

        try
        {
            var detected = WebBrowsers.Detect();
            foreach (var b in detected)
            {
                options.Add(new BrowserOption(b.Name, b.Path));
            }
        }
        catch
        {
            // Browser detection fallback
        }

        // If the saved BrowserPath is not in detected list, add it
        if (!string.IsNullOrWhiteSpace(model.BrowserPath) && File.Exists(model.BrowserPath))
        {
            if (!options.Any(o => string.Equals(o.Path, model.BrowserPath, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new BrowserOption(Path.GetFileNameWithoutExtension(model.BrowserPath), model.BrowserPath));
            }
        }

        BrowserComboBox.ItemsSource = options;

        var selected = options.FirstOrDefault(o =>
            string.Equals(o.Path, model.BrowserPath, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        BrowserComboBox.SelectedItem = selected;
    }

    private void UpdateIconPreview()
    {
        var icon = !string.IsNullOrWhiteSpace(model.CustomIconPath) && File.Exists(model.CustomIconPath)
            ? LinkIconService.LoadIcon(model.CustomIconPath)
            : null;

        if (icon != null)
        {
            IconPreviewImage.Source = icon;
            IconPreviewImage.IsVisible = true;
            IconPreviewFallback.IsVisible = false;
        }
        else
        {
            IconPreviewImage.Source = null;
            IconPreviewImage.IsVisible = false;
            IconPreviewFallback.IsVisible = true;
        }

        FetchFaviconButton.IsEnabled = !string.IsNullOrWhiteSpace(model.Url);
    }

    private void OnHoverZoomCommitted(object? sender, LabeledSliderValueCommittedEventArgs e)
    {
        if (syncing) return;
        var zoom = Math.Clamp(Math.Round(e.Value), 100, 150);
        if (Math.Abs(zoom - model.HoverZoom) < 0.01) return;
        Save(model with { HoverZoom = zoom });
    }

    private void OnUrlTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (syncing) return;
        var text = UrlBox.Text?.Trim();
        Save(model with { Url = string.IsNullOrWhiteSpace(text) ? null : text });
        FetchFaviconButton.IsEnabled = !string.IsNullOrWhiteSpace(text);
    }

    private void OnTitleTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (syncing) return;
        var text = TitleBox.Text?.Trim();
        Save(model with { Title = string.IsNullOrWhiteSpace(text) ? null : text });
    }

    private void OnBrowserSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing) return;
        if (BrowserComboBox.SelectedItem is BrowserOption option)
        {
            Save(model with { BrowserPath = option.Path });
        }
    }

    private void OnIconPercentCommitted(object? sender, LabeledSliderValueCommittedEventArgs e)
    {
        if (syncing) return;
        var value = Math.Round(e.Value);
        Save(model with { IconPercent = value });
    }

    private void OnClearUrl(object? sender, RoutedEventArgs e)
    {
        UrlBox.Text = "";
        Save(model with { Url = null });
        FetchFaviconButton.IsEnabled = false;
    }

    private async void OnPickLocalIcon(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Locale.Folders_SingleLink_PickLocalIcon,
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Image / Icon Files")
                {
                    Patterns = new[] { "*.ico", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.svg" }
                },
                new("All Files")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        if (files.Count > 0 && files[0].Path.LocalPath is { } localPath)
        {
            var savedPath = await LinkIconService.SaveLocalFileAsync(localPath);
            if (!string.IsNullOrWhiteSpace(savedPath))
            {
                Save(model with { CustomIconPath = savedPath });
                UpdateIconPreview();
                ShowStatus(Locale.Folders_SingleLink_FetchSuccess);
            }
        }
    }

    private async void OnFetchFavicon(object? sender, RoutedEventArgs e)
    {
        var url = model.Url;
        if (string.IsNullOrWhiteSpace(url)) return;

        ShowStatus(Locale.Folders_SingleLink_Fetching);
        FetchFaviconButton.IsEnabled = false;

        try
        {
            var cached = await LinkIconService.FetchFaviconAsync(url);
            if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
            {
                Save(model with { CustomIconPath = cached });
                UpdateIconPreview();
                ShowStatus(Locale.Folders_SingleLink_FetchSuccess);
            }
            else
            {
                ShowStatus(Locale.Folders_SingleLink_FetchFailed);
            }
        }
        catch
        {
            ShowStatus(Locale.Folders_SingleLink_FetchFailed);
        }
        finally
        {
            FetchFaviconButton.IsEnabled = true;
        }
    }

    private async void OnDownloadWebIcon(object? sender, RoutedEventArgs e)
    {
        var imageUrl = WebIconUrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(imageUrl)) return;

        ShowStatus(Locale.Folders_SingleLink_Fetching);

        try
        {
            var cached = await LinkIconService.DownloadFromUrlAsync(imageUrl);
            if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
            {
                Save(model with { CustomIconPath = cached });
                UpdateIconPreview();
                WebIconUrlBox.Text = "";
                ShowStatus(Locale.Folders_SingleLink_FetchSuccess);
            }
            else
            {
                ShowStatus(Locale.Folders_SingleLink_FetchFailed);
            }
        }
        catch
        {
            ShowStatus(Locale.Folders_SingleLink_FetchFailed);
        }
    }

    private void OnClearIcon(object? sender, RoutedEventArgs e)
    {
        Save(model with { CustomIconPath = null });
        UpdateIconPreview();
        StatusMessageText.IsVisible = false;
    }

    private void ShowStatus(string message)
    {
        StatusMessageText.Text = message;
        StatusMessageText.IsVisible = true;
    }
}
