using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ZCompare.App.ViewModels;

namespace ZCompare.App.Controls;

internal sealed class DifferenceTextBlock : TextBlock
{
    private static readonly Brush DifferenceBrush = CreateDifferenceBrush();

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<TextDifferenceSegment>),
        typeof(DifferenceTextBlock),
        new PropertyMetadata(null, SegmentsChanged));

    public IReadOnlyList<TextDifferenceSegment>? Segments
    {
        get => (IReadOnlyList<TextDifferenceSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private static void SegmentsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        var textBlock = (DifferenceTextBlock)dependencyObject;
        textBlock.Inlines.Clear();
        foreach (var segment in eventArgs.NewValue as IReadOnlyList<TextDifferenceSegment> ?? [])
        {
            var run = new Run(segment.Text);
            if (segment.IsDifferent)
            {
                run.Foreground = DifferenceBrush;
                run.FontWeight = FontWeights.Bold;
            }

            textBlock.Inlines.Add(run);
        }
    }

    private static Brush CreateDifferenceBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(153, 27, 27));
        brush.Freeze();
        return brush;
    }
}

internal sealed class DifferenceTextColumn : DataGridBoundColumn
{
    public BindingBase? SegmentsBinding { get; init; }

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) =>
        CreateElement();

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) =>
        CreateElement();

    private FrameworkElement CreateElement()
    {
        var element = new DifferenceTextBlock();
        if (ElementStyle is not null)
        {
            element.Style = ElementStyle;
        }

        if (SegmentsBinding is not null)
        {
            BindingOperations.SetBinding(element, DifferenceTextBlock.SegmentsProperty, SegmentsBinding);
        }

        return element;
    }
}
