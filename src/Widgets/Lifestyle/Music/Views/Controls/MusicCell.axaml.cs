using Avalonia;
using Avalonia.Controls;
using Music.Models;
using Music.ViewModels;

namespace Music.Views.Controls;

public partial class MusicCell : UserControl
{
    private readonly MusicViewModel viewModel;

    public MusicCell() : this(new MusicViewModel(new MusicModel())) { }

    public MusicCell(MusicViewModel viewModel)
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
        var side = Math.Min(size.Width, size.Height);
        if (side <= 0) return;

        var pad = Math.Clamp(Math.Round(side * 0.08), 4.0, 10.0);
        var borderSide = Math.Max(24.0, side - 2 * pad);

        CellBorder.Width = borderSide;
        CellBorder.Height = borderSide;
        CellBorder.CornerRadius = new CornerRadius(Math.Clamp(borderSide * 0.18, 6.0, 16.0));

        var btnSide = Math.Clamp(borderSide * 0.44, 20.0, 36.0);
        PlayPauseButton.Width = btnSide;
        PlayPauseButton.Height = btnSide;
        PlayPauseButton.CornerRadius = new CornerRadius(btnSide / 2.0);
    }
}
