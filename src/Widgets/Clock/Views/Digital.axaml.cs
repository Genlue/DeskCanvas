using Avalonia.Controls;
using Avalonia.Interactivity;
using Clock.Models;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views;

/// <summary>
/// 数字时钟小组件：通过 IWidgetSurfaceHints 声明 Colorful 模式下的专属卡片画刷。
/// </summary>
public partial class Digital : UserControl, IWidgetSurfaceHints
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "ClockDigitalCardBackground";

    private readonly DigitalClockViewModel viewModel;

    public Digital() : this(new ClockModel()) {}
    
    public Digital(ClockModel clockModel)
    {
        viewModel = new DigitalClockViewModel(clockModel);
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += (_, _) => viewModel.Stop();
        SizeChanged += OnSizeChanged;
        InitializeComponent();
    }

    // S/M tiers: at a single-cell height the date line (natural 8/52) collapses to
    // a few pixels — show the time only and let the Viewbox use all the height.
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        Date.IsVisible = viewModel.ShowDate
                         && e.NewSize.Height > 60
                         && e.NewSize.Width > 90;
}
