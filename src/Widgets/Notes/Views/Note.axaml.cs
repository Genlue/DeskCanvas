using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Notes.Locales;
using Notes.Models;
using Notes.Services;
using Notes.ViewModels;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Views.Controls;
using DeskCanvas.Views;

namespace Notes.Views;

/// <summary>
/// The note widget. Renders its body as markdown (display mode) and enters edit
/// mode on a double-click; the body can come from the widget's own text, one
/// markdown file, or the (most recent / picked) .md files of a folder, in which
/// case the documents are stacked with titles and separators (macOS-notes-like).
/// The colored top bar (title) follows the app accent or a custom color, with a
/// per-widget opacity.
/// </summary>
public partial class Note : UserControl, IWidgetSelfRefreshing
{
    /// <summary>Card height (DIP) at/below which the header turns compact and the footer hides.</summary>
    private const double CompactHeight = 100;

    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly NoteViewModel viewModel;

    private FileSystemWatcher? watcher;
    private DispatcherTimer? reloadTimer;
    private bool editingInternal;
    private string? editingPath;
    private bool wasCompact;

    public Note(IAppSettingsProvider appSettingsProvider, IWidgetLayoutProvider widgetLayoutProvider)
        : this(appSettingsProvider, widgetLayoutProvider, new NoteModel(Locale.Notes_Title)) {}

    public Note(IAppSettingsProvider appSettingsProvider, IWidgetLayoutProvider widgetLayoutProvider, NoteModel model)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.widgetLayoutProvider = widgetLayoutProvider;
        viewModel = new NoteViewModel(model);
        DataContext = viewModel;

        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
        appSettingsProvider.DataChanged += OnAppSettingsChanged;

        // Light/dark switch: re-render the markdown with the palette of the
        // now-active theme (colors are chosen at render time).
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged += OnThemeVariantChanged;

        // The rendered markdown is a hit-test surface: single clicks pass through
        // to the widget (dragging), double clicks enter edit mode (mirroring the
        // ClickThroughTextBox activation gesture).
        RenderScroll.AddHandler(PointerPressedEvent, OnRenderedPressed, RoutingStrategies.Tunnel);

        Rebuild();
    }

    private NoteModel Model => viewModel.Model;

    /// <inheritdoc />
    public void Refresh(WidgetLayout layout)
    {
        var newModel = layout.GetModel<NoteModel>();
        if (newModel == null) return;

        // A source switch (settings dialog) always ends the current edit session.
        if (newModel.Source != viewModel.Model.Source || newModel.Path != viewModel.Model.Path)
        {
            editingInternal = false;
            editingPath = null;
        }

        viewModel.Update(newModel);
        Rebuild();
    }

    // ---------- rebuild ----------

    private void Rebuild()
    {
        var model = Model;
        var layoutH = widgetLayoutProvider?.Get()?.Height ?? (Bounds.Height > 0 ? Bounds.Height : 180);
        var currentH = Bounds.Height > 0 ? Bounds.Height : layoutH;
        var compact = currentH <= CompactHeight;
        ApplyHeader(model);

        TitleBox.Height = 44;
        TitleBox.FontSize = 16;
        // 顶栏 mode (widget setting): the full bar with its editable title, the bare bar
        // (expand button only), or no bar at all — bar and the divider below it collapse
        // and the body owns the whole card.
        var header = model.EffectiveHeaderMode;
        HeaderBorder.IsVisible = header != NoteHeaderMode.Hidden;
        Divider.IsVisible = header != NoteHeaderMode.Hidden;
        TitleBox.IsVisible = header == NoteHeaderMode.BarAndTitle;
        // The bare bar keeps the factory height: the title box (44 px) defines it when visible.
        HeaderBorder.MinHeight = header == NoteHeaderMode.BarOnly ? 44 : 0;
        ContentBox.IsVisible = false;
        RenderScroll.IsVisible = false;
        FileScroll.IsVisible = false;
        TitleBox.IsHitTestVisible = true;
        // Widget-settings switch: double-click on the card edits inline only when
        // explicitly allowed; the header's expand button always opens the panel.
        ClickThroughTextBox.SetAllowEdit(TitleBox, Model.AllowInlineEdit);

        var padding = Math.Clamp(model.BodyPadding, 0, 64);
        RenderScroll.Padding = new Thickness(padding, 0);
        FilePanel.Margin = new Thickness(padding, 2);
        ContentBox.Padding = new Thickness(padding, 0);

        switch (model.Source)
        {
            case NoteSource.File:
            case NoteSource.Folder:
                BuildDocuments(model, compact);
                break;
            default:
                BuildInternal(model, compact);
                break;
        }

        StartWatching(model);
    }

    private void BuildInternal(NoteModel model, bool compact)
    {
        TitleBox.Text = model.Title;
        UpdatedText.IsVisible = !compact;
        UpdatedText.Text = model.Updated?.ToString("g", CultureInfo.CurrentUICulture);

        if (!model.Markdown)
        {
            ContentBox.IsVisible = true;
            ContentBox.Text = model.Content ?? "";
            return;
        }

        if (editingInternal)
        {
            ContentBox.IsVisible = true;
            ContentBox.Text = model.Content ?? "";
            ActivateEditor(ContentBox);
            return;
        }

        RenderedHost.Content = MarkdownRenderer.Render(this, model.Content, BodyFontSize(compact), model.MarkdownStyle);
        RenderScroll.IsVisible = true;
    }

    private void BuildDocuments(NoteModel model, bool compact)
    {
        UpdatedText.IsVisible = false;
        FileScroll.IsVisible = true;
        FilePanel.Children.Clear();

        var paths = NoteFiles.Resolve(model);

        if (model.Source == NoteSource.File)
        {
            // The card header shows the document title; the file name stays on disk.
            TitleBox.IsHitTestVisible = false;
            TitleBox.Text = paths.Count > 0
                ? NoteFiles.TitleOf(paths[0], NoteFiles.Read(paths[0]))
                : Locale.Notes_Empty;
        }
        else
        {
            TitleBox.Text = model.Title;
        }

        if (paths.Count == 0)
        {
            FilePanel.Children.Add(new TextBlock
            {
                Text = Locale.Notes_Empty,
                Opacity = 0.5,
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }

        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var content = NoteFiles.Read(path);

            // Separate the documents from each other (macOS-style stacked notes).
            if (index > 0 && model.Source == NoteSource.Folder)
                FilePanel.Children.Add(BuildDocumentRule());

            if (model.Source == NoteSource.Folder && !compact)
                FilePanel.Children.Add(new TextBlock
                {
                    Text = NoteFiles.TitleOf(path, content),
                    FontWeight = FontWeight.Bold,
                    FontSize = 16,
                    Margin = new Thickness(0, index == 0 ? 0 : 6, 0, 4),
                });

            FilePanel.Children.Add(BuildDocumentBody(path, content, model, compact));
        }
    }

    private Control BuildDocumentBody(string path, string? content, NoteModel model, bool compact)
    {
        if (editingPath == path)
        {
            var editor = new ClickThroughTextBox
            {
                Text = content ?? "",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 0,
                FontSize = BodyFontSize(compact),
            };
            editor.LostFocus += (_, _) =>
            {
                if (editingPath != path) return;
                NoteFiles.Write(path, editor.Text);
                editingPath = null;
                Rebuild();
            };
            ActivateEditor(editor);
            return editor;
        }

        if (!model.Markdown)
        {
            // Plain-source documents keep the historic click-to-edit box.
            var box = new ClickThroughTextBox
            {
                Text = content ?? "",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 0,
                FontSize = BodyFontSize(compact),
            };
            ClickThroughTextBox.SetAllowEdit(box, model.AllowInlineEdit);
            box.LostFocus += (_, _) => NoteFiles.Write(path, box.Text);
            return box;
        }

        var rendered = MarkdownRenderer.Render(this, NoteFiles.StripTitleLine(content), BodyFontSize(compact), model.MarkdownStyle);
        var host = new Border { Background = Brushes.Transparent, Child = rendered };
        host.AddHandler(PointerPressedEvent, (object? sender, PointerPressedEventArgs e) =>
        {
            var begin = new Action(() =>
            {
                editingPath = path;
                Rebuild();
            });
            HandleThrough(e, begin);
        }, RoutingStrategies.Tunnel);
        return host;
    }

    // ---------- header ----------

    /// <summary>
    /// Title bar brush: the app accent (live) or the custom color, with the
    /// configured opacity (both modes).
    /// In Colorful (macOS 18 widget) mode:
    /// - Opacity is strictly locked to 1.0 (100%), unaffected by widget settings.
    /// - Color is strictly unified to Apple Notes signature Yellow (#FFCC00 in Light, #FFD60A in Dark).
    /// </summary>
    private void ApplyHeader(NoteModel model)
    {
        var theme = appSettingsProvider.Get().Theme;
        var opacity = theme.IsColorful ? 1.0 : Math.Clamp(model.HeaderOpacity, 0, 1);

        Color color;
        if (theme.IsColorful)
        {
            // 多彩模式强制顶栏 100% 不透明，且严格统一为 Apple Notes 经典黄色
            var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            color = isDark ? Color.Parse("#FFD60A") : Color.Parse("#FFCC00");
        }
        else if (model.FollowAccentHeader)
        {
            color = ResolveAccent();
        }
        else
        {
            color = Color.TryParse(model.HeaderColor, out var custom) ? custom : Color.Parse("#3376CD");
        }

        HeaderBorder.Background = new SolidColorBrush(color, opacity);
    }

    private Color ResolveAccent()
    {
        var theme = appSettingsProvider.Get().Theme;
        if (theme.AccentColor != null && Color.TryParse(theme.AccentColor, out var accent)) return accent;
        if (this.TryFindResource("SystemAccentColor", out var accentResource) && accentResource is Color system) return system;
        return Color.Parse("#0078D4");
    }

    // ---------- interaction ----------

    private void OnRenderedPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!RenderScroll.IsVisible) return;
        HandleThrough(e, () =>
        {
            editingInternal = true;
            Rebuild();
        });
    }

    private void HandleThrough(PointerPressedEventArgs e, Action beginEdit)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            // 默认（一级界面编辑关闭）双击打开二级面板；在组件设置里允许
            // "双击编辑"后才是就地进入编辑模式。
            if (Model.AllowInlineEdit)
                beginEdit();
            else
                OpenPopup();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 1)
        {
            // Pass the press through so the widget can still be dragged.
            (VisualRoot as Widget)?.OnPointerPressed(this, e);
            e.Handled = true;
        }
    }

    // ---------- secondary panel ----------

    public void OpenPopupClick(object? sender, RoutedEventArgs e) => OpenPopup();

    /// <summary>Open the secondary panel: a full-size editor for the note
    /// (internal / single-file source) or a read-only preview (folder source).</summary>
    public void OpenPopup()
    {
        var (screenCenter, _) = GetScreenCenterAndTopLevel();
        var owner = VisualRoot as Window;
        NotePopupWindow.ShowPopup(Model, screenCenter, owner, OnPanelModelChanged, SpawnCornerRadius);
    }

    /// <summary>
    /// The host widget's visual corner radius, so the popup's corners and its glass match
    /// the widget card exactly. Null keeps the popup's own default radius.
    /// </summary>
    private double? SpawnCornerRadius
    {
        get
        {
            var r = (VisualRoot as Widget)?.Radius.TopLeft ?? 0;
            return r > 0 ? r : null;
        }
    }

    private (Point? ScreenCenter, TopLevel? TopLevel) GetScreenCenterAndTopLevel()
    {
        if (VisualRoot is Visual rootVisual && VisualRoot is TopLevel topLevel)
        {
            var bounds = Bounds;
            var centerLocal = new Point(bounds.Width / 2, bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, rootVisual);
            if (rootPoint.HasValue)
            {
                var screenPoint = topLevel.PointToScreen(rootPoint.Value);
                return (new Point(screenPoint.X, screenPoint.Y), topLevel);
            }
        }
        return (null, null);
    }

    private void OnPanelModelChanged(NoteModel newModel)
    {
        if (newModel.Source == NoteSource.Internal)
            UpdateModel(newModel);   // persists into the widget settings and refreshes the card
        else
            Rebuild();               // file/folder: the documents changed on disk
    }

    private static void ActivateEditor(TextBox editor) =>
        Dispatcher.UIThread.Post(() =>
        {
            editor.Focusable = true;
            editor.Focus();
            editor.CaretIndex = editor.Text?.Length ?? 0;
        });

    private static double BodyFontSize(bool compact) => compact ? 12 : 14;

    private IBrush ResolveBaseHighBrush()
    {
        // Explicit active variant: an ambient lookup can freeze on the light
        // theme dictionary of the app's style overrides (dark-mode divider
        // drawn in the light accent shade).
        var app = Application.Current;
        if (app is not null &&
            ((IResourceHost)app).TryGetResource("SystemControlForegroundBaseHighBrush", app.ActualThemeVariant, out var brush) &&
            brush is IBrush baseHigh)
            return baseHigh;
        return new SolidColorBrush(Color.Parse("#808080"));
    }

    private Control BuildDocumentRule() => new Border
    {
        Height = 18,
        Child = new Avalonia.Controls.Shapes.Path
        {
            Height = 1,
            Stretch = Stretch.Fill,
            Opacity = 0.2,
            StrokeThickness = 1.5,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 1, 1 },
            StrokeDashOffset = 1,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Stroke = ResolveBaseHighBrush(),
            Data = new LineGeometry(new Point(0, 0), new Point(1, 0)),
        },
    };

    // ---------- persistence ----------

    private void UpdateContent(object? sender, RoutedEventArgs e)
    {
        if (Model.Source != NoteSource.Internal) return;
        var newText = (sender as TextBox)!.Text;
        editingInternal = false;
        UpdateModel(Model with { Content = newText, Updated = DateTime.Now });
    }

    private void UpdateTitle(object? sender, RoutedEventArgs e)
    {
        if (Model.Source == NoteSource.File) return;
        var newText = (sender as TextBox)!.Text;
        UpdateModel(Model with { Title = newText, Updated = DateTime.Now });
    }

    private void UpdateModel(NoteModel newModel)
    {
        viewModel.Update(newModel);
        var currentLayout = widgetLayoutProvider?.Get();
        // The null test above is redundant for the compiler (widgetLayoutProvider is a
        // non-nullable readonly field), but the dereference is guaranteed safe either way.
        if (currentLayout != null)
        {
            var newSettings = JsonSerializer.SerializeToElement(newModel);
            var newLayout = currentLayout with { Settings = newSettings };
            widgetLayoutProvider!.Save(newLayout);
        }
    }

    // ---------- file watching ----------

    private void StartWatching(NoteModel model)
    {
        watcher?.Dispose();
        watcher = null;

        var directory = model.Source switch
        {
            NoteSource.File => Path.GetDirectoryName(model.Path),
            NoteSource.Folder when model.Path != null && Directory.Exists(model.Path) => model.Path,
            _ => null,
        };
        if (directory == null || !Directory.Exists(directory)) return;

        try
        {
            watcher = new FileSystemWatcher(directory, "*.md")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            watcher.Changed += OnExternalChange;
            watcher.Created += OnExternalChange;
            watcher.Deleted += OnExternalChange;
            watcher.Renamed += OnExternalChange;
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher = null;
        }
    }

    private void OnExternalChange(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher raises on a pool thread; the DispatcherTimer must be
        // created and started on the UI thread or its tick never fires.
        Dispatcher.UIThread.Post(() =>
        {
            reloadTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            reloadTimer.Stop();
            reloadTimer.Tick -= OnReloadTick;
            reloadTimer.Tick += OnReloadTick;
            reloadTimer.Start();
        });
    }

    private void OnReloadTick(object? sender, EventArgs e)
    {
        reloadTimer?.Stop();
        // Never rebuild under an open editor (it would drop unsaved text).
        if (editingInternal || editingPath != null) return;
        Rebuild();
    }

    // ---------- lifecycle ----------

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Height <= CompactHeight;
        if (compact == wasCompact) return;
        wasCompact = compact;
        Rebuild();
    }

    private void OnAppSettingsChanged(object? sender, AppSettings? oldSettings, AppSettings newSettings)
    {
        // The header color (accent/custom) and the markdown palette choice both
        // depend on the theme settings — re-apply and re-render.
        ApplyHeader(Model);
        OnThemeVariantChanged(sender, EventArgs.Empty);
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        // Rebuild with the palette of the now-active theme. An open editor is
        // never disturbed (it re-renders on its own when the session ends).
        if (editingInternal || editingPath != null) return;
        Rebuild();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        Unloaded -= OnUnloaded;
        appSettingsProvider.DataChanged -= OnAppSettingsChanged;
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged -= OnThemeVariantChanged;
        RenderScroll.RemoveHandler(PointerPressedEvent, OnRenderedPressed);
        reloadTimer?.Stop();
        if (reloadTimer != null) reloadTimer.Tick -= OnReloadTick;
        watcher?.Dispose();
        watcher = null;
    }
}
