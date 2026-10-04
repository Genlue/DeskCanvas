using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Calendar.Models;
using Calendar.ViewModels;
using DeskCanvas.Services;

namespace Calendar.Views;

public partial class Month : UserControl
{
    private readonly MonthCalendarModel monthCalendarModel;

    private readonly MonthCalendarViewModel viewModel;

    public Month() : this(new MonthCalendarModel(DayOfWeek.Monday)) {}
    
    public Month(MonthCalendarModel monthCalendarModel)
    {
        this.monthCalendarModel = monthCalendarModel;
        viewModel = new MonthCalendarViewModel(monthCalendarModel);
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        InitializeComponent();
        HollowToday = monthCalendarModel.HollowTodayNumber;
        UpdateTodayBrush();
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    /// <summary>
    /// The view can be detached and re-added later (the settings window caches pages and the
    /// Gallery keeps a live preview control), so the five-minute tick and the
    /// process-lifetime theme event released by <see cref="OnUnloaded"/> are re-attached here.
    /// The view model is only stopped on unload — never disposed — so it can be reused.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        viewModel.Start();

        // The accent resource only resolves once the control is attached to a themed tree.
        UpdateTodayBrush();

        if (Application.Current != null)
        {
            Application.Current.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
            Application.Current.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        }

        // The accent is an application resource, so a colour picked in 外观 replaces it
        // in place. Without this the marker keeps the colour it was created with while
        // every other accent-tinted element follows the new one.
        if (Application.Current is IResourceHost host)
        {
            accentHost = host;
            host.ResourcesChanged -= OnAccentResourcesChanged;
            host.ResourcesChanged += OnAccentResourcesChanged;
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        viewModel.Stop();

        // Application.Current is process-lifetime: leaving the handler attached would keep
        // every abandoned Month view (and its view model) alive forever.
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged -= OnActualThemeVariantChanged;

        if (accentHost != null)
            accentHost.ResourcesChanged -= OnAccentResourcesChanged;
    }

    /// <summary>The process-lifetime resource host the accent subscription belongs to.</summary>
    private IResourceHost? accentHost;

    private void OnActualThemeVariantChanged(object? sender, System.EventArgs e) => UpdateTodayBrush();

    private void OnAccentResourcesChanged(object? sender, System.EventArgs e) => UpdateTodayBrush();

    /// <summary>
    /// True when today's number is punched out of the marker disc (镂空) instead of being painted
    /// on top of it. Drives the marker control and hides the day text block for today.
    /// </summary>
    public static readonly StyledProperty<bool> HollowTodayProperty =
        AvaloniaProperty.Register<Month, bool>(nameof(HollowToday), true);

    public bool HollowToday
    {
        get => GetValue(HollowTodayProperty);
        set => SetValue(HollowTodayProperty, value);
    }

    /// <summary>
    /// Color the today marker is filled with, resolved for the current theme: the custom color
    /// when the model asks for one, otherwise the theme accent.
    /// </summary>
    public static readonly StyledProperty<IBrush?> TodayDotBrushProperty =
        AvaloniaProperty.Register<Month, IBrush?>(nameof(TodayDotBrush));

    public IBrush? TodayDotBrush
    {
        get => GetValue(TodayDotBrushProperty);
        set => SetValue(TodayDotBrushProperty, value);
    }

    /// <summary>
    /// True when the today-marker should use a fixed color instead of the
    /// (theme-reactive) accent resource.
    /// </summary>
    public static readonly StyledProperty<bool> IsCustomTodayColorProperty =
        AvaloniaProperty.Register<Month, bool>(nameof(IsCustomTodayColor));

    public bool IsCustomTodayColor
    {
        get => GetValue(IsCustomTodayColorProperty);
        set => SetValue(IsCustomTodayColorProperty, value);
    }

    /// <summary>
    /// The today-marker color for custom mode, resolved against the current theme.
    /// </summary>
    public static readonly StyledProperty<IBrush?> TodayCustomBrushProperty =
        AvaloniaProperty.Register<Month, IBrush?>(nameof(TodayCustomBrush));

    public IBrush? TodayCustomBrush
    {
        get => GetValue(TodayCustomBrushProperty);
        set => SetValue(TodayCustomBrushProperty, value);
    }

    private void UpdateTodayBrush()
    {
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        if (monthCalendarModel.TodayColorMode != "Custom")
        {
            IsCustomTodayColor = false;
            TodayCustomBrush = null;
            SetTodayDotBrush(ResolveAccentBrush());
            return;
        }

        var hex = dark ? monthCalendarModel.TodayColorDark : monthCalendarModel.TodayColorLight;

        IBrush? brush = null;
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { brush = new SolidColorBrush(Color.Parse(hex)); }
            catch { brush = null; }
        }

        IsCustomTodayColor = brush != null;
        TodayCustomBrush = brush;

        // The custom color is per theme (light/dark are tuned independently); when the current
        // theme has none configured the marker keeps following the accent instead of vanishing.
        SetTodayDotBrush(brush ?? ResolveAccentBrush());
    }

    /// <summary>
    /// Swaps the marker brush only when the colour really changed. The app accent resource
    /// is replaced on every theme re-apply, and a blind assignment would re-render every
    /// live month cell (and allocate a brush per cell) for an unchanged colour.
    /// </summary>
    private void SetTodayDotBrush(IBrush? brush)
    {
        if (TodayDotBrush is ISolidColorBrush current && brush is ISolidColorBrush next)
        {
            if (current.Color == next.Color) return;
        }
        else if (TodayDotBrush == null && brush == null)
        {
            return;
        }

        TodayDotBrush = brush;
    }

    /// <summary>The theme accent / calendar today brush, or null while the resource is not resolvable yet.</summary>
    private IBrush? ResolveAccentBrush()
    {
        // Variant-explicit lookup: the variant-less overload resolves the LIGHT theme
        // dictionary even while the widget renders dark — in 单色 modes that turned the
        // today dot into the dark-mode background color (the same trap
        // AggregateViewModel.ResolveAccentBrush and MarkdownRenderer.ResolveThemed
        // document). SystemAccentColor is a plain application resource without theme
        // dictionaries, so the plain overload stays safe for it.
        if (this.TryFindResource("CalendarTodayBrush", this.ActualThemeVariant, out var calBrush) && calBrush is IBrush cb)
            return cb;
        if (this.TryFindResource("SystemAccentColor", out var value) && value is Color accent)
            return new SolidColorBrush(accent);
        return null;
    }

    public static readonly StyledProperty<double> TextSizeProperty = 
        AvaloniaProperty.Register<Month, double>(nameof(TextSize), 12);

    public double TextSize
    {
        get => GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }
    
    public static readonly StyledProperty<Thickness> MonthMarginProperty = 
        AvaloniaProperty.Register<Month, Thickness>(nameof(MonthMargin));
    
    public Thickness MonthMargin
    {
        get => GetValue(MonthMarginProperty);
        set => SetValue(MonthMarginProperty, value);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize;
        var tier = SizeTiers.ResolveTier(this, size);

        // 1×1 (Cell): a whole month is illegible — show today only.
        // A short wide custom card (2×1 …): only one week fits the height — show
        // the current week as a single row. Everything else (2×2 / 4×2 / 4×4 and
        // big customs): the full month grid, which scales with TextSize.
        var cell = tier == WidgetTier.Cell;
        var weekRow = !cell && size.Height <= 160 && size.AspectRatio >= 1.5;

        FullMonth.IsVisible = !cell && !weekRow;
        WeekRow.IsVisible = weekRow;
        TodayOnly.IsVisible = cell;

        if (cell) return;

        if (weekRow)
        {
            // Weekday header row: the day cells scale themselves (Viewboxes);
            // the header shares the row height, so size it against half the card.
            TextSize = size.Height * 0.22;
            MonthMargin = new Thickness();
            return;
        }

        var fontSize = Math.Min(size.Width, size.Height) / 12;
        var margin = Math.Min(size.Width, size.Height) / 30;

        TextSize = fontSize;
        MonthMargin = new Thickness(-margin, 0,  0,  0);
    }
}
