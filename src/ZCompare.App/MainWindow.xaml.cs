using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ZCompare.App.Services;
using ZCompare.App.ViewModels;
using ZCompare.Core;

namespace ZCompare.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IReleaseUpdateService _updateService = new GitHubReleaseUpdateService();
    private readonly UpdateStatusViewModel _updateStatus = new();
    private readonly CancellationTokenSource _windowLifetime = new();
    private ScrollViewer? _leftScrollViewer;
    private ScrollViewer? _rightScrollViewer;
    private bool _synchronizingScroll;
    private bool _synchronizingSelection;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"ZCompare {ProductInfo.VersionText} — XLSX 对比工具";

        var reader = new OpenXmlWorkbookReader();
        var comparer = new WorkbookComparer(reader);
        _viewModel = new MainWindowViewModel(
            reader,
            comparer,
            new FolderComparer(comparer),
            new PathDialogService(),
            new JsonRecentComparisonStore());
        DataContext = _viewModel;
        UpdateBanner.DataContext = _updateStatus;

        _viewModel.GridViewport.PropertyChanged += GridViewportOnPropertyChanged;
        _viewModel.GridNavigationRequested += ViewModelOnGridNavigationRequested;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        Loaded += MainWindow_OnLoaded;
        Closed += MainWindow_OnClosed;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        var result = await _updateService.CheckAsync(ProductInfo.Version, _windowLifetime.Token);
        if (result is not null && !_windowLifetime.IsCancellationRequested)
        {
            _updateStatus.Show(result);
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs eventArgs)
    {
        _windowLifetime.Cancel();
        _windowLifetime.Dispose();
    }

    private void DownloadUpdateButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_updateStatus.DownloadUri is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_updateStatus.DownloadUri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
            _updateStatus.Dismiss();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ErrorDialogService.ShowRecoverable(exception, "打开更新下载地址");
        }
    }

    private void DismissUpdateButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        _updateStatus.Dismiss();

    private void GridViewportOnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(GridViewportViewModel.ColumnCount))
        {
            return;
        }

        Dispatcher.BeginInvoke(RebuildSpreadsheetColumns, DispatcherPriority.DataBind);
    }

    private void RebuildSpreadsheetColumns()
    {
        LeftGrid.Columns.Clear();
        RightGrid.Columns.Clear();

        for (var index = 0; index < _viewModel.GridViewport.ColumnCount; index++)
        {
            LeftGrid.Columns.Add(CreateSpreadsheetColumn(index));
            RightGrid.Columns.Add(CreateSpreadsheetColumn(index));
        }
    }

    private DataGridColumn CreateSpreadsheetColumn(int index)
    {
        var elementStyle = new Style(typeof(TextBlock));
        elementStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        elementStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        elementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding($"Cells[{index}].Foreground")));
        elementStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new Binding($"Cells[{index}].FontFamily")));
        elementStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, new Binding($"Cells[{index}].FontSize")));
        elementStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, new Binding($"Cells[{index}].FontWeight")));
        elementStyle.Setters.Add(new Setter(TextBlock.FontStyleProperty, new Binding($"Cells[{index}].FontStyle")));
        elementStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, new Binding($"Cells[{index}].TextAlignment")));
        elementStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, new Binding($"Cells[{index}].TextWrapping")));

        var baseCellStyle = (Style)FindResource("SpreadsheetCellStyle");
        var cellStyle = new Style(typeof(DataGridCell), baseCellStyle);
        cellStyle.Setters.Add(new Setter(
            FrameworkElement.TagProperty,
            new Binding($"Cells[{index}]") { Mode = BindingMode.OneWay }));

        return new DataGridTextColumn
        {
            Header = GetColumnName(index + 1),
            Binding = new Binding($"Cells[{index}].DisplayValue")
            {
                Mode = BindingMode.OneWay,
                FallbackValue = string.Empty,
                TargetNullValue = string.Empty,
            },
            CellStyle = cellStyle,
            ElementStyle = elementStyle,
            IsReadOnly = true,
            Width = new DataGridLength(110),
            MinWidth = 48,
            MaxWidth = 360,
        };
    }

    private static string GetColumnName(int column)
    {
        Span<char> buffer = stackalloc char[8];
        var index = buffer.Length;
        while (column > 0)
        {
            column--;
            buffer[--index] = (char)('A' + (column % 26));
            column /= 26;
        }

        return new string(buffer[index..]);
    }

    private void PathPanel_OnPreviewDragOver(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Effects = eventArgs.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void PathPanel_OnDrop(object sender, DragEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { Tag: string sideName }
            || eventArgs.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            return;
        }

        var side = string.Equals(sideName, "Right", StringComparison.OrdinalIgnoreCase)
            ? CompareSide.Right
            : CompareSide.Left;
        _viewModel.SetDroppedPath(side, paths[0]);
    }

    private void FolderResultGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is not DependencyObject source ||
            FindVisualAncestor<DataGridRow>(source) is null)
        {
            return;
        }

        OpenSelectedFolderItem();
    }

    private void FolderResultGrid_OnKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Space && eventArgs.OriginalSource is not CheckBox)
        {
            if (FolderResultGrid.SelectedItem is FolderFileItemViewModel item)
            {
                item.IsMarkedForComparison = !item.IsMarkedForComparison;
                eventArgs.Handled = true;
            }
            return;
        }

        if (eventArgs.Key != Key.Enter)
        {
            return;
        }

        OpenSelectedFolderItem();
        eventArgs.Handled = true;
    }

    private void OpenSelectedFolderItem()
    {
        if (FolderResultGrid.SelectedItem is not FolderFileItemViewModel item)
        {
            return;
        }

        if (item.Status == ComparisonStatus.Error && !string.IsNullOrWhiteSpace(item.IssueDetails))
        {
            ShowDetailsWindow($"{item.RelativePath} 比较失败", item.IssueDetails);
            return;
        }

        if (_viewModel.OpenFolderItemCommand.CanExecute(item))
        {
            _viewModel.OpenFolderItemCommand.Execute(item);
        }
    }

    private void SelectAllFolderRows_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SelectAllFolderFiles();
    }

    private void SaveProfileButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new ProfileNameWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.SaveProfile(dialog.ProfileName);
        }
    }

    private void WorksheetSettingsButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new WorksheetSettingsWindow(
            _viewModel.CurrentComparisonOptions,
            _viewModel.IsWorkbookOpen ? _viewModel.CurrentLeftFile : _viewModel.LeftPath,
            _viewModel.IsWorkbookOpen ? _viewModel.CurrentRightFile : _viewModel.RightPath) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.ApplyWorksheetSettings(
                dialog.PairingMode,
                dialog.UseKeyColumnAlignment,
                dialog.ManualPairs,
                dialog.KeyColumnRules,
                dialog.ColumnMappings);
        }
    }

    private async void ExportReportButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出比较报告",
            FileName = _viewModel.ReportSuggestedFileName,
            Filter = "Excel 报告 (*.xlsx)|*.xlsx|JSON 报告 (*.json)|*.json",
            FilterIndex = 1,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var format = string.Equals(Path.GetExtension(dialog.FileName), ".json", StringComparison.OrdinalIgnoreCase)
            ? ComparisonReportFormat.Json
            : ComparisonReportFormat.Xlsx;
        await _viewModel.ExportReportAsync(dialog.FileName, format);
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(MainWindowViewModel.FocusedFolderFile))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel.FocusedFolderFile is { } item)
            {
                FolderResultGrid.ScrollIntoView(item);
            }
        }, DispatcherPriority.Loaded);
    }

    private void SpreadsheetGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is not DependencyObject source ||
            FindVisualAncestor<DataGridCell>(source) is null ||
            sender is not DataGrid grid)
        {
            return;
        }

        ShowCurrentCellDetails(grid);
    }

    private void SpreadsheetGrid_OnKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter || sender is not DataGrid grid)
        {
            return;
        }

        ShowCurrentCellDetails(grid);
        eventArgs.Handled = true;
    }

    private void ShowCurrentCellDetails(DataGrid grid)
    {
        if (grid.CurrentCell.Item is not GridRowViewModel row || grid.CurrentCell.Column is null)
        {
            return;
        }

        var displayRowIndex = grid.Items.IndexOf(row);
        var details = _viewModel.GetCellDialogDetails(displayRowIndex, grid.CurrentCell.Column.DisplayIndex);
        if (!string.IsNullOrWhiteSpace(details))
        {
            ShowDetailsWindow($"{row.RowHeader} 行单元格差异详情", details);
        }
    }

    private void OtherDetailsButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.OtherDetailsText))
        {
            ShowDetailsWindow("其他差异详情", _viewModel.OtherDetailsText);
        }
    }

    private void SpreadsheetGrid_OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender == LeftGrid)
        {
            _leftScrollViewer = FindVisualChild<ScrollViewer>(LeftGrid);
        }
        else if (sender == RightGrid)
        {
            _rightScrollViewer = FindVisualChild<ScrollViewer>(RightGrid);
        }
    }

    private void SpreadsheetGrid_OnScrollChanged(object sender, ScrollChangedEventArgs eventArgs)
    {
        if (_synchronizingScroll || _leftScrollViewer is null || _rightScrollViewer is null)
        {
            return;
        }

        if (eventArgs.OriginalSource is not ScrollViewer source
            || (source != _leftScrollViewer && source != _rightScrollViewer))
        {
            return;
        }

        var target = source == _leftScrollViewer ? _rightScrollViewer : _leftScrollViewer;
        _synchronizingScroll = true;
        try
        {
            if (eventArgs.VerticalChange != 0)
            {
                target.ScrollToVerticalOffset(source.VerticalOffset);
            }

            if (eventArgs.HorizontalChange != 0)
            {
                target.ScrollToHorizontalOffset(source.HorizontalOffset);
            }
        }
        finally
        {
            _synchronizingScroll = false;
        }
    }

    private void SpreadsheetGrid_OnCurrentCellChanged(object? sender, EventArgs eventArgs)
    {
        if (_synchronizingSelection || sender is not DataGrid source || source.CurrentCell.Item is not GridRowViewModel row)
        {
            return;
        }

        var columnIndex = source.CurrentCell.Column?.DisplayIndex ?? -1;
        if (columnIndex < 0)
        {
            return;
        }

        var displayRowIndex = source.Items.IndexOf(row);
        if (displayRowIndex < 0)
        {
            return;
        }

        _viewModel.SelectGridCell(displayRowIndex, columnIndex);
        var target = source == LeftGrid ? RightGrid : LeftGrid;
        SynchronizeCurrentCell(target, displayRowIndex, columnIndex, scrollIntoView: false);
    }

    private void ViewModelOnGridNavigationRequested(object? sender, GridNavigationEventArgs eventArgs)
    {
        SynchronizeCurrentCell(LeftGrid, eventArgs.RowIndex, eventArgs.ColumnIndex, scrollIntoView: true);
        SynchronizeCurrentCell(RightGrid, eventArgs.RowIndex, eventArgs.ColumnIndex, scrollIntoView: true);
        _viewModel.SelectGridCell(eventArgs.RowIndex, eventArgs.ColumnIndex);
    }

    private void SynchronizeCurrentCell(DataGrid target, int rowIndex, int columnIndex, bool scrollIntoView)
    {
        if (target.ItemsSource is not IList rows
            || rowIndex < 0
            || rowIndex >= rows.Count
            || columnIndex < 0
            || columnIndex >= target.Columns.Count)
        {
            return;
        }

        var item = rows[rowIndex];
        _synchronizingSelection = true;
        try
        {
            var cell = new DataGridCellInfo(item, target.Columns[columnIndex]);
            target.CurrentCell = cell;
            target.SelectedCells.Clear();
            target.SelectedCells.Add(cell);
            if (scrollIntoView)
            {
                target.ScrollIntoView(item, target.Columns[columnIndex]);
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }
        return null;
    }

    private void ShowDetailsWindow(string title, string details)
    {
        var window = new DetailsWindow(title, details) { Owner = this };
        window.ShowDialog();
    }

}
