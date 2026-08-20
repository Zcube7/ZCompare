using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ZCompare.App.Services;

namespace ZCompare.App.Tests;

public sealed class MainWindowSmokeTests
{
    [Fact]
    public void SpreadsheetCellAncestorLookupHandlesInlineRun()
    {
        Exception? failure = null;
        object? ancestor = null;
        var thread = new Thread(() =>
        {
            try
            {
                var run = new Run("1002");
                var textBlock = new TextBlock();
                textBlock.Inlines.Add(run);
                var cell = new DataGridCell { Content = textBlock };
                cell.ApplyTemplate();
                cell.Measure(new Size(200, 40));
                cell.Arrange(new Rect(0, 0, 200, 40));
                cell.UpdateLayout();

                var method = typeof(MainWindow)
                    .GetMethod("FindVisualAncestor", BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(typeof(DataGridCell));
                ancestor = method.Invoke(null, [run]);
            }
            catch (TargetInvocationException exception)
            {
                failure = exception.InnerException ?? exception;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Inline ancestor lookup timed out.");

        Assert.Null(failure);
        Assert.IsType<DataGridCell>(ancestor);
    }

    [Fact]
    public void MainWindowCanBeConstructed()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new App();
                application.InitializeComponent();
                var window = new MainWindow();
                Assert.Contains($"ZCompare {ProductInfo.VersionText}", window.Title, StringComparison.Ordinal);
                window.Close();
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "MainWindow construction timed out.");

        Assert.Null(failure);
    }
}
