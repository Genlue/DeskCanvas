using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Music.Models;
using Music.ViewModels;

namespace Music.Views.Controls;

public partial class MusicSmall : UserControl
{
    private readonly MusicViewModel viewModel;

    public MusicSmall() : this(new MusicViewModel(new MusicModel())) { }

    public MusicSmall(MusicViewModel viewModel)
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

        var padX = Math.Clamp(Math.Round(w * 0.08), 8.0, 16.0);
        var padY = Math.Clamp(Math.Round(h * 0.06), 6.0, 14.0);
        MainGrid.Margin = new Thickness(padX, padY);

        // Proportional cover size: ~32-36% of card width, max ~64
        var coverSide = Math.Clamp(Math.Round(w * 0.34), 44.0, 64.0);
        CoverBorder.Width = coverSide;
        CoverBorder.Height = coverSide;
        CoverBorder.CornerRadius = new CornerRadius(Math.Clamp(coverSide * 0.20, 8.0, 14.0));

        if (w >= 180)
        {
            TitleBlock.FontSize = 13.5;
            ArtistBlock.FontSize = 11.5;
            ControlsSection.Spacing = 22;
        }
        else if (w >= 145)
        {
            TitleBlock.FontSize = 12.0;
            ArtistBlock.FontSize = 10.0;
            ControlsSection.Spacing = 16;
        }
        else
        {
            TitleBlock.FontSize = 11.0;
            ArtistBlock.FontSize = 9.5;
            // Buttons: Prev(26) + Play(32) + Next(26) = 84px total width.
            var availSpacing = Math.Max(4.0, (w - 84 - padX * 2) / 2.0);
            ControlsSection.Spacing = Math.Clamp(Math.Round(availSpacing), 4.0, 14.0);
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
