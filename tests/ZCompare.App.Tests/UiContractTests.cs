using System.IO;
using System.Xml.Linq;

namespace ZCompare.App.Tests;

public sealed class UiContractTests
{
    [Fact]
    public void DifferenceTogglesShareModeStyleAndDifferenceTag()
    {
        var mainWindow = XDocument.Load(FindRepositoryFile("src", "ZCompare.App", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var differenceToggles = mainWindow
            .Descendants(presentation + "RadioButton")
            .Where(static element => (string?)element.Attribute("Tag") == "Difference")
            .ToArray();

        Assert.Equal(2, differenceToggles.Length);
        Assert.All(differenceToggles, static toggle =>
        {
            Assert.Equal("Difference", (string?)toggle.Attribute("Tag"));
            Assert.Equal("{StaticResource ModeToggleStyle}", (string?)toggle.Attribute("Style"));
            Assert.Contains("DifferencesOnly", (string?)toggle.Attribute("IsChecked"), StringComparison.Ordinal);
        });
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file: {Path.Combine(relativeSegments)}");
    }
}
