using System.Windows;

namespace ZCompare.App.Services;

internal static class ErrorDialogService
{
    public static void ShowRecoverable(Exception exception, string operation = "操作") =>
        Show(exception, operation, fatal: false);

    public static void ShowFatal(Exception exception, string operation = "程序运行") =>
        Show(exception, operation, fatal: true);

    private static void Show(Exception exception, string operation, bool fatal)
    {
        var details = $"ZCompare v{ProductInfo.VersionText}\n" +
                      $"操作：{operation}\n" +
                      $"结果：{(fatal ? "程序无法安全继续，将退出" : "操作失败，程序仍可继续使用")}\n" +
                      $"原因：{exception.Message}\n" +
                      $"错误类型：{exception.GetType().FullName}\n" +
                      $"错误代码：0x{exception.HResult:X8}\n\n" +
                      "提示：复制这些信息前，请自行确认其中没有不希望分享的本机路径。";

        try
        {
            var owner = Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(static window => window.IsActive);
            var dialog = new DetailsWindow(fatal ? "ZCompare 无法继续" : "ZCompare 操作失败", details);
            if (owner is not null)
            {
                dialog.Owner = owner;
            }
            dialog.ShowDialog();
        }
        catch
        {
            MessageBox.Show(
                details,
                fatal ? "ZCompare 无法继续" : "ZCompare 操作失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
