using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Music.Models;
using Music.ViewModels;

namespace Music.Views.Controls;

public partial class MusicLarge : UserControl
{
    private readonly MusicViewModel viewModel;

    public MusicLarge() : this(new MusicViewModel(new MusicModel())) { }

    public MusicLarge(MusicViewModel viewModel)
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        AdaptLayout(e.NewSize);
    }

    public void AdaptLayout(Size size)
    {
        var h = size.Height;
        var w = size.Width;
        if (h <= 0 || w <= 0) return;

        var padX = Math.Clamp(Math.Round(w * 0.06), 12.0, 24.0);
        var padY = Math.Clamp(Math.Round(h * 0.05), 10.0, 20.0);
        MainGrid.Margin = new Thickness(padX, padY);

        // Responsive cover side: fits height and width comfortably
        var maxCoverH = h * 0.48;
        var maxCoverW = w * 0.60;
        var coverSide = Math.Clamp(Math.Min(maxCoverH, maxCoverW), 90.0, 220.0);

        CoverBorder.Width = coverSide;
        CoverBorder.Height = coverSide;
        CoverBorder.CornerRadius = new CornerRadius(Math.Clamp(coverSide * 0.12, 12.0, 22.0));

        // Responsive progress bar width: ~80% of card width, max 320
        var progressWidth = Math.Clamp(w * 0.82, 160.0, 320.0);
        ProgressSection.Width = progressWidth;

        if (w >= 340)
        {
            TitleBlock.FontSize = 20;
            ArtistBlock.FontSize = 14;
            ControlsSection.Spacing = 32;
        }
        else
        {
            TitleBlock.FontSize = 17;
            ArtistBlock.FontSize = 12.5;
            ControlsSection.Spacing = 24;
        }
    }

    private void OnProgressPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Bounds.Width > 0)
        {
            var pos = e.GetPosition(border);
            var percent = Math.Clamp(pos.X / border.Bounds.Width, 0.0, 1.0);
            viewModel.Seek(percent);
        }
    }
}
