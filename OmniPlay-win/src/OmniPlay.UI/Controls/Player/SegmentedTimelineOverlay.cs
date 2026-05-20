using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace OmniPlay.UI.Controls.Player;

public sealed class SegmentedTimelineOverlay : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> BoundariesProperty =
        AvaloniaProperty.Register<SegmentedTimelineOverlay, IReadOnlyList<double>?>(nameof(Boundaries));

    private static readonly Pen BoundaryPen = new(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.5);
    private static readonly Pen ShadowPen = new(new SolidColorBrush(Color.FromArgb(130, 0, 0, 0)), 1);

    static SegmentedTimelineOverlay()
    {
        AffectsRender<SegmentedTimelineOverlay>(BoundariesProperty);
    }

    public IReadOnlyList<double>? Boundaries
    {
        get => GetValue(BoundariesProperty);
        set => SetValue(BoundariesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Boundaries is not { Count: > 0 } boundaries || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var trackHeight = Math.Clamp(Bounds.Height * 0.5, 4, 10);
        var top = (Bounds.Height - trackHeight) / 2;
        var bottom = top + trackHeight;

        foreach (var boundary in boundaries)
        {
            if (!double.IsFinite(boundary) || boundary <= 0 || boundary >= 1)
            {
                continue;
            }

            var x = Math.Round(Bounds.Width * boundary);
            context.DrawLine(ShadowPen, new Point(x + 1, top), new Point(x + 1, bottom));
            context.DrawLine(BoundaryPen, new Point(x, top), new Point(x, bottom));
        }
    }
}
