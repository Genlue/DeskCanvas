using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using Timetable.Locales;
using Timetable.Models;
using Timetable.Services;

namespace Timetable.Views.Settings;

/// <summary>
/// Settings page: JSON import (with the review step), the copyable AI prompt / format guide,
/// term configuration, per-period bell times and display toggles. Every change saves
/// immediately through the widget layout provider; the review window is the only path
/// that writes course data.
/// </summary>
public partial class TimetableSettings : UserControl
{
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private TimetableModel model;
    private bool initializing = true;

    private bool clearArmed;
    private DispatcherTimer? clearTimer;
    private DispatcherTimer? copiedTimer;

    public TimetableSettings() : this(null)
    {
    }

    public TimetableSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = ReadModel();

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        ApplyModelToControls();
        UpdateStatusText();
        initializing = false;
    }

    // ---------- Lifecycle ----------

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        TimetableStore.ModelChanged += OnStoreModelChanged;
        // The page can be re-shown later; always reflect the current stored model.
        model = ReadModel();
        ApplyModelToControls();
        UpdateStatusText();
        ResetClearButton();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        TimetableStore.ModelChanged -= OnStoreModelChanged;
        clearTimer?.Stop();
        copiedTimer?.Stop();
    }

    private void OnStoreModelChanged(TimetableModel newModel, object? sender)
    {
        if (ReferenceEquals(sender, this)) return;
        model = newModel;
        ApplyModelToControls();
        UpdateStatusText();
    }

    // ---------- Model plumbing ----------

    private TimetableModel ReadModel()
    {
        if (widgetLayoutProvider == null)
        {
            return new TimetableModel();
        }

        if (widgetLayoutProvider.GetType().Name != "WidgetLayoutProvider")
        {
            try
            {
                var layout = widgetLayoutProvider.Get();
                var modelFromLayout = layout?.GetModel<TimetableModel>();
                if (modelFromLayout != null)
                    return modelFromLayout.Normalize();
            }
            catch { }
        }

        var storeModel = TimetableStore.Get();
        if (storeModel.HasData)
            return storeModel;

        try
        {
            var layout = widgetLayoutProvider.Get();
            var modelFromLayout = layout?.GetModel<TimetableModel>();
            if (modelFromLayout != null && modelFromLayout.HasData)
                return modelFromLayout.Normalize();
        }
        catch { }

        return storeModel;
    }

    private void ApplyModelToControls()
    {
        initializing = true;
        try
        {
            StartDatePicker.SelectedDate = DateTime.TryParse(model.StartDate, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
                ? new DateTimeOffset(start.Date)
                : null;
            TotalWeeksBox.Value = model.TotalWeeks;
            CurrentWeekBox.Value = model.CurrentWeekOverride;
            ShowWeekendToggle.IsChecked = model.ShowWeekend;
            ShowRoomToggle.IsChecked = model.ShowRoom;
            ShowTeacherToggle.IsChecked = model.ShowTeacher;
            BuildPeriodRows();
        }
        finally
        {
            initializing = false;
        }
    }

    private void Save()
    {
        if (initializing) return;
        TimetableStore.Save(model, sender: this);
        if (widgetLayoutProvider == null) return;
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout == null) return;
            widgetLayoutProvider.Save(layout with { Settings = JsonSerializer.SerializeToElement(model) });
        }
        catch
        {
            // Saving must never crash the settings page.
        }
    }

    private void UpdateStatusText()
    {
        StatusText.Text = model.HasData
            ? Locale.Timetable_DataStatus
                .Replace("{0}", model.CourseCount.ToString(CultureInfo.InvariantCulture))
                .Replace("{1}", model.SlotCount.ToString(CultureInfo.InvariantCulture))
            : Locale.Timetable_NoData;
    }

    // ---------- Import flow ----------

    private async void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Locale.Timetable_Import,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("JSON")
                    {
                        Patterns = ["*.json", "*.txt"]
                    },
                    new FilePickerFileType(Locale.Timetable_AllFiles)
                    {
                        Patterns = ["*.*"]
                    }
                ]
            });

            if (files.Count == 0) return;
            if (files[0].Path.LocalPath is not { } path || !File.Exists(path)) return;

            var result = await TimetableImporter.ImportFileAsync(path);

            var owner = topLevel as Window;
            var review = new ImportReviewWindow(result);
            var confirmed = owner != null ? await review.ShowDialog<bool>(owner) : false;
            if (!confirmed || result.Model == null) return;

            ApplyImportedModel(result.Model);
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            StatusText.Text = Locale.Timetable_ImportFailed.Replace("{0}", ex.Message);
        }
    }

    /// <summary>
    /// Merge an approved import into the stored model: course data is replaced,
    /// display preferences and a manually pinned current week are kept.
    /// </summary>
    private void ApplyImportedModel(TimetableModel imported)
    {
        model = model with
        {
            Version = TimetableModel.CurrentVersion,
            Name = imported.Name,
            StartDate = imported.StartDate ?? model.StartDate,
            TotalWeeks = imported.TotalWeeks > 0 ? imported.TotalWeeks : model.TotalWeeks,
            Periods = imported.Periods,
            Courses = imported.Courses,
        };

        ApplyModelToControls();
        Save();
    }

    // ---------- Prompt / guide ----------

    private async void OnCopyPromptClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard == null) return;

        await topLevel.Clipboard.SetTextAsync(TimetableGuide.Prompt);

        CopyPromptButton.Content = Locale.Timetable_Copied;
        copiedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        copiedTimer.Stop();
        copiedTimer.Tick -= OnCopiedTimerTick;
        copiedTimer.Tick += OnCopiedTimerTick;
        copiedTimer.Start();
    }

    private void OnCopiedTimerTick(object? sender, EventArgs e)
    {
        copiedTimer?.Stop();
        ResetCopyButton();
    }

    private void ResetCopyButton()
    {
        if (CopyPromptButton != null) CopyPromptButton.Content = Locale.Timetable_Copy;
    }

    private void OnViewGuideClicked(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var guide = new GuideWindow();
        if (owner != null) _ = guide.ShowDialog(owner);
        else guide.Show();
    }

    // ---------- Clear ----------

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        if (!clearArmed)
        {
            clearArmed = true;
            ClearButton.Content = Locale.Timetable_ClearConfirm;
            clearTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            clearTimer.Stop();
            clearTimer.Tick -= OnClearTimerTick;
            clearTimer.Tick += OnClearTimerTick;
            clearTimer.Start();
            return;
        }

        ResetClearButton();
        model = model with
        {
            Name = null,
            StartDate = null,
            TotalWeeks = 0,
            Periods = [],
            Courses = [],
        };
        ApplyModelToControls();
        Save();
        UpdateStatusText();
    }

    private void OnClearTimerTick(object? sender, EventArgs e)
    {
        ResetClearButton();
    }

    private void ResetClearButton()
    {
        clearArmed = false;
        clearTimer?.Stop();
        if (ClearButton != null) ClearButton.Content = Locale.Timetable_Clear;
    }

    // ---------- Period times ----------

    /// <summary>Identifies one TimePicker as "start/end of period N" inside its Tag.</summary>
    private sealed record PeriodSlot(int Index, bool IsEnd);

    /// <summary>
    /// One collapsible period row: a compact "第 N 节  08:00 – 08:45" summary line whose
    /// start/end editors unfold on click and fold away again. Collapsed by default — nine
    /// permanently-open editors both flooded the page and let their pickers clip at every
    /// window width. The editor never clips: its units wrap onto a second line instead.
    /// </summary>
    private sealed class PeriodRowHandle
    {
        public required int Index { get; init; }
        public required Border Row { get; init; }
        public required WrapPanel Editor { get; init; }
        public required TextBlock Summary { get; init; }
        public required PathIcon Chevron { get; init; }
        public bool Expanded { get; set; }
    }

    private static readonly StreamGeometry ChevronDownGeometry =
        StreamGeometry.Parse("M12,15.5 L4,7.5 L6.1,5.4 L12,11.3 L17.9,5.4 L20,7.5 Z");

    private static readonly StreamGeometry ChevronUpGeometry =
        StreamGeometry.Parse("M12,8.5 L4,16.5 L6.1,18.6 L12,12.7 L17.9,18.6 L20,16.5 Z");

    /// <summary>Expanded row indices — kept across rebuilds so an import never closes the row being edited.</summary>
    private readonly HashSet<int> expandedPeriods = [];

    private readonly List<PeriodRowHandle> periodRowHandles = [];

    /// <summary>
    /// How many period rows to offer: enough to cover every period the imported schedule uses,
    /// every period the JSON already defines, and (before any import) a modest starter skeleton.
    /// </summary>
    private int ResolvePeriodRowCount()
    {
        var usedMax = 0;
        if (model.Courses != null)
        {
            foreach (var course in model.Courses)
            {
                if (course.Entries == null) continue;
                foreach (var entry in course.Entries)
                    if (entry.Periods is { Count: > 0 })
                        usedMax = Math.Max(usedMax, entry.Periods[^1]);
            }
        }

        var count = Math.Max(usedMax, model.Periods?.Count ?? 0);
        count = model.HasData ? Math.Max(count, 1) : Math.Max(count, 8);
        return Math.Clamp(count, 1, TimetableLimits.MaxPeriods);
    }

    /// <summary>
    /// One collapsible row per period. Values come from the stored model (an import with a
    /// periods block fills these automatically); edits save straight back.
    /// </summary>
    private void BuildPeriodRows()
    {
        PeriodsPanel.Children.Clear();
        periodRowHandles.Clear();

        var count = ResolvePeriodRowCount();
        for (var i = 0; i < count; i++)
        {
            var period = model.Periods is { } periods && i < periods.Count ? periods[i] : null;
            var handle = BuildPeriodRow(i, period);
            periodRowHandles.Add(handle);
            PeriodsPanel.Children.Add(handle.Row);
        }
    }

    private PeriodRowHandle BuildPeriodRow(int index, TimetablePeriod? period)
    {
        var label = new TextBlock
        {
            Text = Locale.Timetable_PeriodFull.Replace("{0}", (index + 1).ToString(CultureInfo.InvariantCulture)),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var summary = new TextBlock
        {
            Text = FormatSummary(period),
            FontSize = 12,
            Opacity = 0.62,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, summary },
        };

        var chevron = new PathIcon
        {
            Width = 12,
            Height = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Data = ChevronDownGeometry,
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = Brushes.Transparent, // hit-testable across the full row width
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        header.Children.Add(title);
        Grid.SetColumn(chevron, 1);
        header.Children.Add(chevron);

        // The editor: "开始 [picker]   结束 [picker]", wrapping to one unit per line when
        // the row is too narrow. Never a fixed-width strip that clips.
        var editor = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            IsVisible = false,
            Margin = new Thickness(0, 4, 0, 2),
        };
        editor.Children.Add(BuildTimeUnit(Locale.Timetable_PeriodStart,
            MakeTimePicker(period?.Start, new PeriodSlot(index, false))));
        editor.Children.Add(BuildTimeUnit(Locale.Timetable_PeriodEnd,
            MakeTimePicker(period?.End, new PeriodSlot(index, true))));

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(editor);

        var row = new Border
        {
            Classes = { "period-row" },
            Child = content,
        };

        var handle = new PeriodRowHandle
        {
            Index = index,
            Row = row,
            Editor = editor,
            Summary = summary,
            Chevron = chevron,
        };

        header.PointerPressed += (_, e) =>
        {
            TogglePeriodRow(handle);
            e.Handled = true;
        };

        SetPeriodRowExpanded(handle, expandedPeriods.Contains(index));
        return handle;
    }

    private static Control BuildTimeUnit(string caption, TimePicker picker)
    {
        var unit = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        unit.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        unit.Children.Add(picker);
        return unit;
    }

    private void TogglePeriodRow(PeriodRowHandle handle) => SetPeriodRowExpanded(handle, !handle.Expanded);

    private void SetPeriodRowExpanded(PeriodRowHandle handle, bool expanded)
    {
        handle.Expanded = expanded;
        handle.Editor.IsVisible = expanded;
        handle.Chevron.Data = expanded ? ChevronUpGeometry : ChevronDownGeometry;
        if (expanded) expandedPeriods.Add(handle.Index);
        else expandedPeriods.Remove(handle.Index);
    }

    /// <summary>"08:00 – 08:45", a one-sided time, or the "not set" caption.</summary>
    private static string FormatSummary(TimetablePeriod? period)
    {
        var start = period?.Start;
        var end = period?.End;
        var hasStart = !string.IsNullOrWhiteSpace(start);
        var hasEnd = !string.IsNullOrWhiteSpace(end);

        if (!hasStart && !hasEnd) return Locale.Timetable_PeriodNotSet;
        if (hasStart && hasEnd) return $"{start} – {end}";
        return hasStart ? $"{start} –" : $"– {end}";
    }

    /// <summary>Re-reads one row's summary from the model after an edit.</summary>
    private void RefreshPeriodSummary(int index)
    {
        if (index < 0 || index >= periodRowHandles.Count) return;
        var period = model.Periods is { } periods && index < periods.Count ? periods[index] : null;
        periodRowHandles[index].Summary.Text = FormatSummary(period);
    }

    private TimePicker MakeTimePicker(string? value, PeriodSlot slot)
    {
        var picker = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            FontSize = 12,
            // MinWidth, not Width: the full face — "hour"/"minute" placeholders AND the
            // numeric one — lays out at ≈242 DIP naturally, and the old fixed 126 clipped
            // it down to bare fragments ("hour" → "our", digits → shreds, exactly the
            // user-reported screenshot). 200 keeps every face legible while staying
            // narrower than an open editor row; a wider locale grows it naturally.
            MinWidth = 200,
            Tag = slot,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        if (ParseTime(value) is { } time) picker.SelectedTime = time;
        picker.SelectedTimeChanged += OnPeriodTimeChanged;
        return picker;
    }

    private void OnPeriodTimeChanged(object? sender, TimePickerSelectedValueChangedEventArgs e)
    {
        if (initializing) return;
        if (sender is not TimePicker { Tag: PeriodSlot slot }) return;

        var periods = new List<TimetablePeriod>(model.Periods ?? []);
        while (periods.Count <= slot.Index) periods.Add(new TimetablePeriod());

        var current = periods[slot.Index];
        periods[slot.Index] = new TimetablePeriod(
            current.Label,
            slot.IsEnd ? current.Start : FormatTime(e.NewTime),
            slot.IsEnd ? FormatTime(e.NewTime) : current.End);

        model = model with { Periods = periods };
        RefreshPeriodSummary(slot.Index);
        Save();
    }

    private static string? FormatTime(TimeSpan? value) =>
        value is { } time ? $"{time.Hours:00}:{time.Minutes:00}" : null;

    private static TimeSpan? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var time) ? time : null;
    }

    // ---------- Term / display ----------

    private void OnStartDateChanged(object? sender, DatePickerSelectedValueChangedEventArgs e)
    {
        if (initializing) return;
        model = model with
        {
            StartDate = StartDatePicker.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        Save();
    }

    private void OnTotalWeeksChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (initializing) return;
        model = model with { TotalWeeks = (int)Math.Clamp(TotalWeeksBox.Value ?? 0, 0, TimetableLimits.MaxWeeks) };
        Save();
    }

    private void OnCurrentWeekChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (initializing) return;
        model = model with { CurrentWeekOverride = (int)Math.Clamp(CurrentWeekBox.Value ?? 0, 0, TimetableLimits.MaxWeeks) };
        Save();
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (initializing) return;
        model = model with
        {
            ShowWeekend = ShowWeekendToggle.IsChecked ?? true,
            ShowRoom = ShowRoomToggle.IsChecked ?? true,
            ShowTeacher = ShowTeacherToggle.IsChecked ?? false,
        };
        Save();
    }
}
