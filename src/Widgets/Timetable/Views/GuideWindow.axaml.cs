using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeskCanvas.Services;
using Timetable.Locales;
using Timetable.Services;

namespace Timetable.Views;

/// <summary>
/// Read-only viewer for the embedded JSON format guide, with a one-click copy of the
/// AI prompt (the marked section of the guide).
/// </summary>
public partial class GuideWindow : Window
{
    private DispatcherTimer? copiedTimer;

    public GuideWindow()
    {
        InitializeComponent();
        WindowRoles.Register(this, WindowRole.Dialog);

        TitleText.Text = Locale.Timetable_Guide_Title;
        HintText.Text = Locale.Timetable_Guide_Hint;
        CopyPromptButton.Content = Locale.Timetable_CopyPrompt;
        CloseButton.Content = Locale.Timetable_Close;
        GuideText.Text = FormatForDisplay(TimetableGuide.FullText);

        Closed += (_, _) => copiedTimer?.Stop();
    }

    /// <summary>The prompt markers are functional; show them as readable dividers instead of raw comments.</summary>
    private static string FormatForDisplay(string text) => text
        .Replace("<!-- AI-PROMPT:START -->", "———【AI 提示词 · 开始】以下内容 = 「复制 AI 提示词」按钮复制的内容 ———")
        .Replace("<!-- AI-PROMPT:END -->", "———【AI 提示词 · 结束】———");

    private async void OnCopyPromptClicked(object? sender, RoutedEventArgs e)
    {
        if (Clipboard == null) return;

        await Clipboard.SetTextAsync(TimetableGuide.Prompt);
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
        CopyPromptButton.Content = Locale.Timetable_CopyPrompt;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
