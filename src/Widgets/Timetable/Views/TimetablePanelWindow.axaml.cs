using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Timetable.Locales;
using Timetable.Models;
using Timetable.Services;

namespace Timetable.Views;

/// <summary>
/// The full-schedule secondary panel: opened by double-clicking the widget, it renders the
/// complete week grid on a large, readable surface (plus the course notes), spawns from the
/// widget and is dismissed by Escape / an outside click like every other panel.
/// <para>
/// Memory contract: the panel is a short-lived window; it holds only the model snapshot and
/// the controls it builds (bounded by the import limits) and installs no timers — the current
/// lesson outline is computed once when the panel opens.
/// </para>
/// <para>
/// W8 migration: the card, the glass surface, the title chrome and the close button all live in
/// the shared <see cref="SecondaryPanelShell"/> now — the window declares only business content
/// (the week grid and the notes). The five-branch material ApplyTheme, the root drag handler and
/// the per-open AppSettingsProvider are gone: <see cref="SecondaryPanelWindow"/> plus
/// <see cref="DeskCanvas.Services.PanelAppearanceResolver"/> own the material and the transitions.
/// </para>
/// </summary>
public partial class TimetablePanelWindow : SecondaryPanelWindow
{
    private readonly Point? spawnScreenCenter;
    private readonly TimetableModel model;

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
    }

    // ---------- Content ----------

    private void BuildContent()
    {
        var now = DateTime.Now;
        var week = TimetableClock.ComputeWeek(model, now);

        WeekBadgeText.Text = week >= 1
            ? Locale.Timetable_Week.Replace("{0}", week.ToString())
            : Locale.Timetable_UnknownWeek;

        BuildGrid(TimetableRuntime.Build(model), week, now);
        BuildNotes();
    }

    /// <summary>The complete week grid at fixed readable metrics (the body scrolls when tall).</summary>
    private void BuildGrid(TimetableRuntime runtime, int week, DateTime now)
    {
        PanelGridHost.Children.Clear();
        PanelGridHost.ColumnDefinitions.Clear();
        PanelGridHost.RowDefinitions.Clear();

        var today = DayIndex(now.DayOfWeek);
        var nowTime = TimeOnly.FromDateTime(now);
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
                        model.ShowRoom, model.ShowTeacher);
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
                            model.ShowRoom, model.ShowTeacher);
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
