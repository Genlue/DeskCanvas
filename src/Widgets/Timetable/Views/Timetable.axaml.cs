using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
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
/// exactly one shared timer in <see cref="OnLoaded"/> and releases it in <see cref="OnUnloaded"/>,
/// and implements <see cref="IWidgetSelfRefreshing"/> so settings changes rebind in place
/// instead of recreating the whole view.
/// </para>
/// </summary>
public partial class Timetable : UserControl, IWidgetSelfRefreshing
{
    private readonly Action liveTick;

    private TimetableModel model;
    private TimetableRuntime runtime;

    private bool attached;
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

    public Timetable(TimetableModel? model)
    {
        this.model = (model != null && model.HasData ? model : TimetableStore.Get()).Normalize();
        runtime = TimetableRuntime.Build(this.model);
        liveTick = OnLiveTick;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ToolTip.SetTip(this, Locale.Timetable_OpenPanelHint);

        TimetableStore.ModelChanged += OnStoreModelChanged;

        RebuildAll();
    }

    // ---------- Lifecycle ----------

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        attached = true;
        TimerService.Timer1Minute.Subscribe(liveTick);
        UpdateLiveState();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        attached = false;
        TimerService.Timer1Minute.Unsubscribe(liveTick);
        TimetableStore.ModelChanged -= OnStoreModelChanged;
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
        if (!attached) return;
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
            EmptyHint.FontSize = Math.Clamp(Math.Min(width, height) * 0.11, 10, 15);
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
        CellTag.FontSize = Math.Clamp(min * 0.13, 10, 14);
        CellTitle.FontSize = Math.Clamp(min * 0.19, 12.5, 22);
        CellSub.FontSize = Math.Clamp(min * 0.12, 9.5, 13);
        CellTitle.MaxWidth = Math.Max(40, width - 14);
        CellSub.MaxWidth = Math.Max(40, width - 14);
    }

    private void BuildTodayList(double width, double height)
    {
        TodayListPanel.Children.Clear();
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
        TodayHeader.FontSize = Math.Clamp(Math.Min(width, height) * 0.10, 10.5, 14);

        var slots = ActiveSlotsForDay(targetDay, week);
        if (slots.Count == 0)
        {
            TodayListPanel.Children.Add(new TextBlock
            {
                Text = Locale.Timetable_NoClassToday,
                Opacity = 0.72,
                FontSize = Math.Clamp(Math.Min(width, height) * 0.10, 10.5, 14),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 2, 0),
            });
            return;
        }

        // Readability first: one comfortable colour card per lesson; the list scrolls when it
        // does not fit instead of shrinking the type to cram every lesson into the card.
        var titleFont = Math.Clamp(width * 0.088, 11.5, 15);
        var subFont = Math.Clamp(titleFont * 0.8, 9.5, 12);

        foreach (var slot in slots)
            TodayListPanel.Children.Add(BuildTodayRow(slot, titleFont, subFont));
    }

    /// <summary>
    /// One lesson as a solid colour card: the course name leads and is never sacrificed to
    /// secondary info; the time/room line follows at a readable size on the same contrast
    /// colour (not a grey caption). Current-lesson highlighting is applied by
    /// <see cref="UpdateLiveState"/> as an accent outline.
    /// </summary>
    private Border BuildTodayRow(TimetableRuntime.Slot slot, double titleFont, double subFont)
    {
        var color = CoursePalette.ResolveColor(slot.Course);
        var foreground = CoursePalette.ForegroundFor(color);
        var (start, end) = SlotBounds(slot);

        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(new TextBlock
        {
            Text = slot.Course.Name ?? "",
            FontSize = titleFont,
            FontWeight = FontWeight.SemiBold,
            Foreground = foreground,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var infoParts = new List<string>(3);
        if (start != null && end != null) infoParts.Add($"{start:HH:mm}–{end:HH:mm}");
        else if (start != null) infoParts.Add(start.Value.ToString("HH:mm"));
        if (model.ShowRoom && slot.Course.Room is { Length: > 0 } room) infoParts.Add(room);
        if (model.ShowTeacher && slot.Course.Teacher is { Length: > 0 } teacher) infoParts.Add(teacher);

        if (infoParts.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", infoParts),
                FontSize = subFont,
                Foreground = foreground,
                Opacity = 0.9,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        var row = new Border
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 6, 9, 6),
            Child = stack,
        };

        ToolTip.SetTip(row, ScheduleBlocks.BuildTooltip(slot));
        todayRows.Add(new RowHandle { Row = row, Start = start, End = end });
        return row;
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
        WeekGridHost.Children.Clear();
        WeekGridHost.ColumnDefinitions.Clear();
        WeekGridHost.RowDefinitions.Clear();
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
        var titleFont = Math.Clamp(Math.Min(colW * 0.28, rowH * 0.34), 9.5, 13.0);
        var subFont = Math.Max(8.0, titleFont * 0.82);
        var headFont = Math.Clamp(Math.Min(colW * 0.32, headerH * 0.68), 10.0, 12.5);
        var labelFont = Math.Clamp(rowH * 0.30, 8.5, 11.0);

        // Definitions: label column + one column per day; header row + one fixed-height row per period.
        WeekGridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelW, GridUnitType.Pixel) });
        for (var i = 0; i < dayCount; i++)
            WeekGridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        WeekGridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerH, GridUnitType.Pixel) });
        for (var i = 0; i < usedPeriods.Count; i++)
            WeekGridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowH, GridUnitType.Pixel) });

        var accent = EnsureAccentBrush();

        // Day header.
        for (var dayIdx = 0; dayIdx < dayCount; dayIdx++)
        {
            var day = dayIdx + 1;
            var isTodayColumn = isThisWeek && (day == today);
            var headText = colW >= 56 ? TimetableClock.DayName(day) : TimetableClock.DayShortName(day);

            var head = new TextBlock
            {
                Text = headText,
                FontSize = headFont,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Control headerControl = head;
            if (isTodayColumn)
            {
                head.Foreground = accent;
                head.FontWeight = FontWeight.Bold;

                var pill = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x28, accentColor.R, accentColor.G, accentColor.B)),
                    CornerRadius = new CornerRadius(headerH / 2),
                    Padding = new Thickness(Math.Max(4, colW * 0.08), 1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = head,
                };
                headerControl = pill;
            }
            else
            {
                head.Opacity = 0.72;
            }

            Grid.SetRow(headerControl, 0);
            Grid.SetColumn(headerControl, dayIdx + 1);
            WeekGridHost.Children.Add(headerControl);
        }

        // Period labels + empty cell backgrounds.
        for (var r = 0; r < usedPeriods.Count; r++)
        {
            var period = usedPeriods[r];
            var labelText = labelW <= 22 ? period.ToString() : TimetableClock.PeriodShortLabel(model, period);
            var label = new TextBlock
            {
                Text = labelText,
                FontSize = labelFont,
                Opacity = 0.65,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(label, r + 1);
            Grid.SetColumn(label, 0);
            WeekGridHost.Children.Add(label);
            periodLabels.Add((label, period));

            for (var dayIdx = 0; dayIdx < dayCount; dayIdx++)
            {
                var cell = new Border
                {
                    Background = ScheduleBlocks.LaneBrush,
                    CornerRadius = new CornerRadius(5),
                    Margin = new Thickness(1),
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, dayIdx + 1);
                WeekGridHost.Children.Add(cell);
            }
        }

        // Course blocks (placement maths live in ScheduleGridRenderer, shared with the other surfaces).
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
                    var block = ScheduleBlocks.Create(first.Slot, titleFont, subFont, showSub: regionH >= 24,
                        model.ShowRoom, model.ShowTeacher);
                    Grid.SetRow(block, row);
                    Grid.SetRowSpan(block, span);
                    Grid.SetColumn(block, dayIdx + 1);
                    WeekGridHost.Children.Add(block);
                    AddBlockHandle(block, first.Slot, dayIdx);
                }
                else
                {
                    var container = new Grid();
                    for (var i = 0; i < first.LaneCount; i++)
                        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    foreach (var item in items)
                    {
                        var block = ScheduleBlocks.Create(item.Slot, Math.Max(9.0, titleFont * 0.9), Math.Max(8.0, subFont * 0.88),
                            showSub: regionH >= 28, model.ShowRoom, model.ShowTeacher);
                        Grid.SetColumn(block, item.Lane);
                        container.Children.Add(block);
                        AddBlockHandle(block, item.Slot, dayIdx);
                    }

                    Grid.SetRow(container, row);
                    Grid.SetRowSpan(container, span);
                    Grid.SetColumn(container, dayIdx + 1);
                    WeekGridHost.Children.Add(container);
                }
            }
        }

        WeekTitle.FontSize = Math.Clamp(Math.Min(width, height) * 0.075, 11.0, 14.5);
        WeekSubtitle.FontSize = Math.Clamp(Math.Min(width, height) * 0.062, 9.5, 12.0);

        WeekTitle.Text = Locale.Timetable_Week.Replace("{0}", week.ToString());
        WeekSubtitle.Text = currentWeek < 1 ? Locale.Timetable_UnknownWeek : "";

        ResetWeekButton.IsVisible = (currentWeek >= 1 && week != currentWeek);
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
        var color = this.TryFindResource("SystemAccentColor", out var value) && value is Color accent
            ? accent
            : Color.FromRgb(0x00, 0x78, 0xD4);

        if (accentBrush == null || accentColor != color)
        {
            accentColor = color;
            accentBrush = new SolidColorBrush(color);
        }
        return accentBrush;
    }
}
