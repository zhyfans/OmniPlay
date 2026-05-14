using Avalonia;
using Avalonia.Controls;

namespace OmniPlay.UI.Controls.Layout;

public sealed class JustifiedWrapPanel : Panel
{
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, double>(nameof(ItemWidth), 100);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, double>(nameof(ItemHeight), 100);

    public static readonly StyledProperty<double> HorizontalSpacingProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, double>(nameof(HorizontalSpacing), 24);

    public static readonly StyledProperty<double> VerticalSpacingProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, double>(nameof(VerticalSpacing), 20);

    public static readonly StyledProperty<int> ItemsPerRowProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, int>(nameof(ItemsPerRow), 0);

    public static readonly StyledProperty<int> MinimumJustifiedItemsProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, int>(nameof(MinimumJustifiedItems), 4);

    static JustifiedWrapPanel()
    {
        AffectsMeasure<JustifiedWrapPanel>(
            ItemWidthProperty,
            ItemHeightProperty,
            HorizontalSpacingProperty,
            VerticalSpacingProperty,
            ItemsPerRowProperty,
            MinimumJustifiedItemsProperty);
    }

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double HorizontalSpacing
    {
        get => GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public int ItemsPerRow
    {
        get => GetValue(ItemsPerRowProperty);
        set => SetValue(ItemsPerRowProperty, value);
    }

    public int MinimumJustifiedItems
    {
        get => GetValue(MinimumJustifiedItemsProperty);
        set => SetValue(MinimumJustifiedItemsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemSize = ResolveItemSize();
        foreach (var child in Children)
        {
            child.Measure(itemSize);
        }

        if (Children.Count == 0)
        {
            return default;
        }

        var availableWidth = double.IsInfinity(availableSize.Width)
            ? CalculateNaturalWidth(Children.Count)
            : availableSize.Width;
        var itemsPerRow = ResolveItemsPerRow(availableWidth);
        var rowCount = (int)Math.Ceiling(Children.Count / (double)itemsPerRow);
        var desiredHeight = rowCount * itemSize.Height + Math.Max(0, rowCount - 1) * VerticalSpacing;

        return new Size(availableWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemSize = ResolveItemSize();
        if (Children.Count == 0)
        {
            return finalSize;
        }

        var itemsPerRow = ResolveItemsPerRow(finalSize.Width);
        var childIndex = 0;
        var y = 0d;

        while (childIndex < Children.Count)
        {
            var rowCount = Math.Min(itemsPerRow, Children.Count - childIndex);
            var gap = ResolveRowGap(finalSize.Width, itemSize.Width, rowCount);
            var x = 0d;

            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                Children[childIndex].Arrange(new Rect(x, y, itemSize.Width, itemSize.Height));
                childIndex++;
                x += itemSize.Width + gap;
            }

            y += itemSize.Height + VerticalSpacing;
        }

        return finalSize;
    }

    private Size ResolveItemSize()
    {
        return new Size(Math.Max(0, ItemWidth), Math.Max(0, ItemHeight));
    }

    private int ResolveItemsPerRow(double availableWidth)
    {
        if (ItemsPerRow > 0)
        {
            return Math.Max(1, ItemsPerRow);
        }

        var itemWidth = Math.Max(1, ItemWidth);
        var spacing = Math.Max(0, HorizontalSpacing);
        return Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (itemWidth + spacing)));
    }

    private double ResolveRowGap(double availableWidth, double itemWidth, int rowCount)
    {
        var spacing = Math.Max(0, HorizontalSpacing);
        if (rowCount < Math.Max(2, MinimumJustifiedItems))
        {
            if (rowCount > 1 && rowCount * itemWidth + (rowCount - 1) * spacing > availableWidth)
            {
                return Math.Max(0, (availableWidth - rowCount * itemWidth) / (rowCount - 1));
            }

            return spacing;
        }

        var justifiedGap = (availableWidth - rowCount * itemWidth) / (rowCount - 1);
        return Math.Max(0, justifiedGap);
    }

    private double CalculateNaturalWidth(int itemCount)
    {
        var itemsPerRow = ItemsPerRow > 0 ? Math.Min(ItemsPerRow, itemCount) : itemCount;
        return itemsPerRow * Math.Max(0, ItemWidth) + Math.Max(0, itemsPerRow - 1) * Math.Max(0, HorizontalSpacing);
    }
}
