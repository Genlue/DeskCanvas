using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Timetable.Locales;
using Timetable.Models;
using Timetable.Services;

namespace Timetable.Views;

/// <summary>
/// The mandatory second check of the import flow: it renders the parsed schedule as a
/// 星期 × 节次 preview (compared with the original image by the user) plus a grouped
/// issue list. Course data is written by the caller only after <c>ShowDialog</c> returns true.
/// </summary>
public partial class ImportReviewWindow : Window
{
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E24B4A"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#B9770E"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#888780"));

    private readonly ImportResult result;

    public ImportReviewWindow() : this(new ImportResult())
    {
    }

    public ImportReviewWindow(ImportResult result)
    {
        this.result = result;
        InitializeComponent();
        ApplyLocalization();
        Populate();
    }

    private void ApplyLocalization()
    {
        TitleText.Text = Locale.Timetable_Review_Title;
        CancelButton.Content = Locale.Timetable_Cancel;
        ConfirmButton.Content = Locale.Timetable_Review_Confirm;
        HintText.Text = Locale.Timetable_Review_Hint;
        PreviewLabel.Text = Locale.Timetable_Review_Title;
        NotesHeader.Text = Locale.Timetable_Review_Notes;
    }

    private void Populate()
    {
        var model = result.Model;

        if (model != null)
        {
            SummaryText.Text = Locale.Timetable_Review_Summary
                .Replace("{0}", result.CourseCount.ToString())
                .Replace("{1}", result.SlotCount.ToString());

            StartDateText.Text = model.StartDate is { } date
                ? Locale.Timetable_Review_StartDate.Replace("{0}", date)
                : Locale.Timetable_Review_StartDateUnknown;

            var hasTimes = TimetableClock.HasBellTimes(model, TimetableRuntime.Build(model).MaxPeriod);
            TimesText.IsVisible = !hasTimes;
            TimesText.Text = hasTimes ? "" : Locale.Timetable_Review_PeriodsUnknown;

            ConfirmButton.IsEnabled = !result.HasErrors;
        }
        else
        {
            // File-level failure: only the issue list is meaningful.
            SummaryText.IsVisible = false;
            StartDateText.IsVisible = false;
            TimesText.IsVisible = false;
            ConfirmButton.IsVisible = false;
            PreviewLabel.IsVisible = false;
            PreviewBorder.IsVisible = false;
        }

        BuildIssues();
        BuildNotes(model);
        BuildPreview(model);
    }

    // ---------- Notes ----------

    private void BuildNotes(TimetableModel? model)
    {
        NotesPanel.Children.Clear();

        var any = false;
        if (model?.Courses != null)
        {
            foreach (var course in model.Courses)
            {
                if (course.Note is not { Length: > 0 } note) continue;
                any = true;
                NotesPanel.Children.Add(ScheduleBlocks.CreateNoteRow(course.Name ?? "", note));
            }
        }

        NotesBorder.IsVisible = any;
    }

    // ---------- Issues ----------

    private void BuildIssues()
    {
        IssuesPanel.Children.Clear();

        if (result.Issues.Count == 0)
        {
            IssuesPanel.Children.Add(MakeIssueRow(Locale.Timetable_Review_NoIssues, null));
            return;
        }

        var hasErrors = false;
        var hasWarnings = false;
        foreach (var issue in result.Issues)
        {
            if (issue.Severity == ImportSeverity.Error) hasErrors = true;
            else if (issue.Severity == ImportSeverity.Warning) hasWarnings = true;
        }

        if (hasErrors)
        {
            IssuesPanel.Children.Add(MakeHeaderRow(Locale.Timetable_Review_Errors, ErrorBrush));
            foreach (var issue in result.Issues)
                if (issue.Severity == ImportSeverity.Error)
                    IssuesPanel.Children.Add(MakeIssueRow(issue.Message, ErrorBrush));
        }

        if (hasWarnings)
        {
            IssuesPanel.Children.Add(MakeHeaderRow(Locale.Timetable_Review_Warnings, WarningBrush));
            foreach (var issue in result.Issues)
                if (issue.Severity == ImportSeverity.Warning)
                    IssuesPanel.Children.Add(MakeIssueRow(issue.Message, WarningBrush));
        }

        foreach (var issue in result.Issues)
            if (issue.Severity == ImportSeverity.Info)
                IssuesPanel.Children.Add(MakeIssueRow(issue.Message, InfoBrush));
    }

    private static Control MakeHeaderRow(string text, IBrush brush)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
    }

    private static Control MakeIssueRow(string text, IBrush? brush)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        var dot = new TextBlock
        {
            Text = "•",
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Top,
        };
        grid.Children.Add(dot);

        var body = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        return grid;
    }

    // ---------- Preview ----------

    private void BuildPreview(TimetableModel? model)
    {
        PreviewHost.Children.Clear();
        PreviewHost.ColumnDefinitions.Clear();
        PreviewHost.RowDefinitions.Clear();

        if (model is not { HasData: true }) return;

        var runtime = TimetableRuntime.Build(model);
        var rows = Math.Clamp(runtime.MaxPeriod, 1, 14);
        var usedPeriods = new List<int>(rows);
        for (var p = 1; p <= rows; p++) usedPeriods.Add(p);

        const double rowH = 24;
        const double labelW = 36;
        const double headerH = 20;

        PreviewHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelW, GridUnitType.Pixel) });
        for (var i = 0; i < 7; i++)
            PreviewHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        PreviewHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerH, GridUnitType.Pixel) });
        for (var i = 0; i < rows; i++)
            PreviewHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowH, GridUnitType.Pixel) });

        // Day headers.
        for (var day = 1; day <= 7; day++)
        {
            var head = new TextBlock
            {
                Text = TimetableClock.DayShortName(day),
                FontSize = 11,
                Opacity = 0.7,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(head, 0);
            Grid.SetColumn(head, day);
            PreviewHost.Children.Add(head);
        }

        // Period labels + lane backgrounds.
        for (var r = 0; r < rows; r++)
        {
            var label = new TextBlock
            {
                Text = TimetableClock.PeriodShortLabel(model, r + 1),
                FontSize = 10,
                Opacity = 0.6,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(label, r + 1);
            Grid.SetColumn(label, 0);
            PreviewHost.Children.Add(label);

            for (var day = 1; day <= 7; day++)
            {
                var cell = new Border { Background = ScheduleBlocks.LaneBrush, CornerRadius = new CornerRadius(3), Margin = new Thickness(0.5) };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, day);
                PreviewHost.Children.Add(cell);
            }
        }

        // Course blocks (all weeks shown — the preview mirrors the source image).
        for (var day = 1; day <= 7; day++)
        {
            var placements = ScheduleGridRenderer.LayoutDay(runtime, day, -1, usedPeriods);
            foreach (var group in placements.GroupBy(p => p.GroupId))
            {
                var items = group.ToList();
                var first = items[0];
                var row = first.RowIndex + 1;
                var span = first.RowSpan;
                var regionH = rowH * span;

                if (first.LaneCount == 1)
                {
                    var block = ScheduleBlocks.Create(first.Slot, 10.5, 9, showSub: regionH >= 30,
                        showRoom: true, showTeacher: true);
                    Grid.SetRow(block, row);
                    Grid.SetRowSpan(block, span);
                    Grid.SetColumn(block, day);
                    PreviewHost.Children.Add(block);
                }
                else
                {
                    var container = new Grid();
                    for (var i = 0; i < first.LaneCount; i++)
                        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    foreach (var item in items)
                    {
                        var block = ScheduleBlocks.Create(item.Slot, 10.5, 9, showSub: regionH >= 34,
                            showRoom: true, showTeacher: true);
                        Grid.SetColumn(block, item.Lane);
                        container.Children.Add(block);
                    }

                    Grid.SetRow(container, row);
                    Grid.SetRowSpan(container, span);
                    Grid.SetColumn(container, day);
                    PreviewHost.Children.Add(container);
                }
            }
        }
    }

    // ---------- Interaction ----------

    private void OnConfirmClicked(object? sender, RoutedEventArgs e) => Close(!result.HasErrors && result.Model != null);

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close(false);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
