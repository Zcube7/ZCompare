using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ZCompare.App.ViewModels;
using ZCompare.Core;

namespace ZCompare.App;

public partial class WorksheetSettingsWindow : Window
{
    private readonly IWorkbookReader _suggestionWorkbookReader = new OpenXmlWorkbookReader();
    private CancellationTokenSource? _suggestionCancellation;
    private string? _analyzedLeftWorksheetName;
    private string? _analyzedRightWorksheetName;
    private int _analyzedLeftHeaderRow = 1;
    private int _analyzedRightHeaderRow = 1;

    public WorksheetSettingsWindow(ComparisonOptions options)
        : this(options, null, null)
    {
    }

    public WorksheetSettingsWindow(
        ComparisonOptions options,
        string? leftPath,
        string? rightPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        InitializeComponent();

        SuggestionLeftPathTextBox.Text = leftPath ?? string.Empty;
        SuggestionRightPathTextBox.Text = rightPath ?? string.Empty;

        PairingMode = options.WorksheetPairingMode;
        UseKeyColumnAlignment = options.RowAlignmentMode == RowAlignmentMode.KeyColumns;
        foreach (var pair in options.ManualWorksheetPairs)
        {
            ManualPairRows.Add(new ManualPairEditorRow
            {
                LeftWorksheetName = pair.LeftWorksheetName,
                RightWorksheetName = pair.RightWorksheetName,
            });
        }
        foreach (var rule in options.KeyColumnRules)
        {
            KeyRuleRows.Add(new KeyRuleEditorRow
            {
                WorksheetName = rule.WorksheetName,
                HeaderRow = rule.HeaderRow.ToString(),
                Columns = string.Join(',', rule.ColumnIdentifiers),
            });
        }
        foreach (var mapping in options.ColumnMappings)
        {
            ColumnMappingRows.Add(new ColumnMappingEditorRow
            {
                LeftWorksheetName = mapping.LeftWorksheetName,
                RightWorksheetName = mapping.RightWorksheetName,
                ColumnPairs = string.Join(",", mapping.ColumnPairs.Select(static pair =>
                    $"{pair.LeftColumnIdentifier}={pair.RightColumnIdentifier}")),
            });
        }

        PairingModeComboBox.SelectedIndex = PairingMode switch
        {
            WorksheetPairingMode.Index => 1,
            WorksheetPairingMode.Manual => 2,
            _ => 0,
        };
        UseKeyColumnsCheckBox.IsChecked = UseKeyColumnAlignment;
        UpdateEditorStates();
        Closed += (_, _) => _suggestionCancellation?.Cancel();
    }

    public ObservableCollection<ManualPairEditorRow> ManualPairRows { get; } = [];

    public ObservableCollection<KeyRuleEditorRow> KeyRuleRows { get; } = [];

    public ObservableCollection<ColumnMappingEditorRow> ColumnMappingRows { get; } = [];

    public ObservableCollection<string> LeftWorksheetNames { get; } = [];

    public ObservableCollection<string> RightWorksheetNames { get; } = [];

    public ObservableCollection<AlignmentSuggestionEditorRow> SuggestionRows { get; } = [];

    public WorksheetPairingMode PairingMode { get; private set; }

    public bool UseKeyColumnAlignment { get; private set; }

    public IReadOnlyList<WorksheetPair> ManualPairs { get; private set; } = [];

    public IReadOnlyList<KeyColumnRule> KeyColumnRules { get; private set; } = [];

    public IReadOnlyList<WorksheetColumnMapping> ColumnMappings { get; private set; } = [];

    private void PairingModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        PairingMode = PairingModeComboBox.SelectedIndex switch
        {
            1 => WorksheetPairingMode.Index,
            2 => WorksheetPairingMode.Manual,
            _ => WorksheetPairingMode.Name,
        };
        UpdateEditorStates();
    }

    private void UseKeyColumnsCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        UseKeyColumnAlignment = UseKeyColumnsCheckBox.IsChecked == true;
        UpdateEditorStates();
    }

    private void UpdateEditorStates()
    {
        if (!IsInitialized)
        {
            return;
        }

        ManualPairsGrid.IsEnabled = PairingMode == WorksheetPairingMode.Manual;
        KeyRulesGrid.IsEnabled = UseKeyColumnAlignment;
    }

    private void RemoveManualPair_OnClick(object sender, RoutedEventArgs eventArgs) =>
        RemoveSelected(ManualPairsGrid, ManualPairRows);

    private void RemoveKeyRule_OnClick(object sender, RoutedEventArgs eventArgs) =>
        RemoveSelected(KeyRulesGrid, KeyRuleRows);

    private void RemoveColumnMapping_OnClick(object sender, RoutedEventArgs eventArgs) =>
        RemoveSelected(ColumnMappingsGrid, ColumnMappingRows);

    private async void OpenSuggestions_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        SettingsTabControl.SelectedItem = SuggestionsTab;
        await RefreshSuggestionWorksheetsAsync(showErrors: false);
        InferSuggestionWorksheets();
    }

    private async void BrowseSuggestionLeft_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (TryBrowseSuggestionFile(SuggestionLeftPathTextBox.Text, out var filePath))
        {
            SuggestionLeftPathTextBox.Text = filePath;
            await LoadWorksheetNamesAsync(filePath, LeftWorksheetNames, showErrors: true);
            InferSuggestionWorksheets();
        }
    }

    private async void BrowseSuggestionRight_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (TryBrowseSuggestionFile(SuggestionRightPathTextBox.Text, out var filePath))
        {
            SuggestionRightPathTextBox.Text = filePath;
            await LoadWorksheetNamesAsync(filePath, RightWorksheetNames, showErrors: true);
            InferSuggestionWorksheets();
        }
    }

    private async void AnalyzeSuggestions_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var leftPath = SuggestionLeftPathTextBox.Text.Trim();
        var rightPath = SuggestionRightPathTextBox.Text.Trim();
        if (!TryValidateSuggestionPath(leftPath, "左侧", out var pathError) ||
            !TryValidateSuggestionPath(rightPath, "右侧", out pathError))
        {
            ShowSuggestionMessage(pathError);
            return;
        }

        await RefreshSuggestionWorksheetsAsync(showErrors: true);
        InferSuggestionWorksheets();
        var leftWorksheetName = SuggestionLeftWorksheetComboBox.Text.Trim();
        var rightWorksheetName = SuggestionRightWorksheetComboBox.Text.Trim();
        if (leftWorksheetName.Length == 0 || rightWorksheetName.Length == 0)
        {
            ShowSuggestionMessage("请选择或填写左右工作表名称。");
            return;
        }
        if (!TryParseHeaderRow(SuggestionLeftHeaderRowTextBox.Text, out var leftHeaderRow) ||
            !TryParseHeaderRow(SuggestionRightHeaderRowTextBox.Text, out var rightHeaderRow))
        {
            ShowSuggestionMessage("左右表头行必须是大于等于 1 的整数。");
            return;
        }

        _suggestionCancellation?.Cancel();
        _suggestionCancellation?.Dispose();
        _suggestionCancellation = new CancellationTokenSource();
        AnalyzeSuggestionsButton.IsEnabled = false;
        CancelSuggestionAnalysisButton.IsEnabled = true;
        ApplySuggestionsButton.IsEnabled = false;
        SuggestionStatusTextBlock.Text = "正在读取样本并分析…";
        SuggestionRows.Clear();
        SuggestionDetailsTextBlock.Text = string.Empty;

        try
        {
            var service = new AlignmentSuggestionService();
            var result = await service.AnalyzeAsync(
                leftPath,
                rightPath,
                leftWorksheetName,
                rightWorksheetName,
                new AlignmentSuggestionOptions
                {
                    LeftHeaderRow = leftHeaderRow,
                    RightHeaderRow = rightHeaderRow,
                },
                _suggestionCancellation.Token);

            foreach (var suggestion in result.Suggestions)
            {
                SuggestionRows.Add(new AlignmentSuggestionEditorRow(suggestion));
            }

            _analyzedLeftWorksheetName = leftWorksheetName;
            _analyzedRightWorksheetName = rightWorksheetName;
            _analyzedLeftHeaderRow = leftHeaderRow;
            _analyzedRightHeaderRow = rightHeaderRow;
            SuggestionStatusTextBlock.Text = AlignmentSuggestionPresentation.FormatCompletedStatus(result);
        }
        catch (OperationCanceledException)
        {
            SuggestionStatusTextBlock.Text = "分析已取消。";
        }
        catch (Exception exception)
        {
            SuggestionStatusTextBlock.Text = "分析失败。";
            ShowSuggestionMessage($"无法分析建议：{exception.Message}");
        }
        finally
        {
            AnalyzeSuggestionsButton.IsEnabled = true;
            CancelSuggestionAnalysisButton.IsEnabled = false;
            ApplySuggestionsButton.IsEnabled = true;
        }
    }

    private void CancelSuggestionAnalysis_OnClick(object sender, RoutedEventArgs eventArgs) =>
        _suggestionCancellation?.Cancel();

    private void ApplySuggestions_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var selected = SuggestionRows
            .Where(static row => row.IsSelected)
            .Select(static row => row.Suggestion)
            .ToArray();
        if (selected.Length == 0)
        {
            ShowSuggestionMessage("请先勾选至少一条可应用的建议。");
            return;
        }
        if (_analyzedLeftWorksheetName is null || _analyzedRightWorksheetName is null)
        {
            ShowSuggestionMessage("请先完成一次建议分析。");
            return;
        }

        var result = AlignmentSuggestionApplicator.Apply(
            selected,
            _analyzedLeftWorksheetName,
            _analyzedRightWorksheetName,
            _analyzedLeftHeaderRow,
            _analyzedRightHeaderRow,
            KeyRuleRows,
            ColumnMappingRows);
        if (result.AppliedKeyColumns)
        {
            UseKeyColumnsCheckBox.IsChecked = true;
        }

        SuggestionStatusTextBlock.Text = result.Message;
        if (result.AppliedSuggestionCount > 0)
        {
            SettingsTabControl.SelectedIndex = result.AppliedColumnMappings ? 2 : 1;
        }
        else
        {
            ShowSuggestionMessage(result.Message);
        }
    }

    private void SuggestionsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        SuggestionDetailsTextBlock.Text = SuggestionsGrid.SelectedItem is AlignmentSuggestionEditorRow row
            ? row.Details
            : "分组列建议仅供参考，不能直接应用。";
    }

    private async Task RefreshSuggestionWorksheetsAsync(bool showErrors)
    {
        await LoadWorksheetNamesAsync(
            SuggestionLeftPathTextBox.Text.Trim(),
            LeftWorksheetNames,
            showErrors);
        await LoadWorksheetNamesAsync(
            SuggestionRightPathTextBox.Text.Trim(),
            RightWorksheetNames,
            showErrors);
    }

    private async Task LoadWorksheetNamesAsync(
        string filePath,
        ObservableCollection<string> target,
        bool showErrors)
    {
        if (!File.Exists(filePath) ||
            !string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var metadata = await _suggestionWorkbookReader.ReadMetadataAsync(filePath);
            target.Clear();
            foreach (var worksheet in metadata.Worksheets)
            {
                target.Add(worksheet.Name);
            }
        }
        catch (Exception exception) when (showErrors)
        {
            ShowSuggestionMessage($"无法读取“{Path.GetFileName(filePath)}”的工作表：{exception.Message}");
        }
        catch
        {
            // Opening the suggestion tab is best-effort; analysis reports actionable errors.
        }
    }

    private void InferSuggestionWorksheets()
    {
        if (SuggestionLeftWorksheetComboBox.Text.Trim().Length > 0 &&
            SuggestionRightWorksheetComboBox.Text.Trim().Length > 0)
        {
            return;
        }

        var manual = ManualPairsGrid.SelectedItem as ManualPairEditorRow ??
            ManualPairRows.FirstOrDefault(static row => !row.IsBlank);
        var mapping = ColumnMappingsGrid.SelectedItem as ColumnMappingEditorRow ??
            ColumnMappingRows.FirstOrDefault(static row => !row.IsBlank);
        var leftName = manual?.LeftWorksheetName.Trim();
        var rightName = manual?.RightWorksheetName.Trim();
        if (string.IsNullOrWhiteSpace(leftName) || string.IsNullOrWhiteSpace(rightName))
        {
            leftName = mapping?.LeftWorksheetName.Trim();
            rightName = mapping?.RightWorksheetName.Trim();
        }
        if (string.IsNullOrWhiteSpace(leftName) || string.IsNullOrWhiteSpace(rightName))
        {
            leftName = LeftWorksheetNames.FirstOrDefault(name =>
                RightWorksheetNames.Contains(name, StringComparer.OrdinalIgnoreCase)) ??
                LeftWorksheetNames.FirstOrDefault();
            rightName = leftName is not null
                ? RightWorksheetNames.FirstOrDefault(name =>
                    string.Equals(name, leftName, StringComparison.OrdinalIgnoreCase))
                : null;
            rightName ??= RightWorksheetNames.FirstOrDefault();
        }

        if (SuggestionLeftWorksheetComboBox.Text.Trim().Length == 0 && !string.IsNullOrWhiteSpace(leftName))
        {
            SuggestionLeftWorksheetComboBox.Text = leftName;
        }
        if (SuggestionRightWorksheetComboBox.Text.Trim().Length == 0 && !string.IsNullOrWhiteSpace(rightName))
        {
            SuggestionRightWorksheetComboBox.Text = rightName;
        }

        SetSuggestedHeaderRow(SuggestionLeftHeaderRowTextBox, leftName);
        SetSuggestedHeaderRow(SuggestionRightHeaderRowTextBox, rightName);
    }

    private void SetSuggestedHeaderRow(TextBox textBox, string? worksheetName)
    {
        if (worksheetName is null)
        {
            return;
        }

        var rule = KeyRuleRows.FirstOrDefault(row =>
            string.Equals(row.WorksheetName.Trim(), worksheetName, StringComparison.OrdinalIgnoreCase));
        if (rule is not null)
        {
            textBox.Text = rule.HeaderRow;
        }
    }

    private static bool TryBrowseSuggestionFile(string currentPath, out string filePath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择用于建议分析的 XLSX 文件",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (File.Exists(currentPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(currentPath));
            dialog.FileName = Path.GetFileName(currentPath);
        }

        var accepted = dialog.ShowDialog() == true;
        filePath = accepted ? dialog.FileName : string.Empty;
        return accepted;
    }

    internal static bool TryValidateSuggestionPath(string path, string side, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = $"{side}文件路径为空，请选择一个 XLSX 文件。";
            return false;
        }
        if (!File.Exists(path))
        {
            error = Directory.Exists(path)
                ? $"{side}当前是文件夹，请选择其中一个具体的 XLSX 文件。"
                : $"{side}文件不存在，请重新选择。";
            return false;
        }
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{side}文件不是 .xlsx 工作簿。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseHeaderRow(string text, out int headerRow) =>
        int.TryParse(text.Trim(), out headerRow) && headerRow >= 1;

    private void ShowSuggestionMessage(string message) =>
        MessageBox.Show(this, message, "智能对齐建议", MessageBoxButton.OK, MessageBoxImage.Information);

    private static void RemoveSelected<T>(DataGrid grid, ObservableCollection<T> rows)
    {
        foreach (var item in grid.SelectedItems.Cast<T>().ToArray())
        {
            rows.Remove(item);
        }
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        ManualPairsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        KeyRulesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ColumnMappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        if (!TryBuildSettings(out var error))
        {
            MessageBox.Show(this, error, "工作表与行列对齐设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private bool TryBuildSettings(out string error)
    {
        var manualPairs = ManualPairRows
            .Where(static row => !row.IsBlank)
            .Select(static row => new WorksheetPair(
                row.LeftWorksheetName.Trim(),
                row.RightWorksheetName.Trim()))
            .ToArray();
        if (manualPairs.Any(static pair =>
                pair.LeftWorksheetName.Length == 0 || pair.RightWorksheetName.Length == 0))
        {
            error = "手工配对的左右工作表名称都必须填写。";
            return false;
        }
        if (PairingMode == WorksheetPairingMode.Manual && manualPairs.Length == 0)
        {
            error = "手工配对模式至少需要一组工作表。";
            return false;
        }
        if (HasDuplicate(manualPairs.Select(static pair => pair.LeftWorksheetName)) ||
            HasDuplicate(manualPairs.Select(static pair => pair.RightWorksheetName)))
        {
            error = "同一工作表不能在手工配对中重复出现。";
            return false;
        }

        var keyRules = new List<KeyColumnRule>();
        foreach (var row in KeyRuleRows.Where(static row => !row.IsBlank))
        {
            if (string.IsNullOrWhiteSpace(row.WorksheetName) ||
                !int.TryParse(row.HeaderRow, out var headerRow) || headerRow < 1)
            {
                error = "关键列规则需要工作表名称和大于等于 1 的表头行。";
                return false;
            }

            var columns = SplitColumns(row.Columns);
            if (columns.Length == 0 || columns.Any(static column => !IsValidColumn(column)))
            {
                error = $"工作表“{row.WorksheetName.Trim()}”的关键列无效，请填写 A 到 XFD 的列字母。";
                return false;
            }
            if (HasDuplicate(columns))
            {
                error = $"工作表“{row.WorksheetName.Trim()}”包含重复关键列。";
                return false;
            }

            keyRules.Add(new KeyColumnRule(row.WorksheetName.Trim(), headerRow, columns));
        }
        if (UseKeyColumnAlignment && keyRules.Count == 0)
        {
            error = "启用关键列对齐后，至少需要一条关键列规则。";
            return false;
        }
        if (HasDuplicate(keyRules.Select(static rule => rule.WorksheetName)))
        {
            error = "同一工作表只能配置一条关键列规则。";
            return false;
        }

        var mappings = new List<WorksheetColumnMapping>();
        foreach (var row in ColumnMappingRows.Where(static row => !row.IsBlank))
        {
            if (string.IsNullOrWhiteSpace(row.LeftWorksheetName) ||
                string.IsNullOrWhiteSpace(row.RightWorksheetName))
            {
                error = "左右列映射的两个工作表名称都必须填写。";
                return false;
            }

            var pairs = new List<ColumnPair>();
            foreach (var text in SplitItems(row.ColumnPairs))
            {
                var separator = text.IndexOf('=');
                if (separator <= 0 || separator != text.LastIndexOf('='))
                {
                    error = $"列对“{text}”无效，请使用 A=B 格式。";
                    return false;
                }

                var leftColumn = text[..separator].Trim().ToUpperInvariant();
                var rightColumn = text[(separator + 1)..].Trim().ToUpperInvariant();
                if (!IsValidColumn(leftColumn) || !IsValidColumn(rightColumn))
                {
                    error = $"列对“{text}”无效，列字母必须在 A 到 XFD 范围内。";
                    return false;
                }
                pairs.Add(new ColumnPair(leftColumn, rightColumn));
            }
            if (pairs.Count == 0 ||
                HasDuplicate(pairs.Select(static pair => pair.LeftColumnIdentifier)) ||
                HasDuplicate(pairs.Select(static pair => pair.RightColumnIdentifier)))
            {
                error = "每条左右列映射至少需要一组列，且左右列均不能重复。";
                return false;
            }

            mappings.Add(new WorksheetColumnMapping(
                row.LeftWorksheetName.Trim(),
                row.RightWorksheetName.Trim(),
                pairs));
        }
        if (mappings.GroupBy(static mapping =>
                $"{mapping.LeftWorksheetName}\0{mapping.RightWorksheetName}",
                StringComparer.OrdinalIgnoreCase).Any(static group => group.Count() > 1))
        {
            error = "同一组左右工作表只能配置一条列映射。";
            return false;
        }

        ManualPairs = manualPairs;
        KeyColumnRules = keyRules;
        ColumnMappings = mappings;
        error = string.Empty;
        return true;
    }

    private static string[] SplitColumns(string value) => SplitItems(value)
        .Select(static column => column.ToUpperInvariant())
        .ToArray();

    private static string[] SplitItems(string value) => value
        .Split([',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool IsValidColumn(string value) =>
        ExcelAddress.TryParse(value + "1") is { Column: <= 16_384 };

    private static bool HasDuplicate(IEnumerable<string> values) =>
        values.GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Any(static group => group.Count() > 1);
}

public sealed class ManualPairEditorRow
{
    public string LeftWorksheetName { get; set; } = string.Empty;

    public string RightWorksheetName { get; set; } = string.Empty;

    public bool IsBlank => string.IsNullOrWhiteSpace(LeftWorksheetName) &&
        string.IsNullOrWhiteSpace(RightWorksheetName);
}

public sealed class KeyRuleEditorRow
{
    public string WorksheetName { get; set; } = string.Empty;

    public string HeaderRow { get; set; } = "1";

    public string Columns { get; set; } = string.Empty;

    public bool IsBlank => string.IsNullOrWhiteSpace(WorksheetName) &&
        string.IsNullOrWhiteSpace(Columns);
}

public sealed class ColumnMappingEditorRow
{
    public string LeftWorksheetName { get; set; } = string.Empty;

    public string RightWorksheetName { get; set; } = string.Empty;

    public string ColumnPairs { get; set; } = string.Empty;

    public bool IsBlank => string.IsNullOrWhiteSpace(LeftWorksheetName) &&
        string.IsNullOrWhiteSpace(RightWorksheetName) &&
        string.IsNullOrWhiteSpace(ColumnPairs);
}

public sealed class AlignmentSuggestionEditorRow(AlignmentSuggestion suggestion)
{
    public AlignmentSuggestion Suggestion { get; } = suggestion;

    public bool IsSelected { get; set; }

    public bool CanApply => Suggestion.CanApply;

    public string KindDisplay => Suggestion.Kind switch
    {
        AlignmentSuggestionKind.KeyColumns => "关键列",
        AlignmentSuggestionKind.ColumnMapping => "列映射",
        AlignmentSuggestionKind.GroupingColumn => "分组列",
        _ => Suggestion.Kind.ToString(),
    };

    public string Title => Suggestion.Title;

    public string ConfidenceDisplay => $"{Suggestion.ConfidencePercent:F0}%";

    public string LeftColumnsDisplay => string.Join(",", Suggestion.LeftColumns);

    public string RightColumnsDisplay => string.Join(",", Suggestion.RightColumns);

    public string Reason => Suggestion.Reason;

    public string Details
    {
        get
        {
            var metrics = $"样本行：左 {Suggestion.LeftSampledRows} / 右 {Suggestion.RightSampledRows}；" +
                $"覆盖率：左 {Suggestion.LeftCoveragePercent:F1}% / 右 {Suggestion.RightCoveragePercent:F1}%；" +
                $"唯一率：左 {Suggestion.LeftUniquenessPercent:F1}% / 右 {Suggestion.RightUniquenessPercent:F1}%；" +
                $"交叉覆盖 {Suggestion.CrossCoveragePercent:F1}%";
            var samples = Suggestion.Samples.Count == 0
                ? string.Empty
                : $"\n样本：{string.Join("；", Suggestion.Samples.Take(3))}";
            var applyHint = Suggestion.CanApply
                ? string.Empty
                : "\n此建议仅供参考，不能直接应用。";
            return $"{Suggestion.Reason}\n{metrics}{samples}{applyHint}";
        }
    }
}

internal sealed record SuggestionApplyResult(
    int AppliedSuggestionCount,
    int SkippedSuggestionCount,
    bool AppliedKeyColumns,
    bool AppliedColumnMappings,
    string Message);

internal static class AlignmentSuggestionPresentation
{
    public static string FormatCompletedStatus(AlignmentSuggestionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var status = result.Suggestions.Count == 0
            ? "未发现可信的关键列或列映射建议。"
            : $"分析完成，共 {result.Suggestions.Count} 条建议；请勾选后应用。";
        var truncated = new List<string>(4);
        if (result.LeftRowsTruncated)
        {
            truncated.Add($"左侧前 {result.LeftSampledRows} 行");
        }
        if (result.RightRowsTruncated)
        {
            truncated.Add($"右侧前 {result.RightSampledRows} 行");
        }
        if (result.LeftColumnsTruncated)
        {
            truncated.Add("左侧列样本");
        }
        if (result.RightColumnsTruncated)
        {
            truncated.Add("右侧列样本");
        }

        return truncated.Count == 0
            ? status
            : $"{status} 注意：{string.Join("、", truncated)}已截断，置信度不代表全表。";
    }
}

internal static class AlignmentSuggestionApplicator
{
    public static SuggestionApplyResult Apply(
        IEnumerable<AlignmentSuggestion> suggestions,
        string leftWorksheetName,
        string rightWorksheetName,
        int leftHeaderRow,
        int rightHeaderRow,
        IList<KeyRuleEditorRow> keyRules,
        IList<ColumnMappingEditorRow> columnMappings)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        ArgumentException.ThrowIfNullOrWhiteSpace(leftWorksheetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightWorksheetName);
        ArgumentNullException.ThrowIfNull(keyRules);
        ArgumentNullException.ThrowIfNull(columnMappings);

        var applied = 0;
        var skipped = 0;
        var appliedKeys = false;
        var appliedMappings = false;
        var keySuggestionAccepted = false;
        var notes = new List<string>();
        foreach (var suggestion in suggestions)
        {
            if (!suggestion.CanApply)
            {
                skipped++;
                continue;
            }

            switch (suggestion.Kind)
            {
                case AlignmentSuggestionKind.KeyColumns:
                {
                    if (keySuggestionAccepted)
                    {
                        skipped++;
                        notes.Add("关键列候选互为替代方案，仅应用了所选列表中的第一条。");
                        break;
                    }

                    var leftColumns = NormalizeColumns(suggestion.LeftColumns);
                    var rightColumns = NormalizeColumns(suggestion.RightColumns);
                    if (leftColumns.Length == 0 || rightColumns.Length == 0)
                    {
                        skipped++;
                        break;
                    }

                    if (string.Equals(
                            leftWorksheetName,
                            rightWorksheetName,
                            StringComparison.OrdinalIgnoreCase) &&
                        !leftColumns.SequenceEqual(rightColumns, StringComparer.OrdinalIgnoreCase))
                    {
                        skipped++;
                        notes.Add("同名工作表的左右关键列不同，现有规则无法无歧义表示，已跳过该建议。");
                        break;
                    }

                    UpsertKeyRule(keyRules, leftWorksheetName, leftHeaderRow, leftColumns);
                    if (!string.Equals(
                            leftWorksheetName,
                            rightWorksheetName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        UpsertKeyRule(keyRules, rightWorksheetName, rightHeaderRow, rightColumns);
                    }
                    applied++;
                    appliedKeys = true;
                    keySuggestionAccepted = true;
                    break;
                }
                case AlignmentSuggestionKind.ColumnMapping:
                    if (suggestion.ColumnPairs.Count == 0)
                    {
                        skipped++;
                        break;
                    }

                    UpsertColumnMapping(
                        columnMappings,
                        leftWorksheetName,
                        rightWorksheetName,
                        suggestion.ColumnPairs);
                    applied++;
                    appliedMappings = true;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        var message = applied == 0
            ? "所选建议均为只读建议或无法安全应用。"
            : $"已填入 {applied} 条建议；请检查规则，点击“确定”后才会保存。";
        if (skipped > 0)
        {
            message += $" 已跳过 {skipped} 条。";
        }
        if (notes.Count > 0)
        {
            message += " " + string.Join(" ", notes.Distinct(StringComparer.Ordinal));
        }

        return new SuggestionApplyResult(applied, skipped, appliedKeys, appliedMappings, message);
    }

    private static void UpsertKeyRule(
        IList<KeyRuleEditorRow> rows,
        string worksheetName,
        int headerRow,
        IReadOnlyList<string> columns)
    {
        var row = rows.FirstOrDefault(candidate => string.Equals(
            candidate.WorksheetName.Trim(),
            worksheetName.Trim(),
            StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            rows.Add(new KeyRuleEditorRow
            {
                WorksheetName = worksheetName.Trim(),
                HeaderRow = headerRow.ToString(),
                Columns = string.Join(',', columns),
            });
            return;
        }

        row.WorksheetName = worksheetName.Trim();
        row.HeaderRow = headerRow.ToString();
        row.Columns = string.Join(',', columns);
    }

    private static void UpsertColumnMapping(
        IList<ColumnMappingEditorRow> rows,
        string leftWorksheetName,
        string rightWorksheetName,
        IReadOnlyList<ColumnPair> suggestions)
    {
        var row = rows.FirstOrDefault(candidate =>
            string.Equals(
                candidate.LeftWorksheetName.Trim(),
                leftWorksheetName.Trim(),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                candidate.RightWorksheetName.Trim(),
                rightWorksheetName.Trim(),
                StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new ColumnMappingEditorRow
            {
                LeftWorksheetName = leftWorksheetName.Trim(),
                RightWorksheetName = rightWorksheetName.Trim(),
            };
            rows.Add(row);
        }

        var pairs = ParseExistingPairs(row.ColumnPairs);
        foreach (var suggestion in suggestions)
        {
            var left = suggestion.LeftColumnIdentifier.Trim().ToUpperInvariant();
            var right = suggestion.RightColumnIdentifier.Trim().ToUpperInvariant();
            pairs.RemoveAll(pair =>
                string.Equals(pair.LeftColumnIdentifier, left, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.RightColumnIdentifier, right, StringComparison.OrdinalIgnoreCase));
            pairs.Add(new ColumnPair(left, right));
        }
        row.ColumnPairs = string.Join(",", pairs.Select(static pair =>
            $"{pair.LeftColumnIdentifier}={pair.RightColumnIdentifier}"));
    }

    private static List<ColumnPair> ParseExistingPairs(string value)
    {
        var pairs = new List<ColumnPair>();
        foreach (var text in value.Split(
                     [',', '，', ';', '；'],
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = text.IndexOf('=');
            if (separator <= 0 || separator != text.LastIndexOf('='))
            {
                continue;
            }
            pairs.Add(new ColumnPair(
                text[..separator].Trim().ToUpperInvariant(),
                text[(separator + 1)..].Trim().ToUpperInvariant()));
        }
        return pairs;
    }

    private static string[] NormalizeColumns(IEnumerable<string> columns) => columns
        .Select(static column => column.Trim().ToUpperInvariant())
        .Where(static column => column.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
