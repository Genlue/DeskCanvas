using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Tools.Models;
using Tools.Services;
using Tools.Services.Translation;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace Tools.Views;

/// <summary>
/// 翻译组件的二级面板（大窗翻译）：reuses the shared <see cref="SecondaryPanelWindow"/>
/// host plumbing (spawn-from-widget zoom, glass, escape/focus dismissal, single instance)
/// exactly like ClipboardPopupWindow. State is captured from the opening TranslatorView and
/// every translation / preference change is pushed back into it, so panel and widget stay
/// in sync for the panel's whole lifetime.
/// </summary>
public partial class TranslatorPopupWindow : SecondaryPanelWindow
{
    private readonly TranslatorView? ownerView;
    private readonly Point? spawnScreenCenter;
    private TranslatorModel model;
    private string sourceLang;
    private string targetLang;
    private string engine;
    private CancellationTokenSource? cts;

    protected override Visual? PanelCard => CardBorder;
    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public TranslatorPopupWindow() : this(null) { }

    public TranslatorPopupWindow(Point? screenCenter = null, double? cornerRadius = null, TranslatorView? ownerView = null)
    {
        this.ownerView = ownerView;
        spawnScreenCenter = screenCenter;

        // Capture the widget's live editing session before building the UI.
        if (ownerView != null)
        {
            model = ownerView.PanelModel;
            (sourceLang, targetLang, engine) = ownerView.PanelPreferences;
        }
        else
        {
            model = new TranslatorModel();
            sourceLang = model.SourceLanguage;
            targetLang = model.TargetLanguage;
            engine = model.Engine;
        }

        InitializeComponent();
        InitializePanel(cornerRadius);

        InputBox.Text = ownerView?.PanelInput ?? string.Empty;
        OutputBox.Text = ownerView?.PanelOutput ?? string.Empty;
        TranslationService.Instance.Configure(model);

        ApplyTheme();
        UpdateCharCount();
        UpdateComboSelection();

        Closed += OnWindowClosed;
    }

    public static void ShowPopup(Point? screenCenter, Window? owner = null, double? cornerRadius = null, TranslatorView? ownerView = null)
    {
        if (PanelCoolingDown<TranslatorPopupWindow>())
            return;

        if (TryCloseActivePanel<TranslatorPopupWindow>())
            return;

        var popup = new TranslatorPopupWindow(screenCenter, cornerRadius, ownerView);
        popup.ShowAsSecondaryPanel(owner);
    }

    protected override void OnPanelLoaded()
    {
        if (LiquidGlassSurfaceControl.IsVisible)
        {
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
        InputBox.Focus();
    }

    private void ApplyTheme()
    {
        Theme theme;
        try
        {
            theme = new AppSettingsProvider().Get().Theme;
        }
        catch
        {
            theme = new Theme(DarkMode: true, AccentColor: null, OpacityLevel: 0.8, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter");
        }

        // Colour mode "follow system" is DarkMode == null, which must resolve to the *live*
        // variant — the same single source of truth the widget card and the theme preview use.
        bool isDark = theme.DarkMode ?? ActualThemeVariant == ThemeVariant.Dark;

        if (theme.UsesRenderedGlass)
        {
            // Rendered glass always goes through the live surface — the very same path the
            // primary widget card uses. The surface re-parameterises its optics for the
            // panel's open/close zoom (SetAnimationFrameScale), so the animation shows real
            // glass per tick whether or not live sampling is on.
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            LiquidGlassSurfaceControl.Material = theme;
            LiquidGlassSurfaceControl.CornerRadius = CardBorder.CornerRadius;
            LiquidGlassSurfaceControl.IsVisible = true;
            CardBorder.Background = Brushes.Transparent;
            CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
        else if (theme.IsColorful)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            CardBorder.Background = new SolidColorBrush(isDark ? Color.Parse("#1C1C1E") : Color.Parse("#FFFFFF"));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        }
        else if (theme.EffectiveSurface == SurfaceStyle.Solid)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            var hex = isDark ? theme.EffectiveSolidBackgroundDark : theme.EffectiveSolidBackgroundLight;
            var baseColor = Color.TryParse(hex, out var parsed) ? parsed : (isDark ? Color.FromRgb(46, 46, 46) : Colors.White);
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 255), 40, 255);
            CardBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        }
        else // Acrylic
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 220), 40, 240);
            CardBorder.Background = new SolidColorBrush(isDark ? Color.FromArgb(alpha, 28, 28, 32) : Color.FromArgb(alpha, 245, 245, 248));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(35, 0, 0, 0));
        }

        IBrush subTextBrush = isDark ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        CloseButton.Foreground = subTextBrush;
    }

    private void UpdateComboSelection()
    {
        SourceCombo.ItemsSource = TranslatorView.Languages;
        TargetCombo.ItemsSource = TranslatorView.Languages.Where(l => l.Code != "auto").ToList();
        EngineCombo.ItemsSource = TranslatorView.Engines;

        SourceCombo.SelectedItem = TranslatorView.Languages.FirstOrDefault(l => l.Code.Equals(sourceLang, StringComparison.OrdinalIgnoreCase)) ?? TranslatorView.Languages[0];
        TargetCombo.SelectedItem = TranslatorView.Languages.Where(l => l.Code != "auto").FirstOrDefault(l => l.Code.Equals(targetLang, StringComparison.OrdinalIgnoreCase))
                                   ?? TranslatorView.Languages.First(l => l.Code == "zh");
        EngineCombo.SelectedItem = TranslatorView.Engines.FirstOrDefault(e => e.Code.Equals(engine, StringComparison.OrdinalIgnoreCase)) ?? TranslatorView.Engines[0];
    }

    private void UpdateCharCount()
    {
        CharCountText.Text = $"{(InputBox.Text ?? string.Empty).Length} 字符";
    }

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateCharCount();
    }

    private void OnInputKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = ExecuteTranslateAsync();
        }
    }

    private void OnSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SourceCombo.SelectedItem is TranslatorView.LangOption lang)
        {
            sourceLang = lang.Code;
            SyncPreferencesToWidget();
            TriggerTranslationIfHasText();
        }
    }

    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TargetCombo.SelectedItem is TranslatorView.LangOption lang)
        {
            targetLang = lang.Code;
            SyncPreferencesToWidget();
            TriggerTranslationIfHasText();
        }
    }

    private void OnEngineChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (EngineCombo.SelectedItem is TranslatorView.EngineOption eng)
        {
            engine = eng.Code;
            SyncPreferencesToWidget();
            TriggerTranslationIfHasText();
        }
    }

    private void OnTranslateClicked(object? sender, RoutedEventArgs e)
    {
        _ = ExecuteTranslateAsync();
    }

    private void OnSwapClicked(object? sender, RoutedEventArgs e)
    {
        // Same auto-detection rule as the widget: "auto" cannot become a target, so it
        // resolves to the mirror pair of the current target.
        var newSource = targetLang;
        var newTarget = sourceLang == "auto"
            ? (targetLang == "zh" ? "en" : "zh")
            : sourceLang;

        sourceLang = newSource;
        targetLang = newTarget;
        UpdateComboSelection();
        SyncPreferencesToWidget();
        _ = ExecuteTranslateAsync();
    }

    private void SyncPreferencesToWidget()
    {
        ownerView?.ApplyPanelPreferences(sourceLang, targetLang, engine);
    }

    private void TriggerTranslationIfHasText()
    {
        if (!string.IsNullOrWhiteSpace(InputBox.Text))
        {
            _ = ExecuteTranslateAsync();
        }
    }

    private async Task ExecuteTranslateAsync()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        cts?.Cancel();
        // Every attempt replaces the previous source; disposing it releases the handles it owns.
        cts?.Dispose();
        var requestCts = new CancellationTokenSource();
        cts = requestCts;
        var token = requestCts.Token;

        TranslateButton.IsEnabled = false;
        StatusText.Text = "请求中...";

        try
        {
            TranslationService.Instance.Configure(model);
            var result = await TranslationService.Instance.TranslateAsync(text, sourceLang, targetLang, engine, token);
            if (!token.IsCancellationRequested)
            {
                OutputBox.Text = result;
                StatusText.Text = $"完成 ({engine})";
                ownerView?.ApplyPanelResult(InputBox.Text ?? string.Empty, result);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                StatusText.Text = "翻译失败";
                OutputBox.Text = $"[错误] {ex.Message}";
            }
        }
        finally
        {
            TranslateButton.IsEnabled = true;
            requestCts.Dispose();
            if (ReferenceEquals(cts, requestCts)) cts = null;
        }
    }

    private void OnClearInputClicked(object? sender, RoutedEventArgs e)
    {
        InputBox.Text = string.Empty;
        OutputBox.Text = string.Empty;
        StatusText.Text = string.Empty;
    }

    private void OnCopyInputClicked(object? sender, RoutedEventArgs e)
    {
        var text = InputBox.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            ClipboardNative.SetText(text);
            StatusText.Text = "已复制原文";
        }
    }

    private void OnCopyOutputClicked(object? sender, RoutedEventArgs e)
    {
        var result = OutputBox.Text;
        if (!string.IsNullOrWhiteSpace(result))
        {
            ClipboardNative.SetText(result);
            StatusText.Text = "已复制译文";
        }
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        LiquidGlassSurfaceControl.IsVisible = false;
        cts?.Cancel();

        // Hand the final editing session back to the widget so both stay consistent.
        if (ownerView != null)
        {
            ownerView.ApplyPanelPreferences(sourceLang, targetLang, engine);
            var input = InputBox.Text ?? string.Empty;
            var output = OutputBox.Text ?? string.Empty;
            if (!string.IsNullOrEmpty(input) || !string.IsNullOrEmpty(output))
            {
                ownerView.ApplyPanelResult(input, output);
            }
        }
    }
}
