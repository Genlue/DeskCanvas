using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tools.Models;
using Tools.Services;
using Tools.Services.Translation;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace Tools.Views;

/// <summary>
/// 翻译组件的二级面板（大窗翻译）：the shared <see cref="SecondaryPanelWindow"/> host plus the
/// shared <see cref="DeskCanvas.Views.Controls.SecondaryPanelShell"/> own the card, the glass,
/// the header chrome, the close path and the open/close transitions (the shell's material comes
/// from <c>PanelAppearanceResolver</c> via the base-class binder). This class keeps only the
/// business behaviour: text input, the async translation, the language/engine selection and the
/// sync back into the opening TranslatorView, so panel and widget stay in sync for the panel's
/// whole lifetime.
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

        UpdateCharCount();
        UpdateComboSelection();

        Closed += OnWindowClosed;
    }

    public static void ShowPopup(Point? screenCenter, Window? owner = null, double? cornerRadius = null, TranslatorView? ownerView = null)
    {
        // PerOwner identity (P8 batch F): every translator widget owns its own panel, and the
        // ownerView sync (preferences/result hand-back) stays per pair. The service handles
        // the cooldown, the toggle-close and the pinned show (the decision mirrors the old
        // early returns and is deliberately ignored).
        PanelService.Open(new PanelRequest(
            owner is DeskCanvas.Views.Widget widget ? widget.InstanceId : default,
            owner,
            "tools.translator",
            PanelScope.PerOwner,
            () => new TranslatorPopupWindow(screenCenter, cornerRadius, ownerView)));
    }

    protected override void OnPanelLoaded()
    {
        // Initialization focus stays with the business window (the glass render request and the
        // open animation are handled by the base class).
        InputBox.Focus();
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

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    private void UpdateCharCount()
    {
        var len = (InputBox.Text ?? string.Empty).Length;
        CharCountText.Text = IsZh ? $"{len} 字符" : $"{len} chars";
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
        StatusText.Text = IsZh ? "请求中..." : "Requesting...";

        try
        {
            TranslationService.Instance.Configure(model);
            var result = await TranslationService.Instance.TranslateAsync(text, sourceLang, targetLang, engine, token);
            if (!token.IsCancellationRequested)
            {
                OutputBox.Text = result;
                StatusText.Text = IsZh ? $"完成 ({engine})" : $"Done ({engine})";
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
                StatusText.Text = IsZh ? "翻译失败" : "Translation failed";
                OutputBox.Text = IsZh ? $"[错误] {ex.Message}" : $"[Error] {ex.Message}";
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
            StatusText.Text = IsZh ? "已复制原文" : "Source text copied";
        }
    }

    private void OnCopyOutputClicked(object? sender, RoutedEventArgs e)
    {
        var result = OutputBox.Text;
        if (!string.IsNullOrWhiteSpace(result))
        {
            ClipboardNative.SetText(result);
            StatusText.Text = IsZh ? "已复制译文" : "Translation copied";
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
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
