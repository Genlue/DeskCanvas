using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;
using Shortcuts.Models;
using Shortcuts.Services;

namespace Shortcuts.Views.Settings;

public partial class ShortcutsSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private ShortcutsModel currentModel;
    private bool isInitializing = true;
    private bool eventsAttached;

    public ShortcutsSettings(IWidgetLayoutProvider widgetLayoutProvider)
        : this(widgetLayoutProvider, null, null)
    {
    }

    public ShortcutsSettings(
        IWidgetLayoutProvider widgetLayoutProvider,
        IAppSettingsProvider? appSettings,
        WidgetFactory? widgetFactory)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        ShortcutsRuntime.Publish(appSettings, widgetFactory);

        currentModel = LoadModel();

        InitializeComponent();
        InitializeControls();
        isInitializing = false;

        Loaded += (_, _) =>
        {
            ShortcutsRuntime.PublishTopLevel(TopLevel.GetTopLevel(this));
            AttachEvents();
        };
        Unloaded += (_, _) =>
        {
            ShortcutsRuntime.ReleaseTopLevel(TopLevel.GetTopLevel(this));
            DetachEvents();
        };
    }

    // ---- events -------------------------------------------------------------------

    private void AttachEvents()
    {
        if (eventsAttached) return;
        eventsAttached = true;
        ShortcutsStore.ModelChanged += OnStoreChanged;
        PopulateShortcutCombo();

        try
        {
            if (ShortcutsStore.Get().Shortcuts.Any(s => !string.IsNullOrWhiteSpace(s.Hotkey)))
                ShortcutHotkeyService.EnsureStarted();
        }
        catch
        {
            // Hotkey setup must never take the settings page down.
        }
    }

    private void DetachEvents()
    {
        if (!eventsAttached) return;
        eventsAttached = false;
        ShortcutsStore.ModelChanged -= OnStoreChanged;
    }

    private void OnStoreChanged(ShortcutsDocument document, object? sender)
    {
        if (Dispatcher.UIThread.CheckAccess()) PopulateShortcutCombo();
        else Dispatcher.UIThread.Post(PopulateShortcutCombo);
    }

    // ---- model --------------------------------------------------------------------

    private ShortcutsModel LoadModel()
    {
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout.Settings is { } settings && settings.ValueKind == JsonValueKind.Object)
            {
                var parsed = settings.Deserialize<ShortcutsModel>();
                if (parsed != null) return parsed;
            }
        }
        catch
        {
            // Fall through to defaults.
        }

        return new ShortcutsModel();
    }

    private void InitializeControls()
    {
        MaxTilesCombo.Items.Clear();
        var selectedIndex = 0;
        var options = new[] { 4, 6, 8, 10, 12 };
        for (var i = 0; i < options.Length; i++)
        {
            MaxTilesCombo.Items.Add(new ComboBoxItem { Content = options[i].ToString(), Tag = options[i] });
            if (options[i] == currentModel.MaxTiles) selectedIndex = i;
        }

        MaxTilesCombo.SelectedIndex = selectedIndex;
        ConfirmSwitch.IsChecked = currentModel.ConfirmBeforeRun;
        PopulateShortcutCombo();
    }

    /// <summary>
    /// The shortcut list of the "edit" row: keeps the current selection when possible, otherwise
    /// restores the last shortcut opened in the editor.
    /// </summary>
    private void PopulateShortcutCombo()
    {
        var selectedId = (ShortcutCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var document = ShortcutsStore.Get();
        var target = selectedId ?? document.LastEditedId;

        ShortcutCombo.Items.Clear();
        var selectedIndex = 0;
        for (var i = 0; i < document.Shortcuts.Count; i++)
        {
            var shortcut = document.Shortcuts[i];
            ShortcutCombo.Items.Add(new ComboBoxItem { Content = shortcut.Name, Tag = shortcut.Id });
            if (shortcut.Id == target) selectedIndex = i;
        }

        if (ShortcutCombo.ItemCount > 0)
            ShortcutCombo.SelectedIndex = selectedIndex;
    }

    // ---- handlers -----------------------------------------------------------------

    private void OnMaxTilesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        if (MaxTilesCombo.SelectedItem is ComboBoxItem { Tag: int maxTiles })
        {
            currentModel = currentModel with { MaxTiles = maxTiles };
            SaveSettings();
        }
    }

    private void OnConfirmChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        currentModel = currentModel with { ConfirmBeforeRun = ConfirmSwitch.IsChecked == true };
        SaveSettings();
    }

    private void OnOpenEditorClicked(object? sender, RoutedEventArgs e)
    {
        var shortcutId = (ShortcutCombo.SelectedItem as ComboBoxItem)?.Tag as string;

        if (shortcutId == null)
        {
            var blank = ShortcutTemplates.Blank();
            ShortcutsStore.Upsert(blank, this);
            shortcutId = blank.Id;
        }

        ShortcutsEditorWindow.ShowEditor(shortcutId, TopLevel.GetTopLevel(this) as Window);
    }

    private void SaveSettings()
    {
        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(currentModel) };
            widgetLayoutProvider.Save(layout);
        }
        catch
        {
            // Settings persistence is best-effort here; the layout store logs its own failures.
        }
    }

    // ---- AI authoring -------------------------------------------------------------

    /// <summary>
    /// Copy the generated JSON spec. This is the whole feature's entry point: the user pastes it
    /// into any chat model, describes what they want, and imports the reply.
    /// </summary>
    private async void OnCopySpecClicked(object? sender, RoutedEventArgs e)
    {
        var spec = WorkflowSpecDocument.Build();
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(spec);

            CopySpecButton.Content = Loc.Get("Shortcuts_Spec_Copied");
            ShowAiStatus(Loc.Get("Shortcuts_Ai_Copied"), isError: false);
        }
        catch (Exception ex)
        {
            ShowAiStatus(ex.Message, isError: true);
        }
    }

    private void OnPreviewSpecClicked(object? sender, RoutedEventArgs e)
    {
        var showing = !SpecPreview.IsVisible;
        if (showing) SpecPreview.Text = WorkflowSpecDocument.Build();
        SpecPreview.IsVisible = showing;
        PreviewSpecButton.Content = Loc.Get(showing
            ? "Shortcuts_Spec_Hide"
            : "Shortcuts_Spec_Preview");
    }

    private async void OnImportJsonClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner == null) return;

            var result = await WorkflowImportWindow.PromptAsync(owner);
            if (result is not { Success: true }) return;

            foreach (var imported in result.Shortcuts)
            {
                imported.UpdatedAt = DateTime.Now;
                ShortcutsStore.Upsert(imported, this);
            }

            PopulateShortcutCombo();
            ShowAiStatus(
                Loc.Format("Shortcuts_Ai_Imported", result.Shortcuts[0].Name, result.Shortcuts[0].ActionCount),
                isError: false);
        }
        catch (Exception ex)
        {
            ShowAiStatus(ex.Message, isError: true);
        }
    }

    private void ShowAiStatus(string text, bool isError)
    {
        AiStatusText.Text = text;
        AiStatusText.IsVisible = !string.IsNullOrWhiteSpace(text);
        AiStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(isError ? "#A32D2D" : "#3B6D11"));
    }
}
