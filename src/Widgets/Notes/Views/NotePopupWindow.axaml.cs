using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Notes.Locales;
using Notes.Models;
using Notes.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace Notes.Views;

/// <summary>
/// The note widget's secondary panel: a full-size editor for the note.
/// <list type="bullet">
/// <item>Internal source — the title and the markdown/plain body are editable;
/// every commit flows back through <c>onModelChanged</c> into the widget settings.</item>
/// <item>Single-file source — the document title is shown read-only and the body
/// edits write straight back to the markdown file.</item>
/// <item>Folder source — read-only rendered preview of the stacked documents
/// (they are edited on disk, not here).</item>
/// </list>
/// The shell (W7) plus <see cref="SecondaryPanelWindow"/> and
/// <see cref="PanelAppearanceResolver"/> own the material, the chrome and the open/close
/// transitions; this class keeps only the business behaviour: editing, the markdown preview
/// and the auto-save commits, plus the content text colours that stay plugin-owned.
/// </summary>
public partial class NotePopupWindow : SecondaryPanelWindow
{
    private NoteModel currentModel;
    private readonly Point? spawnScreenCenter;
    private readonly Action<NoteModel>? onModelChanged;
    private string? editingFilePath;

    /// <summary>While the editor text is being filled programmatically (populate),
    /// focus-loss commits must not run — they would see no change anyway, but the
    /// guard keeps the intent explicit.</summary>
    private bool suppressCommit;

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public NotePopupWindow() : this(new NoteModel(Locale.Notes_Title), null, null) { }

    public NotePopupWindow(
        NoteModel model,
        Point? screenCenter = null,
        Action<NoteModel>? onModelChanged = null,
        double? cornerRadius = null)
    {
        currentModel = model;
        spawnScreenCenter = screenCenter;
        this.onModelChanged = onModelChanged;

        InitializeComponent();
        InitializePanel(cornerRadius);

        ApplyContentTheme();
        Populate();

        Closed += OnWindowClosed;
    }

    public static void ShowPopup(
        NoteModel model,
        Point? screenCenter,
        Window? owner = null,
        Action<NoteModel>? onModelChanged = null,
        double? cornerRadius = null)
    {
        if (PanelCoolingDown<NotePopupWindow>())
            return;

        if (TryCloseActivePanel<NotePopupWindow>())
            return;

        var popup = new NotePopupWindow(model, screenCenter, onModelChanged, cornerRadius);
        popup.ShowAsSecondaryPanel(owner);
    }

    protected override void OnPanelLoaded()
    {
        if (BodyEditor.IsVisible)
        {
            BodyEditor.Focus();
            BodyEditor.CaretIndex = BodyEditor.Text?.Length ?? 0;
        }
    }

    // ---------- populate ----------

    private void Populate()
    {
        suppressCommit = true;
        try
        {
            switch (currentModel.Source)
            {
                case NoteSource.Folder:
                    ShowFolderPreview();
                    break;

                case NoteSource.File when ResolveSingleFile() is { } file:
                    editingFilePath = file;
                    TitleBox.IsReadOnly = true;
                    TitleBox.Text = NoteFiles.TitleOf(file, NoteFiles.Read(file));
                    ShowEditor(NoteFiles.Read(file) ?? "");
                    FooterText.Text = file;
                    break;

                default:
                    editingFilePath = null;
                    TitleBox.IsReadOnly = false;
                    TitleBox.Text = currentModel.Title ?? "";
                    ShowEditor(currentModel.Content ?? "");
                    SubtitleText.Text = currentModel.Updated?.ToString("g", CultureInfo.CurrentUICulture) ?? "";
                    FooterText.Text = currentModel.Updated?.ToString("g", CultureInfo.CurrentUICulture) ?? "";
                    break;
            }
        }
        finally
        {
            suppressCommit = false;
        }
    }

    private string? ResolveSingleFile()
    {
        var paths = NoteFiles.Resolve(currentModel);
        return paths.Count > 0 ? paths[0] : null;
    }

    private void ShowEditor(string text)
    {
        BodyEditor.Text = text;
        BodyEditor.IsVisible = true;
        PreviewHost.IsVisible = false;
    }

    private void ShowFolderPreview()
    {
        TitleBox.IsReadOnly = true;
        TitleBox.Text = currentModel.Title ?? Locale.Notes_Title;
        SubtitleText.Text = Locale.Notes_Source_Folder;
        FooterText.Text = Locale.Notes_Panel_ReadOnly;
        BodyEditor.IsVisible = false;
        PreviewHost.IsVisible = true;
        PreviewHost.Children.Clear();

        var paths = NoteFiles.Resolve(currentModel);
        if (paths.Count == 0)
        {
            PreviewHost.Children.Add(new TextBlock
            {
                Text = Locale.Notes_Empty,
                Opacity = 0.5,
                FontSize = 13,
                Margin = new Thickness(0, 24, 0, 0),
            });
            return;
        }

        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var content = NoteFiles.Read(path);

            if (index > 0)
                PreviewHost.Children.Add(BuildRule());

            PreviewHost.Children.Add(new TextBlock
            {
                Text = NoteFiles.TitleOf(path, content),
                FontWeight = FontWeight.Bold,
                FontSize = 15,
                Margin = new Thickness(0, index == 0 ? 0 : 4, 0, 4),
            });
            PreviewHost.Children.Add(
                MarkdownRenderer.Render(this, NoteFiles.StripTitleLine(content), 13, currentModel.MarkdownStyle));
        }
    }

    private static Control BuildRule() => new Border
    {
        Height = 1,
        Margin = new Thickness(0, 6),
        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
    };

    // ---------- theme ----------

    /// <summary>Content text colours stay plugin-owned: the shared shell never paints the
    /// editor's TextBoxes, so they follow the same light/dark rule as before. The card, the
    /// glass and the header chrome are the appearance binder's job.</summary>
    private void ApplyContentTheme()
    {
        var theme = PanelAppearanceResolver.ReadTheme(PanelAppearanceResolver.TryResolveSettingsProvider());
        bool isDark = PanelAppearanceResolver.IsDark(theme, ActualThemeVariant);

        IBrush textBrush = isDark ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 30, 30));
        IBrush subTextBrush = isDark ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        TitleBox.Foreground = textBrush;
        BodyEditor.Foreground = textBrush;
        SubtitleText.Foreground = subTextBrush;
        FooterText.Foreground = subTextBrush;
    }

    // ---------- commits ----------

    private void OnTitleLostFocus(object? sender, RoutedEventArgs e) => Commit();

    private void OnBodyLostFocus(object? sender, RoutedEventArgs e) => Commit();

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    /// <summary>Persist the edited title/content. Internal source flows back through
    /// <c>onModelChanged</c> into the widget settings; single-file source writes the
    /// document on disk and refreshes the card through the same callback.</summary>
    private void Commit()
    {
        if (suppressCommit) return;

        if (editingFilePath != null)
        {
            try
            {
                if (NoteFiles.Read(editingFilePath) != BodyEditor.Text)
                    NoteFiles.Write(editingFilePath, BodyEditor.Text ?? "");
            }
            catch
            {
                // Disk errors are surfaced by the file watch on the card; never crash the panel.
            }
            onModelChanged?.Invoke(currentModel);
            return;
        }

        if (currentModel.Source != NoteSource.Internal) return;

        var title = TitleBox.Text;
        var content = BodyEditor.Text;
        if (title == currentModel.Title && content == currentModel.Content) return;

        currentModel = currentModel with { Title = title, Content = content, Updated = DateTime.Now };
        onModelChanged?.Invoke(currentModel);
    }

    // ---------- window lifecycle ----------

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // Flush a pending edit (e.g. the user typed and hit ✕ without focusing away).
        suppressCommit = false;
        Commit();
    }
}
