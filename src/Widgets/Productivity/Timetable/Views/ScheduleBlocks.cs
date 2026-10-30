using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using DeskCanvas.Services;
using Timetable.Locales;
using Timetable.Models;
using Timetable.Services;

namespace Timetable.Views;

/// <summary>
/// Shared course-block visual + tooltip for the widget view, the import-review preview and the
/// full-schedule panel. A block is a solid course-colour card whose text uses a computed
/// contrast colour — readable on every surface without relying on grey captions.
/// </summary>
internal static class ScheduleBlocks
{
    /// <summary>Subtle background for empty grid slots (reads correctly in light and dark themes).</summary>
    public static readonly IBrush LaneBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));

    public static Border Create(TimetableRuntime.Slot slot, double titleFont, double subFont, bool showSub,
        bool showRoom, bool showTeacher, CoursePalette.BlockPaint paint = default)
    {
        var (background, foreground) = CoursePalette.Paint(slot.Course, paint);

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 1,
        };
        stack.Children.Add(new TextBlock
        {
            Text = slot.Course.Name ?? "",
            FontSize = titleFont,
            FontWeight = FontWeight.SemiBold,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        if (showSub && ComposeInfoLine(slot.Course, showRoom, showTeacher) is { } info)
        {
            stack.Children.Add(new TextBlock
            {
                Text = info,
                FontSize = subFont,
                Foreground = foreground,
                Opacity = 0.88,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        var border = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(1),
            Padding = new Thickness(2, 2, 2, 2),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = stack,
        };

        ToolTip.SetTip(border, BuildTooltip(slot));
        return border;
    }

    /// <summary>Resolve the monochrome block paint for a host window; colorful themes get the default.</summary>
    public static CoursePalette.BlockPaint ResolveBlockPaint(Visual host, ThemeVariant variant)
    {
        var theme = PanelAppearanceResolver.ResolveEffectiveTheme(
            PanelAppearanceResolver.TryResolveSettingsProvider(),
            TopLevel.GetTopLevel(host) as Window);
        if (theme is not { IsColorful: false, Monochrome: true })
            return default;

        var accent = ThemeScopeResolver.ResolveColor(host, "SystemControlForegroundAccentBrush", variant,
            Color.FromRgb(0x00, 0x78, 0xD4));
        var foreground = ThemeScopeResolver.ResolveBrush(host, "SystemControlForegroundBaseHighBrush", variant,
            Brushes.White);
        return new CoursePalette.BlockPaint(true, accent, foreground);
    }

    /// <summary>Room / teacher line per the widget's display settings; null when both are off or empty.</summary>
    public static string? ComposeInfoLine(TimetableCourse course, bool showRoom, bool showTeacher)
    {
        var parts = new List<string>(2);
        if (showRoom && course.Room is { Length: > 0 } room) parts.Add(room);
        if (showTeacher && course.Teacher is { Length: > 0 } teacher) parts.Add(teacher);
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>Full tooltip: day · periods · weeks (+ the course's note on a second line).</summary>
    public static string BuildTooltip(TimetableRuntime.Slot slot)
    {
        var span = slot.FirstPeriod == slot.LastPeriod
            ? slot.FirstPeriod.ToString()
            : $"{slot.FirstPeriod}-{slot.LastPeriod}";
        var parts = new List<string>(3)
        {
            TimetableClock.DayName(slot.Entry.Day),
            Locale.Timetable_PeriodFull.Replace("{0}", span),
        };
        if (slot.Entry.Weeks is { Length: > 0 } weeks) parts.Add(weeks);

        var text = $"{slot.Course.Name}｜{string.Join(" · ", parts)}";
        if (slot.Course.Note is { Length: > 0 } note) text += "\n" + note;
        return text;
    }

    /// <summary>"《课程》 备注" row used by the import-review window and the full-schedule panel.</summary>
    public static Control CreateNoteRow(string name, string note)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        var title = new TextBlock
        {
            Text = $"《{name}》",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        grid.Children.Add(title);

        var body = new TextBlock
        {
            Text = note,
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        return grid;
    }
}
