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

    /// <summary>
    /// 骰面边长（DIP，不含投影呼吸边距）。默认 48，与弹窗/1x1 的既有尺寸一致；
    /// 小卡片里由 <see cref="DiceTrayPanel"/> 按可用空间调小。
    /// </summary>
    public static readonly StyledProperty<double> FaceSizeProperty =
        AvaloniaProperty.Register<DiceFaceControl, double>(nameof(FaceSize), 48d);

    private const double BaseFaceSize = 48d;

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

    public double FaceSize
    {
        get => GetValue(FaceSizeProperty);
        set => SetValue(FaceSizeProperty, value);
    }

    static DiceFaceControl()
    {
        ValueProperty.Changed.AddClassHandler<DiceFaceControl>((x, _) => x.UpdateVisuals());
        DiceTypeProperty.Changed.AddClassHandler<DiceFaceControl>((x, _) => x.UpdateVisuals());
        FaceStyleProperty.Changed.AddClassHandler<DiceFaceControl>((x, _) => x.UpdateVisuals());
        FaceSizeProperty.Changed.AddClassHandler<DiceFaceControl>((x, _) => x.UpdateFaceSize());
    }

    public DiceFaceControl()
    {
        InitializeComponent();
        UpdateFaceSize();
        UpdateVisuals();
    }

    /// <summary>
    /// 按 <see cref="FaceSize"/> 等比缩放骰面几何（圆角、边距、点径、字号）。
    /// 默认尺寸 48 时所有数值与历史实现完全一致。
    /// </summary>
    private void UpdateFaceSize()
    {
        double size = FaceSize;
        if (!double.IsFinite(size) || size <= 0)
        {
            size = BaseFaceSize;
        }

        double scale = size / BaseFaceSize;

        FaceBorder.Width = size;
        FaceBorder.Height = size;
        FaceBorder.CornerRadius = new CornerRadius(10 * scale);
        FaceBorder.Margin = new Thickness(4 * scale);
        ContentPanel.Margin = new Thickness(6 * scale);
        NumberText.FontSize = 24 * scale;

        SetDotSize(DotTL, 9 * scale);
        SetDotSize(DotML, 9 * scale);
        SetDotSize(DotBL, 9 * scale);
        SetDotSize(DotTR, 9 * scale);
        SetDotSize(DotMR, 9 * scale);
        SetDotSize(DotBR, 9 * scale);
        SetDotSize(DotCenter, 10 * scale);
        SetDotSize(DotCenterAccent, 10 * scale);
    }

    private static void SetDotSize(Avalonia.Controls.Shapes.Ellipse dot, double diameter)
    {
        dot.Width = diameter;
        dot.Height = diameter;
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
