using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Notes.Locales;
using Notes.Models;
using Notes.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;

namespace Notes.Views.Settings;

/// <summary>
/// Per-widget note settings: markdown rendering with per-format typography
/// (custom font, light/dark color palettes), the colored title bar
/// (follow accent / custom color, plus opacity) and the body source
/// (internal text, one markdown file, or a folder of markdown files with
/// a document count and recent/manual selection).
/// </summary>
public partial class NoteSettings : UserControl
{
    /// <summary>The markdown formats that can get a custom color.</summary>
    private enum MdFormat { Text, Heading, Bold, Italic, Strike, Link, Code, Quote }

    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;

    /// <summary>Font rows: "follow app font" + every installed family.</summary>
    private readonly List<string> fontLabels;

    /// <summary>The installed families behind <see cref="fontLabels"/> (index + 1).</summary>
    private readonly List<string> fontNames;

    /// <summary>The eight color rows, wired generically.</summary>
    private readonly List<(MdFormat Format, ColorPicker Picker, Button Reset)> styleRows = [];

    /// <summary>Rows whose next pure-Black event is the flyout's init push, not a user pick.</summary>
    private readonly HashSet<MdFormat> armedSwallow = [];

    private bool headerSwallowArmed = true;

    private bool syncing;

    public NoteSettings(IWidgetLayoutProvider widgetLayoutProvider, IAppSettingsProvider? appSettingsProvider = null)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.appSettingsProvider = appSettingsProvider;
        InitializeComponent();

        HeaderModeBox.ItemsSource = new[] { Locale.Notes_Header_FollowAccent, Locale.Notes_Header_Custom };
        SourceBox.ItemsSource = new[] { Locale.Notes_Source_Internal, Locale.Notes_Source_File, Locale.Notes_Source_Folder };
        OrderBox.ItemsSource = new[] { Locale.Notes_Order_Recent, Locale.Notes_Order_Manual };

        fontNames = SystemFonts.GetFonts().ToList();
        fontLabels = [Locale.Notes_Style_Font_Default, .. fontNames];
        FontBox.ItemsSource = fontLabels;
        ModeBox.ItemsSource = new[] { Locale.Notes_Style_Mode_Light, Locale.Notes_Style_Mode_Dark };

        styleRows.AddRange(
        [
            (MdFormat.Text, StyleTextColor, StyleTextReset),
            (MdFormat.Heading, StyleHeadingColor, StyleHeadingReset),
            (MdFormat.Bold, StyleBoldColor, StyleBoldReset),
            (MdFormat.Italic, StyleItalicColor, StyleItalicReset),
            (MdFormat.Strike, StyleStrikeColor, StyleStrikeReset),
            (MdFormat.Link, StyleLinkColor, StyleLinkReset),
            (MdFormat.Code, StyleCodeColor, StyleCodeReset),
            (MdFormat.Quote, StyleQuoteColor, StyleQuoteReset),
        ]);

        // Programmatic pre-fill happens BEFORE the handlers are attached, so the
        // handlers only ever see user interaction.
        Load(widgetLayoutProvider.Get().GetModel<NoteModel>() ?? new NoteModel(Locale.Notes_Title));

        MarkdownToggle.Click += (_, _) =>
            UpdateModel(m => m with { Markdown = MarkdownToggle.IsChecked == true });
        AllowInlineEditToggle.Click += (_, _) =>
            UpdateModel(m => m with { AllowInlineEdit = AllowInlineEditToggle.IsChecked == true });
        ShowTitleToggle.Click += (_, _) =>
            UpdateModel(m => m with { ShowTitle = ShowTitleToggle.IsChecked == true });
        StyleToggle.Click += (_, _) =>
        {
            var enabled = StyleToggle.IsChecked == true;
            UpdateModel(m => m with
            {
                MarkdownStyle = (m.MarkdownStyle ?? new MarkdownTypography()) with { Enabled = enabled },
            });
            StyleSection.IsVisible = enabled;
            if (enabled) SyncColorRows();
        };
        ModeBox.SelectionChanged += (_, _) => SyncColorRows();
        FontBox.SelectionChanged += (_, _) =>
        {
            var index = FontBox.SelectedIndex;
            var font = index <= 0 ? null : fontNames[index - 1];
            UpdateModel(m => m with
            {
                MarkdownStyle = (m.MarkdownStyle ?? new MarkdownTypography()) with { Font = font },
            });
        };

        foreach (var (format, picker, reset) in styleRows)
        {
            // The flyout pushes its uninitialized Black once on the very first
            // open (template applies lazily) — disarm after that first event,
            // whatever it was, so genuine later picks always land.
            armedSwallow.Add(format);
            picker.ColorChanged += (_, _) =>
            {
                if (syncing) return;
                // A picker flyout initializes its spectrum at pure Black and pushes
                // that value out on the very first open. For a row without a custom
                // color whose theme default is NOT black, that commit is spurious:
                // saving it would turn e.g. dark-mode body text invisible. Swallow
                // it and re-assert the prefill instead.
                if (armedSwallow.Contains(format) &&
                    picker.Color == Colors.Black && StoredColor(format) == null &&
                    DefaultColor(format) != Colors.Black)
                {
                    armedSwallow.Remove(format);
                    SyncColorRows();
                    return;
                }

                armedSwallow.Remove(format);
                SaveColor(format, picker.Color.ToString());
                reset.IsVisible = true;
            };
            reset.Click += (_, _) =>
            {
                SaveColor(format, null);
                SyncColorRows();
            };
        }

        BodyPaddingBox.ValueChanged += (_, _) =>
            UpdateModel(m => m with { BodyPadding = (int)(BodyPaddingBox.Value ?? 4) });
        HeaderModeBox.SelectionChanged += (_, _) =>
        {
            var follow = HeaderModeBox.SelectedIndex == 0;
            UpdateModel(m => m with
            {
                FollowAccentHeader = follow,
                HeaderColor = follow ? m.HeaderColor : m.HeaderColor ?? "#FF3376CD",
            });
            HeaderColorPicker.IsVisible = !follow;
        };
        HeaderColorPicker.ColorChanged += (_, _) =>
        {
            // First-open init push of the flyout (see the style rows): a lone
            // pure Black while no custom header color exists is not a user pick.
            if (headerSwallowArmed && HeaderColorPicker.Color == Colors.Black && CurrentModel.HeaderColor == null)
            {
                headerSwallowArmed = false;
                syncing = true;
                HeaderColorPicker.Color = Color.Parse("#3376CD");
                syncing = false;
                return;
            }

            headerSwallowArmed = false;
            UpdateModel(m => m with { HeaderColor = HeaderColorPicker.Color.ToString() });
        };
        HeaderOpacityBox.ValueChanged += (_, _) =>
            UpdateModel(m => m with { HeaderOpacity = (double)(HeaderOpacityBox.Value ?? 1.0m) });
        SourceBox.SelectionChanged += (_, _) =>
        {
            UpdateModel(m => m with { Source = (NoteSource)Math.Max(0, SourceBox.SelectedIndex) });
            UpdateSourceVisibility();
            RebuildFileList();
        };
        DocumentCountBox.ValueChanged += (_, _) =>
            UpdateModel(m => m with { DocumentCount = (int)(DocumentCountBox.Value ?? 3) });
        OrderBox.SelectionChanged += (_, _) =>
        {
            UpdateModel(m => m with { RecentFiles = OrderBox.SelectedIndex == 0 });
            FilesSetting.IsVisible = OrderBox.SelectedIndex == 1;
        };

        // Resources (theme brushes, accent) only resolve once the control is in
        // the tree — re-sync the prefill colors after the constructor ran detached.
        Loaded += (_, _) => SyncColorRows();
    }

    private NoteModel CurrentModel =>
        widgetLayoutProvider.Get().GetModel<NoteModel>() ?? new NoteModel(Locale.Notes_Title);

    /// <summary>Which palette the color rows edit right now (dark ↔ light).</summary>
    private bool EditingDark => ModeBox.SelectedIndex == 1;

    private void Load(NoteModel model)
    {
        MarkdownToggle.IsChecked = model.Markdown;
        AllowInlineEditToggle.IsChecked = model.AllowInlineEdit;
        ShowTitleToggle.IsChecked = model.ShowTitle;

        var style = model.MarkdownStyle;
        StyleToggle.IsChecked = style?.Enabled ?? false;
        StyleSection.IsVisible = style?.Enabled ?? false;

        // Default to editing the palette of the mode the app is in right now.
        var actualDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        ModeBox.SelectedIndex = actualDark ? 1 : 0;

        var fontIndex = style?.Font == null ? 0 : fontNames.IndexOf(style.Font) + 1;
        FontBox.SelectedIndex = fontIndex > 0 ? fontIndex : 0;

        BodyPaddingBox.Value = Math.Clamp(model.BodyPadding, 0, 64);

        var isColorful = (appSettingsProvider?.Get().Theme.IsColorful == true) ||
                         (Application.Current != null && Application.Current.TryFindResource("MacosYellowBrush", out _));
        if (isColorful)
        {
            HeaderModeBox.IsEnabled = false;
            HeaderColorPicker.IsEnabled = false;
            HeaderColorPicker.IsVisible = true;
            HeaderColorPicker.Color = actualDark ? Color.Parse("#FFD60A") : Color.Parse("#FFCC00");
            HeaderOpacityBox.IsEnabled = false;
            HeaderOpacityBox.Value = 1.00m;
        }
        else
        {
            HeaderModeBox.IsEnabled = true;
            HeaderColorPicker.IsEnabled = true;
            HeaderModeBox.SelectedIndex = model.FollowAccentHeader ? 0 : 1;
            HeaderColorPicker.IsVisible = !model.FollowAccentHeader;
            HeaderColorPicker.Color = Color.TryParse(model.HeaderColor, out var color) ? color : Color.Parse("#3376CD");
            HeaderOpacityBox.IsEnabled = true;
            HeaderOpacityBox.Value = (decimal)Math.Round(model.HeaderOpacity, 2);
        }

        SourceBox.SelectedIndex = (int)model.Source;
        OrderBox.SelectedIndex = model.RecentFiles ? 0 : 1;
        DocumentCountBox.Value = Math.Clamp(model.DocumentCount, 1, 8);
        FilesSetting.IsVisible = model.Source == NoteSource.Folder && !model.RecentFiles;
        UpdateSourceVisibility();
        RebuildFileList();

        SyncColorRows();
    }

    // ---------- markdown style ----------

    /// <summary>
    /// Load the colors of the selected palette into the pickers. Formats without
    /// a custom color show the theme default they fall back to (the "Default"
    /// reset button is hidden for them).
    /// </summary>
    private void SyncColorRows()
    {
        var model = CurrentModel;
        var palette = (EditingDark ? model.MarkdownStyle?.Dark : model.MarkdownStyle?.Light) ?? new MarkdownPalette();

        syncing = true;
        try
        {
            foreach (var (format, picker, reset) in styleRows)
            {
                var custom = GetColor(palette, format);
                reset.IsVisible = custom != null;
                picker.Color = Color.TryParse(custom, out var color) ? color : DefaultColor(format);
            }
        }
        finally
        {
            syncing = false;
        }
    }

    private void SaveColor(MdFormat format, string? hex)
    {
        UpdateModel(m => m with
        {
            MarkdownStyle = SetPaletteColor(m.MarkdownStyle, EditingDark, format, hex),
        });
    }

    private static MarkdownTypography SetPaletteColor(MarkdownTypography? typography, bool dark, MdFormat format, string? hex)
    {
        var style = typography ?? new MarkdownTypography();
        var palette = (dark ? style.Dark : style.Light) ?? new MarkdownPalette();
        palette = SetColor(palette, format, hex);
        return dark ? style with { Dark = palette } : style with { Light = palette };
    }

    private static MarkdownPalette SetColor(MarkdownPalette palette, MdFormat format, string? hex) => format switch
    {
        MdFormat.Text => palette with { BodyColor = hex },
        MdFormat.Heading => palette with { HeadingColor = hex },
        MdFormat.Bold => palette with { BoldColor = hex },
        MdFormat.Italic => palette with { ItalicColor = hex },
        MdFormat.Strike => palette with { StrikeColor = hex },
        MdFormat.Link => palette with { LinkColor = hex },
        MdFormat.Code => palette with { CodeColor = hex },
        MdFormat.Quote => palette with { QuoteColor = hex },
        _ => palette,
    };

    private static string? GetColor(MarkdownPalette palette, MdFormat format) => format switch
    {
        MdFormat.Text => palette.BodyColor,
        MdFormat.Heading => palette.HeadingColor,
        MdFormat.Bold => palette.BoldColor,
        MdFormat.Italic => palette.ItalicColor,
        MdFormat.Strike => palette.StrikeColor,
        MdFormat.Link => palette.LinkColor,
        MdFormat.Code => palette.CodeColor,
        MdFormat.Quote => palette.QuoteColor,
        _ => null,
    };

    /// <summary>The custom color stored for a format in the palette being edited (null = default).</summary>
    private string? StoredColor(MdFormat format)
    {
        var style = CurrentModel.MarkdownStyle;
        return GetColor((EditingDark ? style?.Dark : style?.Light) ?? new MarkdownPalette(), format);
    }

    /// <summary>
    /// The theme color a format falls back to while it has no custom color:
    /// the app accent for links, the theme text color otherwise. The value is
    /// resolved with an EXPLICIT variant (the palette being edited) through the
    /// application resource host — an ambient lookup would freeze on the
    /// "Default" (light) theme dictionary of the app's monochrome style
    /// overrides and prefill e.g. a dark-mode row with a near-black blue.
    /// Always opaque: the editor cannot represent the alpha the theme brushes
    /// may carry, and a semi-transparent prefill invites accidental dark-on-dark.
    /// </summary>
    private Color DefaultColor(MdFormat format)
    {
        var variant = EditingDark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (Application.Current is IResourceHost host)
        {
            if (format == MdFormat.Link)
            {
                if (host.TryGetResource("SystemAccentColor", variant, out var accent) && accent is Color accentColor)
                    return accentColor;
                return Color.Parse("#0078D4");
            }

            if (host.TryGetResource("SystemControlForegroundBaseHighBrush", variant, out var brush) &&
                brush is ISolidColorBrush solid)
            {
                var raw = solid.Color;
                return new Color(255, raw.R, raw.G, raw.B);
            }
        }

        return EditingDark ? Color.Parse("#FFFFFF") : Colors.Black;
    }

    // ---------- header / source (unchanged behavior) ----------

    private void UpdateSourceVisibility()
    {
        var source = (NoteSource)Math.Max(0, SourceBox.SelectedIndex);
        BrowseButton.IsVisible = source != NoteSource.Internal;
        var path = CurrentModel.Path;
        PathText.IsVisible = source != NoteSource.Internal && !string.IsNullOrEmpty(path);
        PathText.Text = path ?? "";
        FolderSection.IsVisible = source == NoteSource.Folder;
    }

    private async void Browse(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var source = (NoteSource)Math.Max(0, SourceBox.SelectedIndex);
        string? picked = null;

        if (source == NoteSource.Folder)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Locale.Notes_Source_Folder,
                AllowMultiple = false,
            });
            picked = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        else if (source == NoteSource.File)
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Locale.Notes_Source_File,
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Markdown") { Patterns = ["*.md"] } },
            });
            picked = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        if (picked == null) return;
        UpdateModel(m => m with { Path = picked });
        UpdateSourceVisibility();
        RebuildFileList();
    }

    private void RebuildFileList()
    {
        FilesPanel.Children.Clear();
        var model = CurrentModel;
        if (model.Source != NoteSource.Folder || model.Path == null || !Directory.Exists(model.Path)) return;

        string[] files;
        try
        {
            files = Directory.GetFiles(model.Path, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }

        Array.Sort(files, (a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        var selected = new HashSet<string>(model.SelectedFiles ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var check = new CheckBox
            {
                Content = name,
                IsChecked = selected.Contains(name),
                FontSize = 12,
            };
            void OnCheckChanged(object? sender, RoutedEventArgs e)
            {
                if (check.IsChecked == true) selected.Add(name);
                else selected.Remove(name);
                UpdateModel(m => m with { SelectedFiles = selected.ToList() });
            }
            // Checked and Unchecked both used to call OnCheckChanged; IsCheckedChanged fires
            // for exactly the same transitions (false→true, true→false, null→value, value→null),
            // so a single subscription keeps the previous behaviour.
            check.IsCheckedChanged += OnCheckChanged;
            FilesPanel.Children.Add(check);
        }
    }

    private void UpdateModel(Func<NoteModel, NoteModel> mutate)
    {
        var layout = widgetLayoutProvider.Get();
        var model = layout.GetModel<NoteModel>() ?? new NoteModel(Locale.Notes_Title);
        layout = layout with { Settings = JsonSerializer.SerializeToElement(mutate(model)) };
        widgetLayoutProvider.Save(layout);
    }

    // ---------- style presets ----------

    private async void ImportPreset(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Locale.Notes_Style_Preset,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(Locale.Notes_Preset_Filter) { Patterns = ["*.json"] } },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;

        MarkdownTypography? style = null;
        try
        {
            style = StylePreset.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // surfaced through the status line below
        }

        if (style == null)
        {
            PresetStatus.Text = Locale.Notes_Preset_Failed;
            return;
        }

        UpdateModel(m => m with { MarkdownStyle = style });
        StyleToggle.IsChecked = true;
        StyleSection.IsVisible = true;
        var fontIndex = style.Font == null ? 0 : fontNames.IndexOf(style.Font) + 1;
        FontBox.SelectedIndex = fontIndex > 0 ? fontIndex : 0;
        SyncColorRows();
        PresetStatus.Text = Locale.Notes_Preset_Imported;
    }

    private async void ExportPreset(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Locale.Notes_Style_Preset,
            SuggestedFileName = "uwidgets-note-style.json",
            FileTypeChoices = new[] { new FilePickerFileType(Locale.Notes_Preset_Filter) { Patterns = ["*.json"] } },
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;

        try
        {
            File.WriteAllText(path, StylePreset.Serialize(CurrentModel.MarkdownStyle ?? new MarkdownTypography()));
            PresetStatus.Text = Locale.Notes_Preset_Exported;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PresetStatus.Text = Locale.Notes_Preset_Failed;
        }
    }
}
