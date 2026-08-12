using System.Windows;

namespace ZCompare.App;

public partial class DetailsWindow : Window
{
    public DetailsWindow(string title, string details)
    {
        InitializeComponent();
        Title = title;
        DetailsTextBox.Text = details;
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!string.IsNullOrEmpty(DetailsTextBox.Text))
        {
            try
            {
                Clipboard.SetText(DetailsTextBox.Text);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
