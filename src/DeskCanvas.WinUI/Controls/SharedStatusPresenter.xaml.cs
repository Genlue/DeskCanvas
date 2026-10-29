using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SharedStatusRecord = DeskCanvas.UI.SharedStatus;
using SharedStatusKind = DeskCanvas.UI.SharedStatusKind;
using SharedStatusProducer = DeskCanvas.UI.SharedStatusPresenter;

namespace DeskCanvas.WinUI.Controls;

/// <summary>
/// 业务状态呈现控件（旧 <see cref="DeskCanvas.UI.SharedStatusPresenter"/> 的 WinUI 版，报告 §3 第二步第七行）。
/// <para>
/// 状态种类仍复用共享业务枚举（<see cref="SharedStatusKind"/>），文案由调用方注入：
/// 加载 → ProgressRing + 文本；空/就绪 → 文本；过期 → 警告 InfoBar；错误 → 错误 InfoBar。
/// “重试”是调用方的动作（<see cref="RetryRequested"/>），控件不猜怎么重试。
/// </para>
/// <para>
/// 可以直接把业务侧的无界面生产者接进来：<see cref="Attach"/> 订阅它的 Changed，
/// 并在控件离开可视树时自动退订（Loaded/Unloaded 对称，报告 §4.2 生命周期）。
/// </para>
/// </summary>
public partial class SharedStatusPresenter : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(SharedStatusRecord), typeof(SharedStatusPresenter),
        new PropertyMetadata(null, OnStatusChanged));

    /// <summary>“重试”按钮文案（由调用方从 Locale 注入）；为空时不显示重试按钮。</summary>
    public static readonly DependencyProperty RetryTextProperty = DependencyProperty.Register(
        nameof(RetryText), typeof(string), typeof(SharedStatusPresenter),
        new PropertyMetadata(string.Empty, OnRetryTextChanged));

    /// <summary>用户点了“重试”。</summary>
    public event EventHandler? RetryRequested;

    private SharedStatusProducer? _producer;
    private bool _subscribed;

    public SharedStatusPresenter()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Apply();
    }

    public SharedStatusRecord? Status
    {
        get => (SharedStatusRecord?)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public string RetryText
    {
        get => (string)GetValue(RetryTextProperty);
        set => SetValue(RetryTextProperty, value);
    }

    // ---- 便捷入口（与旧生产者的同名方法对齐） ------------------------------------------

    public void ShowLoading(string message) => Status = new SharedStatusRecord(SharedStatusKind.Loading, message);
    public void ShowReady(string message) => Status = new SharedStatusRecord(SharedStatusKind.Ready, message);
    public void ShowEmpty(string message) => Status = new SharedStatusRecord(SharedStatusKind.Empty, message);
    public void ShowStale(string message) => Status = new SharedStatusRecord(SharedStatusKind.Stale, message, true);
    public void ShowError(string message, bool canRetry = true) => Status = new SharedStatusRecord(SharedStatusKind.Error, message, canRetry);

    // ---- 业务生产者桥接 ---------------------------------------------------------------

    /// <summary>订阅一个业务侧状态生产者，并立刻显示它的当前状态。</summary>
    public void Attach(SharedStatusProducer producer)
    {
        ArgumentNullException.ThrowIfNull(producer);

        Unsubscribe();
        _producer = producer;
        if (IsLoaded) Subscribe();

        Status = producer.Current;
    }

    /// <summary>退订并断开生产者（页面卸载时调用）。</summary>
    public void Detach()
    {
        Unsubscribe();
        _producer = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Subscribe();

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Subscribe()
    {
        if (_producer is null || _subscribed) return;
        _producer.Changed += OnProducerChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (_producer is null || !_subscribed) return;
        _producer.Changed -= OnProducerChanged;
        _subscribed = false;
    }

    private void OnProducerChanged(object? sender, SharedStatusRecord status)
    {
        // 业务回调可能来自任意线程：显示必须回到控件自己的 UI 线程。

        if (DispatcherQueue.HasThreadAccess)
        {
            Status = status;
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => Status = status);
        }
    }

    // ---- 呈现 -------------------------------------------------------------------------

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SharedStatusPresenter)d).Apply();

    private static void OnRetryTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SharedStatusPresenter)d).Apply();

    private void Apply()
    {
        var status = Status;
        if (status is null)
        {
            RootPanel.Visibility = Visibility.Collapsed;
            StatusBar.IsOpen = false;
            return;
        }

        var message = status.Message ?? string.Empty;
        var canRetry = status.CanRetry && !string.IsNullOrEmpty(RetryText);

        RetryButton.Content = RetryText;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;

        switch (status.Kind)
        {
            case SharedStatusKind.Loading:
                ShowProgress(message, opacity: 0.85);
                break;

            case SharedStatusKind.Ready:
                // 就绪本身不需要横幅；没有文案就整块收起，避免占一行空白。

                if (string.IsNullOrEmpty(message))
                {
                    RootPanel.Visibility = Visibility.Collapsed;
                    StatusBar.IsOpen = false;
                    return;
                }

                ShowProgress(message, opacity: 1.0);
                break;

            case SharedStatusKind.Empty:
                ShowProgress(message, opacity: 0.7);
                break;

            case SharedStatusKind.Stale:
                ShowInfoBar(message, InfoBarSeverity.Warning);
                break;

            default: // Error
                ShowInfoBar(message, InfoBarSeverity.Error);
                break;
        }
    }

    private void ShowProgress(string message, double opacity)
    {
        RootPanel.Visibility = Visibility.Visible;

        StatusBar.IsOpen = false;

        MessageRow.Visibility = Visibility.Visible;
        LoadingRing.Visibility = Status?.Kind == SharedStatusKind.Loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = Status?.Kind == SharedStatusKind.Loading;
        MessageText.Text = message;
        MessageText.Opacity = opacity;
    }

    private void ShowInfoBar(string message, InfoBarSeverity severity)
    {
        RootPanel.Visibility = Visibility.Visible;

        MessageRow.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;

        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);
}
