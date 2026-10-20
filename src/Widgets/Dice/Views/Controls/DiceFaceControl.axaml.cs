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

    /// <summary>设计基准内容区（= 48 - 2 * 6 padding）：点阵与数字都画在这个方框里。</summary>
    private const double DesignContentSize = 36d;

    /// <summary>设计基准下的字号（单位数）。多位数字按位数收敛，避免撑破 36 的设计宽度。</summary>
    private const double DesignNumberFontSize = 24d;

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

    private void UpdateFaceSize() => ApplyFaceSize(FaceSize);

    /// <summary>
    /// 按实际骰面边长设置外框；圆角、投影留白与内边距等比缩放。
    /// 点阵/数字本身不在这里缩放——它们固定画在 36x36 的设计基准里，由 Viewbox
    /// 统一等比缩放到实际内容区，因此任何尺寸下都不会溢出骰面。
    /// </summary>
    private void ApplyFaceSize(double size)
    {
        if (!double.IsFinite(size) || size <= 0)
        {
            size = BaseFaceSize;
        }

        if (Math.Abs(FaceBorder.Width - size) < 0.01 && Math.Abs(FaceBorder.Height - size) < 0.01)
        {
            return;
        }

        double scale = size / BaseFaceSize;

        FaceBorder.Width = size;
        FaceBorder.Height = size;
        FaceBorder.CornerRadius = new CornerRadius(10 * scale);
        FaceBorder.Padding = new Thickness(6 * scale);
        // 投影呼吸边距：始终给 blur 2 留出 4*scale 的余量（小骰面下也按比例收）。
        FaceBorder.Margin = new Thickness(4 * scale);

        // 内容区在设计基准里是固定 36x36，Viewbox 负责缩放；这里只需保证
        // 数字在设计基准内不超宽。
        ContentPanel.Width = DesignContentSize;
        ContentPanel.Height = DesignContentSize;
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
            var text = val.ToString();
            NumberText.Text = text;

            // 设计基准宽度 36：单位数用 24 号，两位数/三位数按位数收敛，
            // 使 100（D100）也不会超出设计宽度而被裁切。
            NumberText.FontSize = text.Length switch
            {
                <= 1 => DesignNumberFontSize,
                2 => DesignNumberFontSize * 0.82,
                _ => DesignNumberFontSize * 0.64,
            };
        }
    }
}
