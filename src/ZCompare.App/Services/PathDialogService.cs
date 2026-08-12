using Microsoft.Win32;
using System.IO;

namespace ZCompare.App.Services;

internal interface IPathDialogService
{
    string? SelectWorkbook(string? initialPath);

    string? SelectFolder(string? initialPath);
}

internal sealed class PathDialogService : IPathDialogService
{
    public string? SelectWorkbook(string? initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Excel 工作簿",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = GetInitialDirectory(initialPath),
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectFolder(string? initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含 XLSX 的文件夹",
            Multiselect = false,
            InitialDirectory = GetInitialDirectory(initialPath),
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string? GetInitialDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Directory.Exists(path))
        {
            return path;
        }

        return Path.GetDirectoryName(path);
    }
}
