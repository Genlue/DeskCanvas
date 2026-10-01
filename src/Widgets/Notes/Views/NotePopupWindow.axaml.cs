using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Notes.Locales;
using Notes.Models;
using Notes.Services;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
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
/// The surface (liquid glass / colorful / solid / acrylic) mirrors the primary
/// widget card exactly — same <see cref="SecondaryPanelWindow"/> plumbing as the
/// other popups.
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

    protected override Visual? PanelCard => CardBorder;
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

        ApplyTheme();
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
        if (LiquidGlassSurfaceControl.IsVisible)
        {
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
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

    private void ApplyTheme()
    {
        Theme theme;
        try
        {
            theme = new AppSettingsProvider().Get().Theme;
        }
        catch
        {
            theme = new Theme(DarkMode: true, AccentColor: null, OpacityLevel: 0.8, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter");
        }

        // Colour mode "follow system" is DarkMode == null, which must resolve to the *live*
        // variant — the same single source of truth the widget card uses (DarkMode ?? ActualThemeVariant).
        bool isDark = theme.DarkMode ?? ActualThemeVariant == ThemeVariant.Dark;

        if (theme.UsesRenderedGlass)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            LiquidGlassSurfaceControl.Material = theme;
            LiquidGlassSurfaceControl.CornerRadius = CardBorder.CornerRadius;
            LiquidGlassSurfaceControl.IsVisible = true;
            CardBorder.Background = Brushes.Transparent;
            CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
        else if (theme.IsColorful)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            CardBorder.Background = new SolidColorBrush(isDark ? Color.Parse("#1C1C1E") : Color.Parse("#FFFFFF"));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        }
        else if (theme.EffectiveSurface == SurfaceStyle.Solid)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            var hex = isDark ? theme.EffectiveSolidBackgroundDark : theme.EffectiveSolidBackgroundLight;
            var baseColor = Color.TryParse(hex, out var parsed) ? parsed : (isDark ? Color.FromRgb(46, 46, 46) : Colors.White);
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 255), 40, 255);
            CardBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        }
        else // Acrylic (毛玻璃)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 220), 40, 240);
            CardBorder.Background = new SolidColorBrush(isDark ? Color.FromArgb(alpha, 28, 28, 32) : Color.FromArgb(alpha, 245, 245, 248));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(35, 0, 0, 0));
        }

        IBrush textBrush = isDark ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 30, 30));
        IBrush subTextBrush = isDark ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        TitleBox.Foreground = textBrush;
        BodyEditor.Foreground = textBrush;
        CloseButton.Foreground = subTextBrush;
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

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // Flush a pending edit (e.g. the user typed and hit ✕ without focusing away).
        suppressCommit = false;
        Commit();

        LiquidGlassSurfaceControl.IsVisible = false;
    }
}
