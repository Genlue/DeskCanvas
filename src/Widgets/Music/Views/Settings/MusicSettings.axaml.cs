using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Music.Models;
using Music.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Music.Views.Settings;

public partial class MusicSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    // Not readonly: the settings window caches pages, so this page can be unloaded and re-added to the
    // visual tree; the media service is disposed on unload and rebuilt on the next load.
    private MediaManagerService mediaService;
    private MusicModel model;
    private bool isInitializing = true;

    public MusicSettings() : this(null!) { }

    public MusicSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider!;
        mediaService = new MediaManagerService();
        model = widgetLayoutProvider != null ? (ReadModel(widgetLayoutProvider.Get()) ?? new MusicModel()) : new MusicModel();

        InitializeComponent();

        AmbientGlowToggle.IsChecked = model.AmbientGlow;
        ShowProgressToggle.IsChecked = model.ShowProgressBar;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        isInitializing = false;
        RefreshList();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // A cached page comes back after being unloaded, at which point its media service (and the
        // 1.5 s SMTC poll timer inside it) had to be released — rebuild it so "add from running
        // sessions" keeps working.
        if (mediaService.IsDisposed)
        {
            mediaService = new MediaManagerService();
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        // Without this the page's SMTC client, its event subscriptions and its 1.5 s poll timer live
        // for the rest of the process, once per opened settings page.
        mediaService.Dispose();
    }

    private void RefreshList()
    {
        RulesList.ItemsSource = null;
        RulesList.ItemsSource = model.PlayerRules.OrderBy(r => r.Priority).ToList();
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        model.AmbientGlow = AmbientGlowToggle.IsChecked ?? true;
        model.ShowProgressBar = ShowProgressToggle.IsChecked ?? true;
        Save();
    }

    private void OnRuleCheckedChange(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        Save();
    }

    private void OnMoveUpClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlayerRule rule })
        {
            var index = model.PlayerRules.IndexOf(rule);
            if (index > 0)
            {
                model.PlayerRules.RemoveAt(index);
                model.PlayerRules.Insert(index - 1, rule);
                ReassignPriorities();
                RefreshList();
                Save();
            }
        }
    }

    private void OnMoveDownClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlayerRule rule })
        {
            var index = model.PlayerRules.IndexOf(rule);
            if (index >= 0 && index < model.PlayerRules.Count - 1)
            {
                model.PlayerRules.RemoveAt(index);
                model.PlayerRules.Insert(index + 1, rule);
                ReassignPriorities();
                RefreshList();
                Save();
            }
        }
    }

    private void OnDeleteRuleClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlayerRule rule })
        {
            model.PlayerRules.Remove(rule);
            ReassignPriorities();
            RefreshList();
            Save();
        }
    }

    private void OnResetDefaultsClicked(object? sender, RoutedEventArgs e)
    {
        model.PlayerRules = PresetPlayers.CreateDefaultRules();
        RefreshList();
        Save();
    }

    private async void OnBrowseExeClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择音乐播放器可执行文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("应用程序") { Patterns = ["*.exe"] }
            ]
        });

        if (files.Count > 0)
        {
            var file = files[0];
            var path = file.Path.LocalPath;
            var name = Path.GetFileNameWithoutExtension(path);
            var pattern = Path.GetFileName(path);

            if (!model.PlayerRules.Any(r => r.MatchPattern.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                model.PlayerRules.Add(new PlayerRule(name, pattern, true, model.PlayerRules.Count, path));
                RefreshList();
                Save();
            }
        }
    }

    private async void OnAddFromRunningClicked(object? sender, RoutedEventArgs e)
    {
        // Capture the instance: unloading the page disposes (and the next load replaces) the field,
        // and a stale reference here would query a torn-down SMTC client.
        var service = mediaService;
        var sessions = await service.GetActiveSessionsAsync();
        // The page can be unloaded while the picker is open; a torn-down client cannot serve it.
        if (service.IsDisposed) return;

        if (sessions.Count == 0)
        {
            RunningSessionsList.ItemsSource = new List<ActiveSessionItem>
            {
                new("未检测到正在运行的媒体软件", "请先在任意音乐软件中播放音乐")
            };
        }
        else
        {
            RunningSessionsList.ItemsSource = sessions.Select(s => new ActiveSessionItem(
                $"{s.Title} - {s.Artist}",
                s.AppId
            )).ToList();
        }

        RunningSessionsPanel.IsVisible = true;
    }

    private void OnRunningSessionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (RunningSessionsList.SelectedItem is ActiveSessionItem item)
        {
            var appId = item.AppId;
            if (!string.IsNullOrWhiteSpace(appId) && !appId.Contains("请先在"))
            {
                var friendlyName = Path.GetFileNameWithoutExtension(appId);
                if (string.IsNullOrWhiteSpace(friendlyName)) friendlyName = appId;

                if (!model.PlayerRules.Any(r => r.MatchPattern.Equals(appId, StringComparison.OrdinalIgnoreCase)))
                {
                    model.PlayerRules.Insert(0, new PlayerRule(friendlyName, appId, true, 0));
                    ReassignPriorities();
                    RefreshList();
                    Save();
                }
            }

            RunningSessionsPanel.IsVisible = false;
            RunningSessionsList.SelectedItem = null;
        }
    }

    private void OnCloseRunningSessions(object? sender, RoutedEventArgs e)
    {
        RunningSessionsPanel.IsVisible = false;
    }

    private void OnAddManualRuleClicked(object? sender, RoutedEventArgs e)
    {
        ManualNameBox.Text = string.Empty;
        ManualPatternBox.Text = string.Empty;
        ManualAddPanel.IsVisible = true;
    }

    private void OnCancelManualAdd(object? sender, RoutedEventArgs e)
    {
        ManualAddPanel.IsVisible = false;
    }

    private void OnConfirmManualAdd(object? sender, RoutedEventArgs e)
    {
        var name = ManualNameBox.Text?.Trim();
        var pattern = ManualPatternBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(pattern))
        {
            model.PlayerRules.Insert(0, new PlayerRule(name, pattern, true, 0));
            ReassignPriorities();
            RefreshList();
            Save();
        }
        ManualAddPanel.IsVisible = false;
    }

    private void ReassignPriorities()
    {
        for (int i = 0; i < model.PlayerRules.Count; i++)
        {
            model.PlayerRules[i].Priority = i;
        }
    }

    private static MusicModel? ReadModel(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<MusicModel>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save()
    {
        widgetLayoutProvider.Save(widgetLayoutProvider.Get() with
        {
            Settings = JsonSerializer.SerializeToElement(model)
        });
    }
}
