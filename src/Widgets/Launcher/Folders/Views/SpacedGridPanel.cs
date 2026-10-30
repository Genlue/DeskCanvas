using Avalonia;
using Avalonia.Controls;

namespace Folders.Views;

/// <summary>
/// A uniform grid panel that enforces exact spacing between columns and rows,
/// without adding extra outer margins.
/// </summary>
public class SpacedGridPanel : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<SpacedGridPanel, int>(nameof(Columns), 3);

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<SpacedGridPanel, int>(nameof(Rows), 3);

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SpacedGridPanel, double>(nameof(Spacing), 8.0);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<SpacedGridPanel, double>(nameof(ItemWidth), 0.0);

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<SpacedGridPanel, double>(nameof(ItemHeight), 0.0);

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    static SpacedGridPanel()
    {
        AffectsMeasure<SpacedGridPanel>(ColumnsProperty, RowsProperty, SpacingProperty, ItemWidthProperty, ItemHeightProperty);
        AffectsArrange<SpacedGridPanel>(ColumnsProperty, RowsProperty, SpacingProperty, ItemWidthProperty, ItemHeightProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int cols = Math.Max(1, Columns);
        int rows = Math.Max(1, Rows);
        double spacing = Math.Max(0, Spacing);

        double itemW = ItemWidth;
        double itemH = ItemHeight;

        if (itemW <= 0 || itemH <= 0)
        {
            foreach (var child in Children)
            {
                child.Measure(availableSize);
                itemW = Math.Max(itemW, child.DesiredSize.Width);
                itemH = Math.Max(itemH, child.DesiredSize.Height);
            }
        }
        else
        {
            var childConstraint = new Size(itemW, itemH);
            foreach (var child in Children)
            {
                child.Measure(childConstraint);
            }
        }

        double totalW = cols * itemW + Math.Max(0, cols - 1) * spacing;
        double totalH = rows * itemH + Math.Max(0, rows - 1) * spacing;

        return new Size(Math.Max(0, totalW), Math.Max(0, totalH));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = Math.Max(1, Columns);
        int rows = Math.Max(1, Rows);
        double spacing = Math.Max(0, Spacing);

        double itemW = ItemWidth > 0 ? ItemWidth : (finalSize.Width - Math.Max(0, cols - 1) * spacing) / cols;
        double itemH = ItemHeight > 0 ? ItemHeight : (finalSize.Height - Math.Max(0, rows - 1) * spacing) / rows;

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            int col = i % cols;
            int row = i / cols;

            if (row >= rows)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            double x = col * (itemW + spacing);
            double y = row * (itemH + spacing);

            child.Arrange(new Rect(x, y, Math.Max(0, itemW), Math.Max(0, itemH)));
        }

        double totalW = cols * itemW + Math.Max(0, cols - 1) * spacing;
        double totalH = rows * itemH + Math.Max(0, rows - 1) * spacing;
        return new Size(Math.Max(0, totalW), Math.Max(0, totalH));
    }
}
