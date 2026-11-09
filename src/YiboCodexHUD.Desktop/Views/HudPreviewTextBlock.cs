using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using YiboCodexHUD.Desktop.ViewModels;

namespace YiboCodexHUD.Desktop.Views;

public sealed class HudPreviewTextBlock : TextBlock
{
    public static readonly DependencyProperty CandidatesProperty = DependencyProperty.Register(
        nameof(Candidates),
        typeof(IReadOnlyList<IReadOnlyList<OverlayViewModel.StyledDisplaySegment>>),
        typeof(HudPreviewTextBlock),
        new FrameworkPropertyMetadata(null, (element, _) => ((HudPreviewTextBlock)element).UpdateText()));

    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.Register(
        nameof(OffsetX), typeof(int), typeof(HudPreviewTextBlock),
        new FrameworkPropertyMetadata(0, (element, _) => ((HudPreviewTextBlock)element).UpdateText()));

    public static readonly DependencyProperty OffsetYProperty = DependencyProperty.Register(
        nameof(OffsetY), typeof(int), typeof(HudPreviewTextBlock),
        new FrameworkPropertyMetadata(0, (element, _) => ((HudPreviewTextBlock)element).UpdateText()));

    public IReadOnlyList<IReadOnlyList<OverlayViewModel.StyledDisplaySegment>>? Candidates
    {
        get => (IReadOnlyList<IReadOnlyList<OverlayViewModel.StyledDisplaySegment>>?)GetValue(CandidatesProperty);
        set => SetValue(CandidatesProperty, value);
    }

    public int OffsetX
    {
        get => (int)GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    public int OffsetY
    {
        get => (int)GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    public HudPreviewTextBlock()
    {
        SizeChanged += (_, _) => UpdateText();
    }

    private void UpdateText()
    {
        if (Candidates is not { Count: > 0 } candidates)
        {
            Inlines.Clear();
            return;
        }

        var segments = candidates[0];
        var text = string.Concat(segments.Select(segment => segment.Text));
        var dpi = VisualTreeHelper.GetDpi(this);
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        double Measure(string value) => new FormattedText(
            value, CultureInfo.CurrentUICulture, FlowDirection, typeface, FontSize,
            Brushes.Black, dpi.PixelsPerDip).WidthIncludingTrailingWhitespace;

        var textWidth = Measure(text);
        if (candidates.Count > 1 && ActualWidth > 0 && textWidth > ActualWidth)
        {
            segments = candidates[1];
            text = string.Concat(segments.Select(segment => segment.Text));
            textWidth = Measure(text);
        }

        Inlines.Clear();
        foreach (var segment in segments)
        {
            Inlines.Add(new Run(segment.Text) { Foreground = segment.Foreground });
        }
        ToolTip = text;

        var availableX = Math.Max(0, ActualWidth - textWidth);
        var availableY = Parent is FrameworkElement parent ? Math.Max(0, parent.ActualHeight - ActualHeight) / 2 : 0;
        var leadingSpace = TextAlignment switch
        {
            TextAlignment.Center => availableX / 2,
            TextAlignment.Right => availableX,
            _ => 0
        };
        RenderTransform = new TranslateTransform(
            Math.Clamp(OffsetX / dpi.DpiScaleX, -leadingSpace, availableX - leadingSpace),
            Math.Clamp(OffsetY / dpi.DpiScaleY, -availableY, availableY));
    }
}
