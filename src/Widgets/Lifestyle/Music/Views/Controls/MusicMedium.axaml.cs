using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Music.Models;
using Music.ViewModels;

namespace Music.Views.Controls;

public partial class MusicMedium : UserControl
{
    private readonly MusicViewModel viewModel;

    public MusicMedium() : this(new MusicViewModel(new MusicModel())) { }

    public MusicMedium(MusicViewModel viewModel)
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

        // Dynamic edge padding according to height
        var pad = Math.Clamp(Math.Round(h * 0.09), 10.0, 16.0);
        var rightPad = Math.Clamp(Math.Round(w * 0.05), 12.0, 20.0);
        var gap = Math.Clamp(Math.Round(w * 0.04), 12.0, 18.0);

        // Compute cover size: must fit vertically within (h - 2*pad), and horizontally <= 44% of card width
        var maxCoverW = (w - pad - rightPad - gap) * 0.44;
        var coverSide = Math.Max(32.0, Math.Min(h - 2 * pad, maxCoverW));
        var vertPad = Math.Max(4.0, (h - coverSide) / 2.0);

        CoverBorder.Width = coverSide;
        CoverBorder.Height = coverSide;
        CoverBorder.CornerRadius = new CornerRadius(Math.Clamp(coverSide * 0.13, 8.0, 16.0));
        CoverBorder.Margin = new Thickness(pad, vertPad, gap, vertPad);

        var rightPanelVertPad = Math.Max(4.0, vertPad - 2.0);
        RightPanel.Margin = new Thickness(0, rightPanelVertPad, rightPad, rightPanelVertPad);

        // Fine-tuned typography & spacing based on available height and width
        var rightAvailW = w - (pad + coverSide + gap) - rightPad;
        if (rightAvailW < 125)
        {
            PrevButton.Width = 24;
            PrevButton.Height = 24;
            PlayButton.Width = 32;
            PlayButton.Height = 32;
            NextButton.Width = 24;
            NextButton.Height = 24;
            ControlsSection.Spacing = Math.Clamp(Math.Round((rightAvailW - 80) / 2.0), 8.0, 16.0);
        }
        else if (rightAvailW < 155)
        {
            PrevButton.Width = 26;
            PrevButton.Height = 26;
            PlayButton.Width = 34;
            PlayButton.Height = 34;
            NextButton.Width = 26;
            NextButton.Height = 26;
            ControlsSection.Spacing = Math.Clamp(Math.Round((rightAvailW - 86) / 2.0), 12.0, 20.0);
        }
        else
        {
            PrevButton.Width = 28;
            PrevButton.Height = 28;
            PlayButton.Width = 36;
            PlayButton.Height = 36;
            NextButton.Width = 28;
            NextButton.Height = 28;
            ControlsSection.Spacing = 24;
        }

        if (h < 130)
        {
            TitleBlock.FontSize = 13.5;
            ArtistBlock.FontSize = 11.5;
            ProgressSection.Margin = new Thickness(0, 4, 0, 4);
        }
        else
        {
            TitleBlock.FontSize = 15;
            ArtistBlock.FontSize = 12.0;
            ProgressSection.Margin = new Thickness(0, 7, 0, 7);
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
