using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ZCompare.App.ViewModels;

namespace ZCompare.App;

public partial class DetailsWindow : Window
{
    private static readonly Brush DifferenceForeground = CreateBrush(153, 27, 27);
    private static readonly Brush DifferenceBackground = CreateBrush(254, 202, 202);
    private readonly string _clipboardText;

    public DetailsWindow(string title, string details)
        : this(title, new CellDetailsContent(
            details,
            [new DetailTextSegment(details, details, false)]))
    {
    }

    internal DetailsWindow(string title, CellDetailsContent details)
    {
        InitializeComponent();
        Title = title;
        _clipboardText = details.ClipboardText;
        foreach (var segment in details.Segments)
        {
            var run = new Run(segment.DisplayText);
            if (segment.IsDifferent)
            {
                run.Foreground = DifferenceForeground;
                run.Background = DifferenceBackground;
                run.FontWeight = FontWeights.SemiBold;
            }

            DetailsTextBlock.Inlines.Add(run);
        }
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!string.IsNullOrEmpty(_clipboardText))
        {
            try
            {
                Clipboard.SetText(_clipboardText);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private static Brush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
