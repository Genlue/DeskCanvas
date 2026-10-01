using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Search.Models;
using Search.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Search.Views.Settings;

public partial class SearchSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private SearchModel model;
    private bool isInitializing = true;
    private SearchEngine? editingEngine = null;

    private readonly ObservableCollection<SearchEngine> engines = [];

    public SearchSettings() : this(null!) { }

    public SearchSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider!;
        model = widgetLayoutProvider != null ? (ReadModel(widgetLayoutProvider.Get()) ?? new SearchModel()) : new SearchModel();

        InitializeComponent();

        InitCombos();
        LoadFromModel();
        isInitializing = false;
    }

    private void InitCombos()
    {
        EditCategoryCombo.ItemsSource = new List<string> { "通用", "AI 智能", "开发者", "影音媒体", "问答社区" };
        EditCategoryCombo.SelectedIndex = 0;

        EditIconCombo.ItemsSource = SearchIconProvider.EngineIcons.Keys.ToList();
        EditIconCombo.SelectedIndex = 0;
    }

    private void LoadFromModel()
    {
        ClearOnSearchToggle.IsChecked = model.ClearInputAfterSearch;
        SaveHistoryToggle.IsChecked = model.SaveHistory;

        engines.Clear();
        var list = model.CustomEngines != null && model.CustomEngines.Count > 0
            ? model.CustomEngines
            : PresetEngines.GetDefaults();

        foreach (var eng in list.OrderBy(e => e.Priority))
        {
            engines.Add(eng.Clone());
        }

        EnginesList.ItemsSource = engines;

        // Default Engine Combo
        DefaultEngineCombo.ItemsSource = engines.Select(e => e.Name).ToList();
        var defaultEng = engines.FirstOrDefault(e => e.Id.Equals(model.CurrentEngineId, StringComparison.OrdinalIgnoreCase))
                         ?? engines.FirstOrDefault();
        if (defaultEng != null)
        {
            DefaultEngineCombo.SelectedItem = defaultEng.Name;
        }
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        model = model with
        {
            ClearInputAfterSearch = ClearOnSearchToggle.IsChecked ?? true,
            SaveHistory = SaveHistoryToggle.IsChecked ?? true
        };
        Save();
    }

    private void OnDefaultEngineChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        if (DefaultEngineCombo.SelectedItem is string name)
        {
            var eng = engines.FirstOrDefault(x => x.Name == name);
            if (eng != null)
            {
                model = model with { CurrentEngineId = eng.Id };
                Save();
            }
        }
    }

    private void OnClearHistoryClicked(object? sender, RoutedEventArgs e)
    {
        model = model with { RecentHistory = [] };
        Save();
    }

    private void OnShowAddPanelClicked(object? sender, RoutedEventArgs e)
    {
        editingEngine = null;
        EditPanelTitle.Text = "添加自定义搜索引擎";
        EditNameBox.Text = "";
        EditUrlBox.Text = "";
        EditCategoryCombo.SelectedIndex = 0;
        EditIconCombo.SelectedIndex = 0;
        EditColorBox.Text = "#4285F4";
        EditPanel.IsVisible = true;
    }

    private void OnCancelEdit(object? sender, RoutedEventArgs e)
    {
        editingEngine = null;
        EditPanel.IsVisible = false;
    }

    private void OnConfirmEdit(object? sender, RoutedEventArgs e)
    {
        var name = EditNameBox.Text?.Trim() ?? "";
        var url = EditUrlBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return;

        var category = EditCategoryCombo.SelectedItem?.ToString() ?? "通用";
        var iconKey = EditIconCombo.SelectedItem?.ToString() ?? "Google";
        var color = EditColorBox.Text?.Trim() ?? "#4285F4";

        if (editingEngine != null)
        {
            editingEngine.Name = name;
            editingEngine.UrlTemplate = url;
            editingEngine.Category = category;
            editingEngine.IconKey = iconKey;
            editingEngine.IconColor = color;
        }
        else
        {
            var newEngine = new SearchEngine
            {
                Name = name,
                UrlTemplate = url,
                Category = category,
                IconKey = iconKey,
                IconColor = color,
                IsEnabled = true,
                IsCustom = true,
                Priority = engines.Count
            };
            engines.Add(newEngine);
        }

        EditPanel.IsVisible = false;
        editingEngine = null;
        SaveEngines();
    }

    private void OnEditEngineClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchEngine eng })
        {
            editingEngine = eng;
            EditPanelTitle.Text = $"编辑搜索引擎: {eng.Name}";
            EditNameBox.Text = eng.Name;
            EditUrlBox.Text = eng.UrlTemplate;
            EditCategoryCombo.SelectedItem = eng.Category;
            EditIconCombo.SelectedItem = eng.IconKey;
            EditColorBox.Text = eng.IconColor;
            EditPanel.IsVisible = true;
        }
    }

    private void OnDeleteEngineClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchEngine eng })
        {
            engines.Remove(eng);
            SaveEngines();
        }
    }

    private void OnMoveUpClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchEngine eng })
        {
            var idx = engines.IndexOf(eng);
            if (idx > 0)
            {
                engines.Move(idx, idx - 1);
                SaveEngines();
            }
        }
    }

    private void OnMoveDownClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchEngine eng })
        {
            var idx = engines.IndexOf(eng);
            if (idx < engines.Count - 1)
            {
                engines.Move(idx, idx + 1);
                SaveEngines();
            }
        }
    }

    private void OnEngineCheckboxClicked(object? sender, RoutedEventArgs e)
    {
        SaveEngines();
    }

    private void OnResetDefaultsClicked(object? sender, RoutedEventArgs e)
    {
        engines.Clear();
        foreach (var def in PresetEngines.GetDefaults())
        {
            engines.Add(def);
        }
        SaveEngines();
    }

    private void SaveEngines()
    {
        for (int i = 0; i < engines.Count; i++)
        {
            engines[i].Priority = i;
        }

        model = model with
        {
            CustomEngines = engines.Select(e => e.Clone()).ToList()
        };

        DefaultEngineCombo.ItemsSource = engines.Select(e => e.Name).ToList();
        var defaultEng = engines.FirstOrDefault(e => e.Id.Equals(model.CurrentEngineId, StringComparison.OrdinalIgnoreCase))
                         ?? engines.FirstOrDefault();
        if (defaultEng != null)
        {
            DefaultEngineCombo.SelectedItem = defaultEng.Name;
        }

        Save();
    }

    private void Save()
    {
        if (widgetLayoutProvider == null) return;
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout == null) return;
            widgetLayoutProvider.Save(layout with
            {
                Settings = JsonSerializer.SerializeToElement(model)
            });
        }
        catch
        {
            // Ignored
        }
    }

    private static SearchModel? ReadModel(WidgetLayout? layout)
    {
        if (layout?.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<SearchModel>();
        }
        catch
        {
            return null;
        }
    }
}
