using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using Timetable.Locales;
using Timetable.Models;
using Timetable.Services;

namespace Timetable.Views;

/// <summary>
/// The timetable card. Renders one of four layouts depending on the widget's grid span —
/// 1×1 next lesson, 2×2 today list, 4×2 compact week grid, 4×4 full week grid — and keeps
/// the time-dependent parts (current lesson ring, today highlight, countdown texts) fresh
/// from the shared one-minute timer.
/// <para>
/// Memory contract (matching the repository's widget audit): the view keeps references only
/// to the controls it builds (bounded by the import limits), never caches bitmaps, subscribes
/// exactly one shared timer <b>and</b> the store event symmetrically in <see cref="OnLoaded"/>
/// and releases both in <see cref="OnUnloaded"/> (so an instance detached before its first
/// load is never rooted by a process-lifetime static), and implements
/// <see cref="IWidgetSelfRefreshing"/> so settings changes rebind in place
/// instead of recreating the whole view.
/// </para>
/// <para>
/// Session gating (P8): with a host-injected <see cref="IWidgetSession"/> the shared timer
/// subscription additionally follows the session's <see cref="ActivitySnapshot.RenderActive"/>
/// (the view implements <see cref="IWidgetActivityConsumer"/> and reconciles
/// <c>attached AND renderActive</c>). Without a session the historic always-on behaviour
/// holds — that host never pushes activity either.
/// </para>
/// </summary>
public partial class Timetable : UserControl, IWidgetSelfRefreshing, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "TimetableCardBackground";

    private readonly Action liveTick;
    private readonly IWidgetSession? session;

    private TimetableModel model;
    private TimetableRuntime runtime;

    private bool attached;
    private bool renderActive = true;
    private int tickSubscriptions;
    private WidgetTier tier = WidgetTier.Small;
    private double builtWidth = -1;
    private double builtHeight = -1;
    private int builtDayKey = -1;
    private int viewingWeek = -1;

    private readonly List<BlockHandle> weekBlocks = [];
    private readonly List<(TextBlock Label, int Period)> periodLabels = [];
    private readonly List<RowHandle> todayRows = [];
    private int todayListDay = -1;
    private bool todayListIsToday;

    /// <summary>
    /// Reusable lane-background cells for the week grid (up to MaxPeriods × 7 — a fixed,
    /// small ceiling). Each rebuild used to allocate that many fresh Borders only to show
    /// the subtle slot background; the pool holds them across rebuilds so a rebuild
    /// re-positions instead of re-allocating.
    /// </summary>
    private readonly List<Border> laneCellPool = [];

    /// <summary>
    /// Pooled week-grid chrome and blocks. Every rebuild used to Clear the host and build a
    /// fresh tree (headers, period labels, lane cells, course blocks); the discarded controls
    /// release their native side (compositor visuals, shaped text) lazily, so a resize drag
    /// or week flip retained tens of MB that GC could not attribute or reclaim. The pools
    /// cap the tree at its steady-state size: a rebuild rebinds texts, sizes and positions
    /// in place instead of allocating. Surplus entries stay detached after the host Clear.
    /// </summary>
    private readonly List<Border> headerPool = [];
    private readonly List<TextBlock> periodLabelPool = [];
    private readonly List<Border> blockPool = [];
    private readonly List<Border> todayRowPool = [];

    /// <summary>One parallel-lane group: its Grid stays attached to the host, its blocks stay attached to it.</summary>
    private sealed class LaneContainer
    {
        public required Grid Grid { get; init; }
        public List<Border> Blocks { get; } = [];
    }

    private readonly List<LaneContainer> laneContainerPool = [];

    private IBrush? accentBrush;
    private Color accentColor;

    private sealed class BlockHandle
    {
        public required Border Border { get; init; }
        public TimeOnly? Start { get; init; }
        public TimeOnly? End { get; init; }
        public int Column { get; init; }
    }

    private sealed class RowHandle
    {
        public required Border Row { get; init; }
        public TimeOnly? Start { get; init; }
        public TimeOnly? End { get; init; }
    }

    public Timetable() : this(null)
    {
    }

    /// <summary>
    /// The <paramref name="model"/> parameter exists only for the host activation contract
    /// (<c>WidgetFactory.CreateWidgetControl</c> passes the legacy per-widget Settings copy or a
    /// default instance). It is deliberately <b>ignored</b>: the unified store
    /// (<c>timetable.json</c>) is the single source of truth. Preferring the layout copy here is
    /// what let a stale Settings snapshot mask — or get masked by — the store after an update,
    /// which surfaced as "时间设置还在，课程全丢".
    /// </summary>
    public Timetable(TimetableModel? model, IWidgetSession? session = null)
    {
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        // Get() already returns a normalized model on every path into the cache
        // (Save / LoadFromFile / migration / test reset all Normalize) — a second
        // Normalize here only deep-copied the whole model per widget construction.
        this.model = TimetableStore.Get();
        runtime = TimetableRuntime.Build(this.model);
        liveTick = OnLiveTick;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ToolTip.SetTip(this, Locale.Timetable_OpenPanelHint);

        // No static-event subscription here (the store event is subscribed in OnLoaded):
        // an instance removed before ever loading — a Gallery thumbnail replaced mid-show,
        // a preview detached before layout — would otherwise stay rooted by the store's
        // process-lifetime event forever, and every save would keep rebuilding an
        // invisible full grid on the zombie.

        RebuildAll();
    }

    /// <summary>Check seam: the live subscription count over the shared minute timer (0 or 1).</summary>
    public int TickSubscriptionCountForChecks => tickSubscriptions;

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: the latest activity snapshot's render flag as reconciled.</summary>
    public bool RenderActiveForChecks => renderActive;

    /// <summary>The host's activity push: reconcile the timer subscription with the snapshot.</summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        renderActive = activity.RenderActive;
        UpdateTickSubscription();
    }

    // ---------- Lifecycle ----------

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        attached = true;
        UpdateTickSubscription();
        TimetableStore.ModelChanged -= OnStoreModelChanged;
        TimetableStore.ModelChanged += OnStoreModelChanged;
        UpdateLiveState();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        attached = false;
        UpdateTickSubscription();
        TimetableStore.ModelChanged -= OnStoreModelChanged;
    }

    /// <summary>
    /// Single subscription state machine: the shared minute-timer subscription exists exactly
    /// while the control is loaded AND the session renders. Overlapping transitions (a covered
    /// screen while unloaded — the settings window caches pages, the Gallery keeps live
    /// previews) cannot double-subscribe or double-unsubscribe — the subscription is
    /// reconciled from both inputs on every change, exactly like the clock/calendar view
    /// models' gate.
    /// </summary>
    private void UpdateTickSubscription()
    {
        var wanted = attached && renderActive;
        if (wanted && tickSubscriptions == 0)
        {
            tickSubscriptions++;
            TimerService.Timer1Minute.Subscribe(liveTick);
            UpdateLiveState();
        }
        else if (!wanted && tickSubscriptions > 0)
        {
            tickSubscriptions--;
            TimerService.Timer1Minute.Unsubscribe(liveTick);
        }
    }

    private void OnStoreModelChanged(TimetableModel newModel, object? sender)
    {
        if (ReferenceEquals(sender, this)) return;
        model = newModel;
        runtime = TimetableRuntime.Build(model);
        RebuildAll();
    }

    private void OnLiveTick()
    {
        if (!attached || !renderActive) return;
        UpdateLiveState();
    }

    /// <inheritdoc />
    public void Refresh(WidgetLayout layout)
    {
        model = TimetableStore.Get();
        runtime = TimetableRuntime.Build(model);
        RebuildAll();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize;
        var newTier = ResolveTier(size);
        var tierChanged = newTier != tier;
        tier = newTier;

        var bucketChanged = Math.Abs(size.Width - builtWidth) > 5 || Math.Abs(size.Height - builtHeight) > 5;
        if (tierChanged || bucketChanged || builtWidth < 0)
        {
            // Rebuild immediately — deliberately NOT debounced. A deferred rebuild leaves the
            // old tree rendering at the new size for the debounce window: the fixed-pixel row
            // metrics and old font sizes meet the new column widths, so text visibly re-wraps
            // and then snaps — reported as 抖动/闪烁. A same-frame rebuild never paints the
            // intermediate state. The allocation cost that motivated deferring is already gone
            // (pooled lane cells + shared immutable brushes + the startup-rebuild fixes).
            builtWidth = size.Width;
            builtHeight = size.Height;
            RebuildAll();
        }
    }

    private WidgetTier ResolveTier(Size size)
    {
        var resolved = SizeTiers.ResolveTier(this, size);
        if (resolved != WidgetTier.Other) return resolved;

        // Custom spans outside the presets: classify by shape so every size gets a usable layout.
        if (size.Width <= 130 && size.Height <= 130) return WidgetTier.Cell;
        if (size.Width >= size.Height * 1.35) return WidgetTier.Medium;
        return WidgetTier.Small;
    }

    // ---------- Build ----------

    private void RebuildAll()
    {
        // The tree now reflects "today" as of this build: stamp the key so the first
        // UpdateLiveState after a construction-time build does not rebuild everything
        // again (the build itself is the update).
        builtDayKey = DayIndex(DateTime.Now.DayOfWeek);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            if (builtWidth > 0) { width = builtWidth; height = builtHeight; }
            else { width = 200; height = 200; }
        }
        builtWidth = width;
        builtHeight = height;

        CellLayout.IsVisible = false;
        TodayLayout.IsVisible = false;
        WeekLayout.IsVisible = false;
        OpenPanelButton.IsVisible = model.HasData;

        if (!model.HasData)
        {
            EmptyHint.IsVisible = true;
            EmptyHint.Text = Locale.Timetable_NoData;
            EmptyHint.FontSize = TextMetrics.Quantize(Math.Clamp(Math.Min(width, height) * 0.11, 10, 15));
            UpdateLiveState();
            return;
        }

        EmptyHint.IsVisible = false;

        switch (tier)
        {
            case WidgetTier.Cell:
                CellLayout.IsVisible = true;
                ApplyCellFonts(width, height);
                break;
            case WidgetTier.Medium:
            case WidgetTier.Large:
                WeekLayout.IsVisible = true;
                BuildWeekGrid(width, height, compact: tier == WidgetTier.Medium);
                break;
            default:
                TodayLayout.IsVisible = true;
                BuildTodayList(width, height);
                break;
        }

        UpdateLiveState();
    }

    private void ApplyCellFonts(double width, double height)
    {
        var min = Math.Min(width, height);
        CellTag.FontSize = TextMetrics.Quantize(Math.Clamp(min * 0.13, 10, 14));
        CellTitle.FontSize = TextMetrics.Quantize(Math.Clamp(min * 0.19, 12.5, 22));
        CellSub.FontSize = TextMetrics.Quantize(Math.Clamp(min * 0.12, 9.5, 13));
        CellTitle.MaxWidth = Math.Max(40, width - 14);
        CellSub.MaxWidth = Math.Max(40, width - 14);
    }

    private void BuildTodayList(double width, double height)
    {
        todayRows.Clear();

        var now = DateTime.Now;
        var week = TimetableClock.ComputeWeek(model, now);
        var today = DayIndex(now.DayOfWeek);

        var targetDay = today;
        var isToday = true;
        for (var offset = 0; offset <= 6; offset++)
        {
            var day = ((today - 1 + offset) % 7) + 1;
            if (ActiveSlotsForDay(day, week).Count == 0) continue;
            targetDay = day;
            isToday = offset == 0;
            break;
        }

        todayListDay = targetDay;
        todayListIsToday = isToday;

        var prefix = isToday ? Locale.Timetable_Today : Locale.Timetable_Tomorrow;
        TodayHeader.Text = isToday || targetDay == ((today) % 7) + 1
            ? $"{prefix} · {TimetableClock.DayName(targetDay)}"
            : TimetableClock.DayName(targetDay);
        TodayHeader.FontSize = TextMetrics.Quantize(Math.Clamp(Math.Min(width, height) * 0.10, 10.5, 14));

        var slots = ActiveSlotsForDay(targetDay, week);
        if (slots.Count == 0)
        {
            HidePoolSurplus(todayRowPool, 0);
            TodayListPanel.Children.Add(new TextBlock
            {
                Text = Locale.Timetable_NoClassToday,
                Opacity = 0.72,
                FontSize = TextMetrics.Quantize(Math.Clamp(Math.Min(width, height) * 0.10, 10.5, 14)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 2, 0),
            });
            return;
        }

        // Readability first: one comfortable colour card per lesson; the list scrolls when it
        // does not fit instead of shrinking the type to cram every lesson into the card.
        var titleFont = TextMetrics.Quantize(Math.Clamp(width * 0.088, 11.5, 15));
        var subFont = TextMetrics.Quantize(Math.Clamp(titleFont * 0.8, 9.5, 12));

        var rowIndex = 0;
        foreach (var slot in slots)
        {
            var row = TakeAttachedFromPool(todayRowPool, rowIndex++, TodayListPanel, static () => new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 6, 9, 6),
                Child = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock
                        {
                            FontWeight = FontWeight.SemiBold,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock
                        {
                            Opacity = 0.9,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
            });
            RebindTodayRowInPlace(row, slot, titleFont, subFont);
            row.IsVisible = true;
        }
        HidePoolSurplus(todayRowPool, rowIndex);
    }

    /// <summary>Rebind a pooled today-list row in place (same contract as the week grid's blocks).</summary>
    private void RebindTodayRowInPlace(Border row, TimetableRuntime.Slot slot, double titleFont, double subFont)
    {
        var panel = (StackPanel)row.Child!;
        var title = (TextBlock)panel.Children[0];
        var sub = (TextBlock)panel.Children[1];

        var color = CoursePalette.ResolveColor(slot.Course);
        var foreground = CoursePalette.ForegroundFor(color);
        var (start, end) = SlotBounds(slot);

        title.Text = slot.Course.Name ?? "";
        title.FontSize = titleFont;
        title.Foreground = foreground;

        var infoParts = new List<string>(3);
        if (start != null && end != null) infoParts.Add($"{start:HH:mm}–{end:HH:mm}");
        else if (start != null) infoParts.Add(start.Value.ToString("HH:mm"));
        if (model.ShowRoom && slot.Course.Room is { Length: > 0 } room) infoParts.Add(room);
        if (model.ShowTeacher && slot.Course.Teacher is { Length: > 0 } teacher) infoParts.Add(teacher);

        sub.Text = infoParts.Count > 0 ? string.Join(" · ", infoParts) : "";
        sub.FontSize = subFont;
        sub.Foreground = foreground;
        sub.IsVisible = infoParts.Count > 0;

        row.Background = CoursePalette.BrushFor(color);
        ToolTip.SetTip(row, ScheduleBlocks.BuildTooltip(slot));
        todayRows.Add(new RowHandle { Row = row, Start = start, End = end });
    }

    // ---------- Week switching ----------

    private int GetCurrentOrViewingWeek()
    {
        var currentWeek = TimetableClock.ComputeWeek(model, DateTime.Now);
        if (viewingWeek >= 1) return viewingWeek;
        return currentWeek >= 1 ? currentWeek : 1;
    }

    private int GetMaxWeek()
    {
        var currentWeek = TimetableClock.ComputeWeek(model, DateTime.Now);
        var baseMax = model.TotalWeeks > 0 ? model.TotalWeeks : 25;
        return Math.Max(baseMax, Math.Max(currentWeek, 20));
    }

    private void OnPrevWeekClicked(object? sender, RoutedEventArgs e)
    {
        var current = GetCurrentOrViewingWeek();
        viewingWeek = Math.Max(1, current - 1);
        RebuildAll();
    }

    private void OnNextWeekClicked(object? sender, RoutedEventArgs e)
    {
        var current = GetCurrentOrViewingWeek();
        viewingWeek = Math.Min(GetMaxWeek(), current + 1);
        RebuildAll();
    }

    private void OnResetWeekClicked(object? sender, RoutedEventArgs e)
    {
        var currentWeek = TimetableClock.ComputeWeek(model, DateTime.Now);
        viewingWeek = currentWeek >= 1 ? currentWeek : 1;
        RebuildAll();
    }

    private void OnWeekTitleClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control target) return;

        var flyout = new MenuFlyout();
        var currentWeek = TimetableClock.ComputeWeek(model, DateTime.Now);
        var maxWeek = GetMaxWeek();
        var active = GetCurrentOrViewingWeek();

        for (var w = 1; w <= maxWeek; w++)
        {
            var weekNum = w;
            var isCurrent = weekNum == currentWeek;
            var isSelected = weekNum == active;
            var header = Locale.Timetable_Week.Replace("{0}", weekNum.ToString());
            if (isCurrent) header += $" ({Locale.Timetable_Today})";
            if (isSelected) header = "● " + header;

            var item = new MenuItem
            {
                Header = header,
                FontWeight = isSelected ? FontWeight.Bold : FontWeight.Normal,
            };
            item.Click += (_, _) =>
            {
                viewingWeek = weekNum;
                RebuildAll();
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(target);
    }

    private void BuildWeekGrid(double width, double height, bool compact)
    {
        // Sync, don't Clear-and-rebuild. Pulling pooled controls out of the visual tree and
        // re-adding them resets every inherited property (FontFamily/FontSize/Foreground),
        // which rebuilds each TextLayout — ~4 KB of native shaped text per TextBlock that
        // the GC cannot attribute — on every resize step and week flip. The pooled controls
        // therefore stay attached to the host for their whole life: a rebuild rebinds
        // properties in place (equal values never invalidate) and surplus slots are hidden.
        weekBlocks.Clear();
        periodLabels.Clear();

        var now = DateTime.Now;
        var currentWeek = TimetableClock.ComputeWeek(model, now);
        var week = GetCurrentOrViewingWeek();
        var isThisWeek = currentWeek >= 1 && week == currentWeek;
        var today = DayIndex(now.DayOfWeek);
        var dayCount = model.ShowWeekend ? 7 : 5;

        // Which period rows the grid displays: the compact tier skips rows that hold no lesson
        // at all; the large tier shows every period. Both scroll when they do not fit.
        var usedPeriods = new List<int>();
        if (compact)
        {
            var seen = new HashSet<int>();
            for (var day = 1; day <= 7; day++)
                foreach (var slot in runtime.ByDay[day])
                    if (slot.Entry.Periods != null && TimetableRuntime.IsActiveThisWeek(slot, week))
                        foreach (var p in slot.Entry.Periods)
                            seen.Add(p);

            if (seen.Count == 0)
            {
                for (var day = 1; day <= 7; day++)
                    foreach (var slot in runtime.ByDay[day])
                        if (slot.Entry.Periods != null)
                            foreach (var p in slot.Entry.Periods)
                                seen.Add(p);
            }
            usedPeriods.AddRange(seen);
            usedPeriods.Sort();
        }
        else
        {
            for (var p = 1; p <= Math.Max(1, runtime.MaxPeriod); p++) usedPeriods.Add(p);
        }

        if (usedPeriods.Count == 0) return;

        // Account for card horizontal margins (6 on left, 6 on right, and 2 inside host)
        width = Math.Max(80, width - 14);

        // Adaptive column distribution:
        // Compact tier uses a slim 18px period column so the 5 or 7 day columns get max space.
        var labelW = compact ? 18.0 : Math.Clamp(width * 0.065, 20.0, 32.0);
        var gridWidth = Math.Max(60, width - labelW);
        var colW = gridWidth / dayCount;
        var headerH = Math.Clamp(colW * 0.38, 16.0, 22.0);
        var viewportHeight = Math.Max(40, height - headerH - 10);

        var minRowH = compact ? 22.0 : 26.0;
        var rowH = Math.Max(minRowH, (viewportHeight - 2) / usedPeriods.Count);
        var titleFont = TextMetrics.Quantize(Math.Clamp(Math.Min(colW * 0.28, rowH * 0.34), 9.5, 13.0));
        var subFont = TextMetrics.Quantize(Math.Max(8.0, titleFont * 0.82));
        var headFont = TextMetrics.Quantize(Math.Clamp(Math.Min(colW * 0.32, headerH * 0.68), 10.0, 12.5));
        var labelFont = TextMetrics.Quantize(Math.Clamp(rowH * 0.30, 8.5, 11.0));

        // Definitions: label column + one column per day; header row + one fixed-height row per period.
        SyncGridDefinitions(labelW, dayCount, headerH, rowH, usedPeriods.Count);

        var accent = EnsureAccentBrush();

        // Day header pills (one per day column; the today column carries the tinted pill).
        var headerIndex = 0;
        for (var dayIdx = 0; dayIdx < dayCount; dayIdx++)
        {
            var day = dayIdx + 1;
            var isTodayColumn = isThisWeek && (day == today);
            var headText = colW >= 56 ? TimetableClock.DayName(day) : TimetableClock.DayShortName(day);

            var pill = TakeAttachedFromPool(headerPool, headerIndex++, WeekGridHost, static () => new Border
            {
                Padding = new Thickness(4, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            var head = (TextBlock)pill.Child!;

            head.Text = headText;
            head.FontSize = headFont;
            if (isTodayColumn)
            {
                head.Foreground = accent;
                head.FontWeight = FontWeight.Bold;
                head.Opacity = 1.0;
                pill.Background = new SolidColorBrush(Color.FromArgb(0x28, accentColor.R, accentColor.G, accentColor.B));
            }
            else
            {
                head.ClearValue(TextBlock.ForegroundProperty);
                head.FontWeight = FontWeight.Normal;
                head.Opacity = 0.72;
                pill.Background = null;
            }
            pill.CornerRadius = new CornerRadius(headerH / 2);
            pill.Padding = new Thickness(Math.Max(4, colW * 0.08), 1);
            pill.IsVisible = true;

            Grid.SetRow(pill, 0);
            Grid.SetColumn(pill, dayIdx + 1);
        }
        HidePoolSurplus(headerPool, headerIndex);

        // Period labels + empty cell backgrounds.
        var laneIndex = 0;
        var labelIndex = 0;
        for (var r = 0; r < usedPeriods.Count; r++)
        {
            var period = usedPeriods[r];
            var labelText = labelW <= 22 ? period.ToString() : TimetableClock.PeriodShortLabel(model, period);
            var label = TakeAttachedFromPool(periodLabelPool, labelIndex++, WeekGridHost, static () => new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            });
            label.Text = labelText;
            label.FontSize = labelFont;
            label.Opacity = 0.65;
            label.ClearValue(TextBlock.ForegroundProperty);
            label.IsVisible = true;
            Grid.SetRow(label, r + 1);
            Grid.SetColumn(label, 0);
            periodLabels.Add((label, period));

            for (var dayIdx = 0; dayIdx < dayCount; dayIdx++)
            {
                // The lane background is the biggest control count in the grid (periods × days):
                // identical every rebuild, so the pooled Borders are only ever moved in place.
                var cell = TakeAttachedFromPool(laneCellPool, laneIndex++, WeekGridHost, static () => new Border
                {
                    Background = ScheduleBlocks.LaneBrush,
                    CornerRadius = new CornerRadius(5),
                    Margin = new Thickness(1),
                });
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, dayIdx + 1);
                cell.IsVisible = true;
            }
        }
        HidePoolSurplus(periodLabelPool, labelIndex);
        HidePoolSurplus(laneCellPool, laneIndex);

        // Course blocks (placement maths live in ScheduleGridRenderer, shared with the other surfaces).
        // Host-level blocks and per-container blocks both stay attached where they are; a
        // rebuild rebinds and re-positions in place, and surplus slots are hidden.
        var blockIndex = 0;
        var containerIndex = 0;
        for (var dayIdx = 0; dayIdx < dayCount; dayIdx++)
        {
            var placements = ScheduleGridRenderer.LayoutDay(runtime, dayIdx + 1, week, usedPeriods);
            foreach (var group in placements.GroupBy(p => p.GroupId))
            {
                var items = group.ToList();
                var first = items[0];
                var row = first.RowIndex + 1;
                var span = first.RowSpan;
                var regionH = rowH * span;

                if (first.LaneCount == 1)
                {
                    var block = TakeAttachedFromPool(blockPool, blockIndex++, WeekGridHost, CreateBlockControl);
                    RebindBlockInPlace(block, first.Slot, titleFont, subFont,
                        showSub: regionH >= 24, model.ShowRoom, model.ShowTeacher);
                    Grid.SetRow(block, row);
                    Grid.SetRowSpan(block, span);
                    Grid.SetColumn(block, dayIdx + 1);
                    block.IsVisible = true;
                    AddBlockHandle(block, first.Slot, dayIdx);
                }
                else
                {
                    LaneContainer lane;
                    if (containerIndex < laneContainerPool.Count)
                    {
                        lane = laneContainerPool[containerIndex];
                    }
                    else
                    {
                        lane = new LaneContainer { Grid = new Grid() };
                        laneContainerPool.Add(lane);
                        WeekGridHost.Children.Add(lane.Grid);
                    }
                    containerIndex++;
                    var container = lane.Grid;

                    if (container.ColumnDefinitions.Count != first.LaneCount)
                    {
                        container.ColumnDefinitions.Clear();
                        for (var i = 0; i < first.LaneCount; i++)
                            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    }

                    var laneBlock = 0;
                    foreach (var item in items)
                    {
                        if (lane.Blocks.Count <= laneBlock)
                        {
                            var created = CreateBlockControl();
                            lane.Grid.Children.Add(created);
                            lane.Blocks.Add(created);
                        }
                        var block = lane.Blocks[laneBlock++];
                        RebindBlockInPlace(block, item.Slot,
                            TextMetrics.Quantize(Math.Max(9.0, titleFont * 0.9)),
                            TextMetrics.Quantize(Math.Max(8.0, subFont * 0.88)),
                            showSub: regionH >= 28, model.ShowRoom, model.ShowTeacher);
                        Grid.SetColumn(block, item.Lane);
                        block.IsVisible = true;
                        AddBlockHandle(block, item.Slot, dayIdx);
                    }
                    for (; laneBlock < lane.Blocks.Count; laneBlock++)
                        lane.Blocks[laneBlock].IsVisible = false;

                    Grid.SetRow(container, row);
                    Grid.SetRowSpan(container, span);
                    Grid.SetColumn(container, dayIdx + 1);
                    container.IsVisible = true;
                }
            }
        }
        HidePoolSurplus(blockPool, blockIndex);
        for (var i = containerIndex; i < laneContainerPool.Count; i++)
            laneContainerPool[i].Grid.IsVisible = false;

        WeekTitle.FontSize = TextMetrics.Quantize(Math.Clamp(Math.Min(width, height) * 0.075, 11.0, 14.5));
        WeekSubtitle.FontSize = TextMetrics.Quantize(Math.Clamp(Math.Min(width, height) * 0.062, 9.5, 12.0));

        WeekTitle.Text = Locale.Timetable_Week.Replace("{0}", week.ToString());
        WeekSubtitle.Text = currentWeek < 1 ? Locale.Timetable_UnknownWeek : "";

        ResetWeekButton.IsVisible = (currentWeek >= 1 && week != currentWeek);
    }

    /// <summary>
    /// Pool accessor for controls that stay attached to <paramref name="parent"/> for their
    /// whole life: grow with the factory as needed, then hand out slot <paramref name="index"/>.
    /// Rebinds never remove and re-add controls — re-parenting resets inherited properties and
    /// forces a full TextLayout rebuild per TextBlock (native memory the GC cannot reclaim).
    /// </summary>
    private static T TakeAttachedFromPool<T>(List<T> pool, int index, Panel parent, Func<T> factory)
        where T : Control
    {
        while (pool.Count <= index)
        {
            var control = factory();
            pool.Add(control);
            parent.Children.Add(control);
        }
        return pool[index];
    }

    /// <summary>Hide pool slots beyond the currently bound count; they stay attached.</summary>
    private static void HidePoolSurplus<T>(List<T> pool, int needed) where T : Control
    {
        for (var i = needed; i < pool.Count; i++) pool[i].IsVisible = false;
    }

    /// <summary>
    /// Align the host's grid definitions with the current shape. Definitions carry no native
    /// state, so they are rebuilt when the shape changes and mutated in place (row heights)
    /// when only the metrics moved.
    /// </summary>
    private void SyncGridDefinitions(double labelW, int dayCount, double headerH, double rowH, int periodRows)
    {
        var cols = WeekGridHost.ColumnDefinitions;
        if (cols.Count != dayCount + 1)
        {
            cols.Clear();
            cols.Add(new ColumnDefinition { Width = new GridLength(labelW, GridUnitType.Pixel) });
            for (var i = 0; i < dayCount; i++)
                cols.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            cols[0].Width = new GridLength(labelW, GridUnitType.Pixel);
        }

        var rows = WeekGridHost.RowDefinitions;
        if (rows.Count != periodRows + 1)
        {
            rows.Clear();
            rows.Add(new RowDefinition { Height = new GridLength(headerH, GridUnitType.Pixel) });
            for (var i = 0; i < periodRows; i++)
                rows.Add(new RowDefinition { Height = new GridLength(rowH, GridUnitType.Pixel) });
        }
        else
        {
            rows[0].Height = new GridLength(headerH, GridUnitType.Pixel);
            for (var i = 1; i < rows.Count; i++)
                rows[i].Height = new GridLength(rowH, GridUnitType.Pixel);
        }
    }

    /// <summary>Build a fresh course-block control (Border → StackPanel → title + info line).</summary>
    private static Border CreateBlockControl()
    {
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 1,
        };
        stack.Children.Add(new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        stack.Children.Add(new TextBlock
        {
            Opacity = 0.88,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 1, 0, 0),
        });
        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(1),
            Padding = new Thickness(2, 2, 2, 2),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = stack,
        };
    }

    /// <summary>
    /// Rebind a pooled course block in place to <paramref name="slot"/>. The block's tree
    /// (Border → StackPanel → title + optional info line) is fixed; a rebuild only changes
    /// texts, fonts, colours and the info line's visibility. Equal assignments never
    /// invalidate, so an unchanged slot costs no TextLayout rebuild at all.
    /// </summary>
    private static void RebindBlockInPlace(Border border, TimetableRuntime.Slot slot, double titleFont, double subFont,
        bool showSub, bool showRoom, bool showTeacher)
    {
        var panel = (StackPanel)border.Child!;
        var titleBlock = (TextBlock)panel.Children[0];
        var subBlock = (TextBlock)panel.Children[1];

        var color = CoursePalette.ResolveColor(slot.Course);
        var foreground = CoursePalette.ForegroundFor(color);
        var info = showSub ? ScheduleBlocks.ComposeInfoLine(slot.Course, showRoom, showTeacher) : null;

        border.Background = CoursePalette.BrushFor(color);
        titleBlock.Text = slot.Course.Name ?? "";
        titleBlock.FontSize = titleFont;
        titleBlock.Foreground = foreground;
        subBlock.Text = info ?? "";
        subBlock.FontSize = subFont;
        subBlock.Foreground = foreground;
        subBlock.IsVisible = info != null;
        ToolTip.SetTip(border, ScheduleBlocks.BuildTooltip(slot));
    }

    private void AddBlockHandle(Border block, TimetableRuntime.Slot slot, int column)
    {
        var (start, end) = SlotBounds(slot);
        weekBlocks.Add(new BlockHandle
        {
            Border = block,
            Start = start,
            End = end,
            Column = column,
        });
    }

    // ---------- Secondary panel ----------

    private void OnOpenPanelClicked(object? sender, RoutedEventArgs e)
    {
        OpenPanel();
    }

    /// <summary>
    /// Open the full-schedule secondary panel, anchored on this widget — triggered by the
    /// card's corner button, so a plain click never fights the drag gesture.
    /// Shows the complete week grid on a large readable surface.
    /// </summary>
    public void OpenPanel()
    {
        if (!model.HasData) return;

        Point? screenCenter = null;
        if (VisualRoot is Visual rootVisual && VisualRoot is TopLevel topLevel)
        {
            var centerLocal = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, rootVisual);
            if (rootPoint.HasValue)
            {
                var screenPoint = topLevel.PointToScreen(rootPoint.Value);
                screenCenter = new Point(screenPoint.X, screenPoint.Y);
            }
        }

        var owner = VisualRoot as Window;
        var radius = (VisualRoot as DeskCanvas.Views.Widget)?.Radius.TopLeft ?? 0;
        TimetablePanelWindow.ShowPanel(model, screenCenter, owner, radius > 0 ? radius : null);
    }

    // ---------- Live state ----------

    private void UpdateLiveState()
    {
        if (!IsInitialized) return;

        var now = DateTime.Now;
        var today = DayIndex(now.DayOfWeek);

        if (builtDayKey != today && model.HasData)
        {
            // The calendar day rolled over while the widget stayed on the desktop: rebuild so
            // "today" highlights and the list target follow the new day (bounded to once a day).
            builtDayKey = today;
            RebuildAll();
            return;
        }
        builtDayKey = today;

        var currentWeek = TimetableClock.ComputeWeek(model, now);
        var week = GetCurrentOrViewingWeek();
        var isThisWeek = currentWeek >= 1 && week == currentWeek;
        var nowTime = TimeOnly.FromDateTime(now);

        if (CellLayout.IsVisible)
        {
            var state = ComputeCellState(nowTime, week);
            CellTag.Text = state.Tag;
            CellTitle.Text = state.Title;
            CellSub.Text = state.Sub;
            CellSub.IsVisible = state.Sub.Length > 0;
        }

        if (TodayLayout.IsVisible)
        {
            var isToday = todayListIsToday && todayListDay == today;
            var accent = isToday ? EnsureAccentBrush() : null;
            foreach (var handle in todayRows)
            {
                var active = isToday && handle.Start != null && handle.End != null &&
                             nowTime >= handle.Start.Value && nowTime < handle.End.Value;
                var past = isToday && handle.End != null && nowTime >= handle.End.Value;

                if (active)
                {
                    handle.Row.BorderBrush = accent;
                    handle.Row.BorderThickness = new Thickness(1.5);
                }
                else if (handle.Row.BorderThickness != default)
                {
                    handle.Row.BorderBrush = null;
                    handle.Row.BorderThickness = default;
                }
                handle.Row.Opacity = past ? 0.5 : 1.0;
            }
        }

        if (WeekLayout.IsVisible)
        {
            var accent = EnsureAccentBrush();
            var dayCount = model.ShowWeekend ? 7 : 5;
            var todayColumn = (isThisWeek && today <= dayCount) ? today - 1 : -1;

            foreach (var handle in weekBlocks)
            {
                var active = todayColumn >= 0 && handle.Column == todayColumn &&
                             handle.Start != null && handle.End != null &&
                             nowTime >= handle.Start.Value && nowTime < handle.End.Value;
                if (active)
                {
                    handle.Border.BorderBrush = accent;
                    handle.Border.BorderThickness = new Thickness(1.5);
                }
                else if (handle.Border.BorderThickness != default)
                {
                    handle.Border.BorderBrush = null;
                    handle.Border.BorderThickness = default;
                }
            }

            foreach (var (label, period) in periodLabels)
            {
                var (start, end) = TimetableClock.PeriodBounds(model, period);
                var isNow = isThisWeek && start != null && end != null && nowTime >= start.Value && nowTime < end.Value;
                if (isNow)
                {
                    label.Foreground = accent;
                    label.Opacity = 1.0;
                }
                else if (label.Foreground != null || label.Opacity < 1.0)
                {
                    label.ClearValue(TextBlock.ForegroundProperty);
                    label.Opacity = 0.65;
                }
            }
        }
    }

    private (string Tag, string Title, string Sub) ComputeCellState(TimeOnly nowTime, int week)
    {
        var today = DayIndex(DateTime.Now.DayOfWeek);
        var slots = ActiveSlotsForDay(today, week);

        var hasTimes = false;
        foreach (var slot in slots)
        {
            var (s, e) = SlotBounds(slot);
            if (s != null && e != null) { hasTimes = true; break; }
        }

        if (hasTimes)
        {
            foreach (var slot in slots)
            {
                var (s, e) = SlotBounds(slot);
                if (s != null && e != null && nowTime >= s.Value && nowTime < e.Value)
                    return (Locale.Timetable_Ongoing, slot.Course.Name ?? "", Locale.Timetable_Until.Replace("{0}", e.Value.ToString("HH:mm")));
            }

            foreach (var slot in slots)
            {
                var (s, _) = SlotBounds(slot);
                if (s != null && s.Value > nowTime)
                {
                    var sub = s.Value.ToString("HH:mm");
                    if (ComposeRoomOnly(slot.Course) is { } room) sub += " · " + room;
                    return (Locale.Timetable_Next, slot.Course.Name ?? "", sub);
                }
            }
        }
        else if (slots.Count > 0)
        {
            var first = slots[0];
            return (Locale.Timetable_Today, first.Course.Name ?? "", TimetableClock.PeriodLabel(model, first.FirstPeriod));
        }

        // Nothing left today — show what is coming next within the week.
        for (var offset = 1; offset <= 6; offset++)
        {
            var day = ((today - 1 + offset) % 7) + 1;
            var next = ActiveSlotsForDay(day, week);
            if (next.Count == 0) continue;

            var first = next[0];
            var (s, _) = SlotBounds(first);
            var sub = s?.ToString("HH:mm") ?? "";
            if (ComposeRoomOnly(first.Course) is { } room)
                sub = sub.Length > 0 ? sub + " · " + room : room;
            var tag = offset == 1 ? Locale.Timetable_Tomorrow : TimetableClock.DayName(day);
            return (tag, first.Course.Name ?? "", sub);
        }

        return slots.Count > 0
            ? (Locale.Timetable_Today, Locale.Timetable_FinishedToday, "")
            : (Locale.Timetable_Today, Locale.Timetable_NoClassToday, "");
    }

    // ---------- Helpers ----------

    private List<TimetableRuntime.Slot> ActiveSlotsForDay(int day, int week)
    {
        var result = new List<TimetableRuntime.Slot>();
        if (day is < 1 or > 7) return result;

        foreach (var slot in runtime.ByDay[day])
            if (TimetableRuntime.IsActiveThisWeek(slot, week))
                result.Add(slot);
        return result;
    }

    private (TimeOnly? Start, TimeOnly? End) SlotBounds(TimetableRuntime.Slot slot)
    {
        var start = TimetableClock.PeriodBounds(model, slot.FirstPeriod).Start;
        var end = TimetableClock.PeriodBounds(model, slot.LastPeriod).End;
        return (start, end);
    }

    private string? ComposeRoomOnly(TimetableCourse course) =>
        model.ShowRoom && course.Room is { Length: > 0 } room ? room : null;

    private static int DayIndex(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    private IBrush EnsureAccentBrush()
    {
        var color = ThemeScopeResolver.ResolveColor(this, "SystemAccentColor", ActualThemeVariant, Color.FromRgb(0x00, 0x78, 0xD4));

        if (accentBrush == null || accentColor != color)
        {
            accentColor = color;
            accentBrush = new SolidColorBrush(color);
        }
        return accentBrush;
    }
}
