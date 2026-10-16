using Avalonia;
using Avalonia.Controls;
using Dice.Models;

namespace Dice.Views.Controls;

public partial class DiceFaceControl : UserControl
{
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<DiceFaceControl, int>(nameof(Value), 1);

    public static readonly StyledProperty<DiceType> DiceTypeProperty =
        AvaloniaProperty.Register<DiceFaceControl, DiceType>(nameof(DiceType), DiceType.D6);

    public static readonly StyledProperty<DiceFaceStyle> FaceStyleProperty =
        AvaloniaProperty.Register<DiceFaceControl, DiceFaceStyle>(nameof(FaceStyle), DiceFaceStyle.Dots);

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public DiceType DiceType
    {
        get => GetValue(DiceTypeProperty);
        set => SetValue(DiceTypeProperty, value);
    }

    public DiceFaceStyle FaceStyle
    {
        get => GetValue(FaceStyleProperty);
        set => SetValue(FaceStyleProperty, value);
    }

    static DiceFaceControl()
    {
        ValueProperty.Changed.AddClassHandler<DiceFaceControl>((x, _) => x.UpdateVisuals());
        DiceTypeProperty.Changed.AddClassHandler<DiceFaceControl>((x, _) => x.UpdateVisuals());
        FaceStyleProperty.Changed.AddClassHandler<DiceFaceControl>((x, _) => x.UpdateVisuals());
    }

    public DiceFaceControl()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        int val = Value;
        if (DiceType == DiceType.D6 && FaceStyle == DiceFaceStyle.Dots && val >= 1 && val <= 6)
        {
            NumberText.IsVisible = false;
            DotsGrid.IsVisible = true;

            DotTL.IsVisible = val is 4 or 5 or 6 or 2 or 3;
            DotML.IsVisible = val is 6;
            DotBL.IsVisible = val is 4 or 5 or 6 or 2 or 3;

            DotCenter.IsVisible = val is 3 or 5;
            DotCenterAccent.IsVisible = val == 1;

            DotTR.IsVisible = val is 4 or 5 or 6 or 2 or 3;
            DotMR.IsVisible = val is 6;
            DotBR.IsVisible = val is 4 or 5 or 6 or 2 or 3;
        }
        else
        {
            DotsGrid.IsVisible = false;
            NumberText.IsVisible = true;
            NumberText.Text = val.ToString();
        }
    }
}
