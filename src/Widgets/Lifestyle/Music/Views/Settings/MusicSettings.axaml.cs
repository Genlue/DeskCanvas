using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Music.Locales;
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

    /// <summary>The last "add from running" probe found no session, so the single row in the list is
    /// the informational placeholder and must not be treated as a player to add.</summary>
    private bool sessionsEmpty;

    // 预设规则的显示名。这些名字历史上以中文落盘（既有设置文件里存的就是中文，不能改写），
    // 所以在 zh-Hans 资源里读到的原值既是"模型里存的样子"，也是"中文环境下要显示的字"；
    // 其它语言用同一资源键.CurrentUICulture 的值替换，用户自建的规则名则原样保留。
    private static string[] PresetRuleNameKeys { get; } =
    [
        nameof(Locale.Music_Player_Netease),
        nameof(Locale.Music_Player_QQMusic),
        nameof(Locale.Music_Player_Soda),
        nameof(Locale.Music_Player_Kugou),
        nameof(Locale.Music_Player_Kuwo),
        nameof(Locale.Music_Player_Wmp),
        nameof(Locale.Music_Player_Browser),
    ];

    private static Dictionary<string, string> BuildPresetNameTranslations()
    {
        var zh = CultureInfo.GetCultureInfo("zh-Hans");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in PresetRuleNameKeys)
        {
            var stored = Locale.ResourceManager.GetString(key, zh);
            var display = Locale.ResourceManager.GetString(key, Locale.Culture);
            if (!string.IsNullOrEmpty(stored) && !string.IsNullOrEmpty(display))
            {
                map[stored] = display;
            }
        }
        return map;
    }

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
        var translations = BuildPresetNameTranslations();
        if (translations.Count > 0)
        {
            foreach (var r in model.PlayerRules)
            {
                if (translations.TryGetValue(r.Name, out var display))
                {
                    r.Name = display;
                }
            }
        }
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
            Title = Locale.Music_BrowseExeTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Locale.Music_BrowseExeFilter) { Patterns = ["*.exe"] }
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

        // Remembering emptiness replaces the old "does the AppId contain 请先在?" sentinel string
        // test — whether a list row is selectable must not depend on its localized caption.
        sessionsEmpty = sessions.Count == 0;

        if (sessionsEmpty)
        {
            RunningSessionsList.ItemsSource = new List<ActiveSessionItem>
            {
                new(Locale.Music_NoSessionsTitle, Locale.Music_NoSessionsHint)
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
            if (!sessionsEmpty && !string.IsNullOrWhiteSpace(appId))
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
