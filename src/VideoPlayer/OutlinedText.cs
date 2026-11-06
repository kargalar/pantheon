using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VideoPlayer;

/// <summary>Single-line text with a rounded outline and optional hard shadow, matching how mpv/libass draws subtitles.</summary>
internal sealed class OutlinedText : Control
{
    private string _text = "";
    private IBrush _foreground = Brushes.White;
    private FormattedText? _formatted;

    public Typeface Typeface { get; init; } = Typeface.Default;
    public double FontSize { get; init; } = 20;
    public double OutlineWidth { get; init; }
    public double ShadowOffset { get; init; }

    public string Text
    {
        get => _text;
        set { _text = value; _formatted = null; InvalidateMeasure(); InvalidateVisual(); }
    }

    public IBrush Foreground
    {
        get => _foreground;
        set { _foreground = value; InvalidateVisual(); }
    }

    private double Pad => Math.Ceiling(OutlineWidth + ShadowOffset);

    private FormattedText Formatted => _formatted ??= new FormattedText(_text, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, Typeface, FontSize, Brushes.White);

    protected override Size MeasureOverride(Size availableSize) =>
        new(Formatted.WidthIncludingTrailingWhitespace + Pad * 2, Formatted.Height + Pad * 2);

    public override void Render(DrawingContext context)
    {
        if (_text.Length == 0) return;
        var geometry = Formatted.BuildGeometry(new Point(Pad, Pad));
        if (geometry is null) return;
        if (ShadowOffset > 0)
        {
            using (context.PushTransform(Matrix.CreateTranslation(ShadowOffset, ShadowOffset)))
                context.DrawGeometry(Brushes.Black, OutlineWidth > 0 ? new Pen(Brushes.Black, OutlineWidth * 2, lineJoin: PenLineJoin.Round) : null, geometry);
        }
        if (OutlineWidth > 0)
            context.DrawGeometry(null, new Pen(Brushes.Black, OutlineWidth * 2, lineJoin: PenLineJoin.Round), geometry);
        context.DrawGeometry(_foreground, null, geometry);
    }
}
