using Avalonia;
using Avalonia.Controls;
using Music.Models;
using Music.ViewModels;

namespace Music.Views.Controls;

public partial class MusicBanner : UserControl
{
    private readonly MusicViewModel viewModel;

    public MusicBanner() : this(new MusicViewModel(new MusicModel())) { }

    public MusicBanner(MusicViewModel viewModel)
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

        // Adapt cover art to available height
        var padY = Math.Clamp(Math.Round(h * 0.12), 4.0, 10.0);
        var coverSide = Math.Max(28.0, h - 2 * padY - 3.0); // 3px reserved for bottom bar

        CoverBorder.Width = coverSide;
        CoverBorder.Height = coverSide;
        CoverBorder.CornerRadius = new CornerRadius(Math.Clamp(coverSide * 0.20, 6.0, 12.0));

        var padX = Math.Clamp(Math.Round(w * 0.04), 8.0, 16.0);

        // If width is very tight (e.g. 2x1 ~ 150px): optimize space for track title
        if (w < 190)
        {
            PrevButton.IsVisible = false;
            NextButton.IsVisible = false;
            TitleBlock.FontSize = 12.0;
            ArtistBlock.FontSize = 10.0;
            CoverBorder.Margin = new Thickness(0, 0, 8, 0);
            MainGrid.Margin = new Thickness(8, 0, 8, 3);
        }
        else
        {
            PrevButton.IsVisible = true;
            NextButton.IsVisible = true;
            TitleBlock.FontSize = 13.5;
            ArtistBlock.FontSize = 11.0;
            CoverBorder.Margin = new Thickness(0, 0, 10, 0);
            MainGrid.Margin = new Thickness(padX, 0, padX, 3);
        }
    }
}
