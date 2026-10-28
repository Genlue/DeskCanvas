using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Timetable.Locales;
using Timetable.Models;
using Timetable.Services;

namespace Timetable.Views;

/// <summary>
/// Full timetable panel: week grid and course notes. Week navigation is panel-local and never
/// written back; the current-lesson outline is computed once on open.
/// </summary>
public partial class TimetablePanelWindow : SecondaryPanelWindow
{
    private readonly Point? spawnScreenCenter;
    private readonly TimetableModel model;

    private TimetableRuntime? runtime;

    /// <summary>The real term week at open time (0 when the term date cannot be resolved).</summary>
    private int currentWeek;

    /// <summary>Last selectable week: the declared term length, the model's override, or the
    /// highest week any course rule mentions — whichever is largest.</summary>
    private int lastWeek = 1;

    /// <summary>The week the grid currently shows (ignored when <see cref="showAllWeeks"/>).</summary>
    private int displayWeek;

    /// <summary>Whole-term mode: passes week 0 to the renderer (its "no week context" value).</summary>
    private bool showAllWeeks;

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public TimetablePanelWindow()
        : this(new TimetableModel(), null, null)
    {
    }

    public TimetablePanelWindow(TimetableModel model, Point? screenCenter = null, double? cornerRadius = null)
    {
        this.model = (model ?? new TimetableModel()).Normalize();
        spawnScreenCenter = screenCenter;

        InitializeComponent();
        InitializePanel(cornerRadius);

        try
        {
            ApplyLocalization();
            BuildContent();
        }
        catch
        {
            AbandonPanel(this);
            throw;
        }
    }

    /// <summary>One panel per owning widget at a time; re-triggering toggles the open one (the service handles cooldown).</summary>
    public static void ShowPanel(TimetableModel model, Point? screenCenter, Window? owner = null, double? cornerRadius = null)
    {
        if (model == null || !model.HasData) return;

        // PerOwner identity (the weather panel precedent): every timetable widget owns its own
        // panel. The service handles the cooldown, the toggle-close and the pinned show — the
        // decision is deliberately ignored, mirroring the old bare early returns.
        PanelService.Open(new PanelRequest(
            owner is Widget widget ? widget.InstanceId : default,
            owner,
            "timetable.panel",
            PanelScope.PerOwner,
            () => new TimetablePanelWindow(model, screenCenter, cornerRadius)));
    }

    private void ApplyLocalization()
    {
        Title = Locale.Timetable_Title;
        HeaderTitleText.Text = model.Name ?? Locale.Timetable_Title;
        NotesHeaderText.Text = Locale.Timetable_Review_Notes;

        NameButton(PreviousWeekButton, Locale.Timetable_PreviousWeek);
        NameButton(NextWeekButton, Locale.Timetable_NextWeek);
        NameButton(BackToCurrentWeekButton, Locale.Timetable_BackToCurrentWeek);
    }

    /// <summary>Tooltip + accessible name from one localized string (icon-only buttons).</summary>
    private static void NameButton(Button button, string label)
    {
        ToolTip.SetTip(button, label);
        AutomationProperties.SetName(button, label);
    }

    // ---------- Content ----------

    private void BuildContent()
    {
        var now = DateTime.Now;
        currentWeek = TimetableClock.ComputeWeek(model, now);
        lastWeek = ResolveLastWeek(currentWeek);

        // A resolved term date opens on the current week; with no week context the panel keeps
        // its historical behaviour and shows everything (which is what week 0 means).
        showAllWeeks = currentWeek < 1;
        displayWeek = currentWeek >= 1 ? Math.Clamp(currentWeek, 1, lastWeek) : 1;

        runtime = TimetableRuntime.Build(model);
        BuildNotes();
        RefreshWeekView(now);
    }

    /// <summary>
    /// Re-renders the grid and the week chrome for the current panel state. Only the grid host
    /// and the header badge are touched — the window, the notes card and the runtime survive.
    /// </summary>
    private void RefreshWeekView(DateTime now)
    {
        var week = showAllWeeks ? 0 : displayWeek;

        WeekBadgeText.Text = showAllWeeks
            ? Locale.Timetable_AllWeeks
            : Locale.Timetable_Week.Replace("{0}", displayWeek.ToString());

        BuildGrid(runtime ?? TimetableRuntime.Empty, week, now);

        // Out-of-range is disabled, not silently clamped: a button that looks clickable but
        // does nothing is worse than a visibly unavailable one.
        PreviousWeekButton.IsEnabled = !showAllWeeks && displayWeek > 1;
        NextWeekButton.IsEnabled = !showAllWeeks && displayWeek < lastWeek;
        BackToCurrentWeekButton.IsEnabled = currentWeek >= 1 && (showAllWeeks || displayWeek != currentWeek);

        WeekModeButton.Content = showAllWeeks ? Locale.Timetable_WeekView : Locale.Timetable_AllWeeks;
        NameButton(WeekModeButton, showAllWeeks ? Locale.Timetable_ShowWeekView : Locale.Timetable_ShowAllWeeks);
    }

    /// <summary>
    /// Upper bound of the selectable week range. The declared term length wins; when the term
    /// length is unset the highest week mentioned by any course rule (and the current week)
    /// keeps the range meaningful. Always at least 1 and bounded by the import limit.
    /// </summary>
    private int ResolveLastWeek(int currentWeek)
    {
        var last = Math.Max(model.TotalWeeks, currentWeek);

        if (model.Courses != null)
        {
            foreach (var course in model.Courses)
            {
                if (course.Entries == null) continue;
                foreach (var entry in course.Entries)
                {
                    if (!WeekExpression.TryParse(entry.Weeks, out var set, out _) || set.All) continue;
                    last = Math.Max(last, HighestWeek(set));
                }
            }
        }

        return Math.Clamp(last, 1, TimetableLimits.MaxWeeks);
    }

    /// <summary>Highest 1-based week present in a rule mask (0 for an empty set).</summary>
    private static int HighestWeek(WeekSet set)
    {
        var mask = set.Mask;
        var week = 0;
        while (mask != 0)
        {
            week++;
            mask >>= 1;
        }
        return week;
    }

    // ---------- Week navigation ----------

    private void OnPreviousWeekClick(object? sender, RoutedEventArgs e) => StepWeek(-1);

    private void OnNextWeekClick(object? sender, RoutedEventArgs e) => StepWeek(1);

    private void OnBackToCurrentWeekClick(object? sender, RoutedEventArgs e)
    {
        if (currentWeek < 1) return;
        showAllWeeks = false;
        displayWeek = Math.Clamp(currentWeek, 1, lastWeek);
        RefreshWeekView(DateTime.Now);
    }

    private void OnWeekModeClick(object? sender, RoutedEventArgs e)
    {
        showAllWeeks = !showAllWeeks;

        if (!showAllWeeks)
        {
            // Leaving the whole-term view returns to a real week: the current one when the term
            // date is known, otherwise whatever week the panel last showed.
            displayWeek = currentWeek >= 1
                ? Math.Clamp(currentWeek, 1, lastWeek)
                : Math.Clamp(displayWeek, 1, lastWeek);
        }

        RefreshWeekView(DateTime.Now);
    }

    private void StepWeek(int delta)
    {
        var target = displayWeek + delta;
        if (target < 1 || target > lastWeek) return;

        showAllWeeks = false;
        displayWeek = target;
        RefreshWeekView(DateTime.Now);
    }

    /// <summary>The complete week grid at fixed readable metrics (the body scrolls when tall).</summary>
    private void BuildGrid(TimetableRuntime runtime, int week, DateTime now)
    {
        PanelGridHost.Children.Clear();
        PanelGridHost.ColumnDefinitions.Clear();
        PanelGridHost.RowDefinitions.Clear();

        var today = DayIndex(now.DayOfWeek);
        var nowTime = TimeOnly.FromDateTime(now);
        // The live-lesson outline only means something on the real current week: browsing week 3
        // (or the whole term, week 0) must not highlight a block just because its time matches.
        var highlightCurrent = week >= 1 && week == currentWeek;
        var dayCount = model.ShowWeekend ? 7 : 5;
        var rowCount = Math.Max(1, runtime.MaxPeriod);

        const double labelW = 46;
        const double headerH = 24;
        const double rowH = 32;

        PanelGridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelW, GridUnitType.Pixel) });
        for (var i = 0; i < dayCount; i++)
            PanelGridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        PanelGridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerH, GridUnitType.Pixel) });
        for (var i = 0; i < rowCount; i++)
            PanelGridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowH, GridUnitType.Pixel) });

        var accent = ThemeScopeResolver.ResolveBrush(this, "SystemAccentColor", ActualThemeVariant,
            new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)));
        var paint = ScheduleBlocks.ResolveBlockPaint(this, ActualThemeVariant);

        // Day header.
        for (var dayIdx = 0; dayIdx < dayCount; dayIdx++)
        {
            var day = dayIdx + 1;
            var head = new TextBlock
            {
                Text = TimetableClock.DayShortName(day),
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (day == today)
            {
                head.Foreground = accent;
                head.FontWeight = FontWeight.SemiBold;
            }
            else
            {
                head.Opacity = 0.75;
            }
            Grid.SetRow(head, 0);
            Grid.SetColumn(head, dayIdx + 1);
            PanelGridHost.Children.Add(head);
        }

        // Period labels + slot backgrounds.
        for (var r = 0; r < rowCount; r++)
        {
            var period = r + 1;
            var label = new TextBlock
            {
                Text = TimetableClock.PeriodShortLabel(model, period),
                FontSize = 11,
                Opacity = 0.7,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(label, r + 1);
            Grid.SetColumn(label, 0);
            PanelGridHost.Children.Add(label);

            for (var dayIdx = 0; dayIdx < dayCount; dayIdx++)
            {
                var cell = new Border
                {
                    Background = ScheduleBlocks.LaneBrush,
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(1),
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, dayIdx + 1);
                PanelGridHost.Children.Add(cell);
            }
        }

        // Course blocks.
        var usedPeriods = new List<int>(rowCount);
        for (var p = 1; p <= rowCount; p++) usedPeriods.Add(p);

        for (var dayIdx = 0; dayIdx < dayCount; dayIdx++)
        {
            var day = dayIdx + 1;
            var placements = ScheduleGridRenderer.LayoutDay(runtime, day, week, usedPeriods);
            foreach (var group in placements.GroupBy(p => p.GroupId))
            {
                var items = group.ToList();
                var first = items[0];
                var row = first.RowIndex + 1;
                var span = first.RowSpan;

                if (first.LaneCount == 1)
                {
                    var block = ScheduleBlocks.Create(first.Slot, 12, 10, showSub: true,
                        model.ShowRoom, model.ShowTeacher, paint);
                    if (highlightCurrent)
                        HighlightCurrent(block, first.Slot, day, today, nowTime, accent);
                    Grid.SetRow(block, row);
                    Grid.SetRowSpan(block, span);
                    Grid.SetColumn(block, dayIdx + 1);
                    PanelGridHost.Children.Add(block);
                }
                else
                {
                    var container = new Grid();
                    for (var i = 0; i < first.LaneCount; i++)
                        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    foreach (var item in items)
                    {
                        var block = ScheduleBlocks.Create(item.Slot, 12, 10, showSub: true,
                            model.ShowRoom, model.ShowTeacher, paint);
                        if (highlightCurrent)
                            HighlightCurrent(block, item.Slot, day, today, nowTime, accent);
                        Grid.SetColumn(block, item.Lane);
                        container.Children.Add(block);
                    }

                    Grid.SetRow(container, row);
                    Grid.SetRowSpan(container, span);
                    Grid.SetColumn(container, dayIdx + 1);
                    PanelGridHost.Children.Add(container);
                }
            }
        }
    }

    private void HighlightCurrent(Border block, TimetableRuntime.Slot slot, int day, int today, TimeOnly nowTime, IBrush accent)
    {
        if (day != today) return;

        var start = TimetableClock.PeriodBounds(model, slot.FirstPeriod).Start;
        var end = TimetableClock.PeriodBounds(model, slot.LastPeriod).End;
        if (start == null || end == null) return;

        if (nowTime >= start.Value && nowTime < end.Value)
        {
            block.BorderBrush = accent;
            block.BorderThickness = new Thickness(2);
        }
    }

    private void BuildNotes()
    {
        NotesPanel.Children.Clear();

        var any = false;
        if (model.Courses != null)
        {
            foreach (var course in model.Courses)
            {
                if (course.Note is not { Length: > 0 } note) continue;
                any = true;
                NotesPanel.Children.Add(ScheduleBlocks.CreateNoteRow(course.Name ?? "", note));
            }
        }

        NotesCard.IsVisible = any;
    }

    // ---------- Interaction ----------
    // The window declares no close button and no root drag/dismiss handler of its own any
    // more: the shared shell's close button, the backdrop press, Escape and the outside-click
    // hook all collapse into the base class's single animated close.

    private static int DayIndex(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;
}
