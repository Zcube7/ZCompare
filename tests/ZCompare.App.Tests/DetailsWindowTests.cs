using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ZCompare.App.ViewModels;

namespace ZCompare.App.Tests;

public sealed class DetailsWindowTests
{
    [Fact]
    public void DifferenceRunsUseHighlightStyleAndPlainRunsRemainUnstyled()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var content = new CellDetailsContent(
                    "normal2",
                    [
                        new DetailTextSegment("normal", "normal", false),
                        new DetailTextSegment("2", "2", true),
                    ]);
                var window = new DetailsWindow("差异详情", content);
                var textBlock = Assert.IsType<TextBlock>(window.FindName("DetailsTextBlock"));
                var runs = textBlock.Inlines.OfType<Run>().ToArray();

                Assert.Equal(2, runs.Length);
                Assert.Equal(DependencyProperty.UnsetValue, runs[0].ReadLocalValue(TextElement.ForegroundProperty));
                Assert.Equal(DependencyProperty.UnsetValue, runs[0].ReadLocalValue(TextElement.BackgroundProperty));
                Assert.Equal(DependencyProperty.UnsetValue, runs[0].ReadLocalValue(TextElement.FontWeightProperty));
                Assert.Equal(Color.FromRgb(153, 27, 27), Assert.IsType<SolidColorBrush>(runs[1].Foreground).Color);
                Assert.Equal(Color.FromRgb(254, 202, 202), Assert.IsType<SolidColorBrush>(runs[1].Background).Color);
                Assert.Equal(FontWeights.SemiBold, runs[1].FontWeight);

                var clipboardField = typeof(DetailsWindow)
                    .GetField("_clipboardText", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(clipboardField);
                Assert.Equal(content.ClipboardText, clipboardField.GetValue(window));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "DetailsWindow rendering timed out.");

        Assert.Null(failure);
    }
}
