using System.Windows;

namespace ZCompare.App;

public partial class ProfileNameWindow : Window
{
    public ProfileNameWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ProfileNameTextBox.Focus();
    }

    public string ProfileName => ProfileNameTextBox.Text.Trim();

    private void SaveButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ProfileName.Length == 0)
        {
            MessageBox.Show(this, "请输入配置名称。", "保存配置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
