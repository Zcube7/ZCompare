using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ZCompare.App.Infrastructure;
using ZCompare.App.Services;
using ZCompare.Core;

namespace ZCompare.App.ViewModels;

internal sealed class MainWindowViewModel : ObservableObject
{
    private readonly IWorkbookReader _workbookReader;
    private readonly IWorkbookComparer _workbookComparer;
    private readonly IFolderComparer _folderComparer;
    private readonly IPathDialogService _pathDialogService;
    private readonly IRecentComparisonStore? _recentComparisonStore;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly LruCache<string, WorksheetPreview> _previewCache = new(4);
    private readonly LruCache<string, WorkbookCompareResult> _folderComparisonCache = new(4);
    private readonly List<FileSystemWatcher> _sourceWatchers = [];
    private readonly Stopwatch _operationStopwatch = new();
    private readonly DispatcherTimer _operationElapsedTimer;
    private readonly Dictionary<string, WorksheetCompareResult> _worksheetResults =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorksheetInfo> _leftWorksheetInfo =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorksheetInfo> _rightWorksheetInfo =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DifferenceItemViewModel> _staticDifferences = [];
    private readonly Dictionary<string, FolderFileItemViewModel> _folderFilesByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private CompareMode _mode;
    private RecentComparisonEntry? _selectedRecentComparison;
    private bool _applyingRecentComparison;
    private ComparisonOptions _comparisonOptionsTemplate = new();
    private string _leftPath = string.Empty;
    private string _rightPath = string.Empty;
    private bool _compareFormulas;
    private bool _compareFormatting;
    private bool _compareFonts;
    private bool _compareComments;
    private bool _compareHyperlinks;
    private bool _compareLayout;
    private bool _caseSensitive = true;
    private bool _strictRowNumberComparison;
    private bool _useKeyColumnAlignment;
    private bool _includeSubdirectories;
    private string _folderFilePattern = "*.xlsx";
    private bool _isBusy;
    private bool _isPreviewBusy;
    private bool _isWorkbookOpen;
    private bool _showFolderDifferencesOnly;
    private string _folderSearchText = string.Empty;
    private FolderFileItemViewModel? _focusedFolderFile;
    private bool _showWorkbookDifferencesOnly;
    private bool _hasComparisonResults;
    private bool _resultsAreStale;
    private string _statusText = "请选择左右两侧进行比较";
    private string _currentProgressItem = string.Empty;
    private string _operationElapsedText = "耗时 00:00:00";
    private string _warningText = string.Empty;
    private string _otherDetailsText = string.Empty;
    private int _progressPercent;
    private bool _progressIsIndeterminate;
    private WorksheetTabViewModel? _selectedWorksheet;
    private string _currentLeftFile = string.Empty;
    private string _currentRightFile = string.Empty;
    private string _selectedAddress = "—";
    private string _leftSelectedRaw = string.Empty;
    private string _rightSelectedRaw = string.Empty;
    private string _leftSelectedDisplay = string.Empty;
    private string _rightSelectedDisplay = string.Empty;
    private string _leftSelectedFormula = string.Empty;
    private string _rightSelectedFormula = string.Empty;
    private string? _loadedWorksheetName;
    private int _differenceNavigationIndex = -1;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _previewCancellation;
    private WorkbookCompareResult? _activeComparison;
    private TimeSpan _folderComparisonElapsed;
    private bool _sourceVerificationScheduled;
    private bool _sourceVerificationPending;

    public MainWindowViewModel(
        IWorkbookReader workbookReader,
        IWorkbookComparer workbookComparer,
        IFolderComparer folderComparer,
        IPathDialogService pathDialogService,
        IRecentComparisonStore? recentComparisonStore = null)
    {
        _workbookReader = workbookReader;
        _workbookComparer = workbookComparer;
        _folderComparer = folderComparer;
        _pathDialogService = pathDialogService;
        _recentComparisonStore = recentComparisonStore;
        _operationElapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _operationElapsedTimer.Tick += (_, _) => UpdateOperationElapsed();

        BrowseCommand = new RelayCommand<string>(Browse);
        SwapCommand = new RelayCommand(SwapPaths, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartPrimaryAsync, CanStart);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy || IsPreviewBusy);
        RefreshCommand = new AsyncRelayCommand(StartPrimaryAsync, CanStart);
        BackToFolderCommand = new RelayCommand(CloseWorkbook, () => IsFolderMode && IsWorkbookOpen);
        CompareSelectedCommand = new AsyncRelayCommand(CompareSelectedAsync, CanCompareSelected);
        OpenFolderItemCommand = new AsyncRelayCommand<FolderFileItemViewModel>(OpenFolderItemAsync);
        PreviousDifferenceCommand = new AsyncRelayCommand(() => MoveDifferenceAsync(-1));
        NextDifferenceCommand = new AsyncRelayCommand(() => MoveDifferenceAsync(1));

        FolderFilesView = CollectionViewSource.GetDefaultView(FolderFiles);
        FolderFilesView.Filter = FilterFolderFile;
        FolderFiles.CollectionChanged += FolderFiles_OnCollectionChanged;

        foreach (var entry in _recentComparisonStore?.Load() ?? [])
        {
            RecentComparisons.Add(entry);
        }
    }

    public event EventHandler<GridNavigationEventArgs>? GridNavigationRequested;

    public ObservableCollection<FolderFileItemViewModel> FolderFiles { get; } = [];

    public ObservableCollection<RecentComparisonEntry> RecentComparisons { get; } = [];

    public ICollectionView FolderFilesView { get; }

    public string FolderSelectionSummary =>
        $"已选 {FolderFiles.Count(static item => item.IsMarkedForComparison)} 项 · 可深比 {FolderFiles.Count(static item => item.IsMarkedForComparison && item.HasLeft && item.HasRight)} 组";

    public ObservableCollection<WorksheetTabViewModel> Worksheets { get; } = [];

    public BulkObservableCollection<DifferenceItemViewModel> Differences { get; } = [];

    public GridViewportViewModel GridViewport { get; } = new();

    public ICommand BrowseCommand { get; }

    public ICommand SwapCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand BackToFolderCommand { get; }

    public ICommand CompareSelectedCommand { get; }

    public ICommand OpenFolderItemCommand { get; }

    public ICommand PreviousDifferenceCommand { get; }

    public ICommand NextDifferenceCommand { get; }

    public RecentComparisonEntry? SelectedRecentComparison
    {
        get => _selectedRecentComparison;
        set
        {
            if (!SetProperty(ref _selectedRecentComparison, value) || value is null)
            {
                return;
            }

            _applyingRecentComparison = true;
            try
            {
                SetMode(value.Mode == RecentComparisonMode.Files ? CompareMode.Files : CompareMode.Folders);
                LeftPath = value.LeftPath;
                RightPath = value.RightPath;
                var options = value.EffectiveOptions with
                {
                    KeyColumnRules = value.EffectiveOptions.KeyColumnRules ?? [],
                    ManualWorksheetPairs = value.EffectiveOptions.ManualWorksheetPairs ?? [],
                    ColumnMappings = value.EffectiveOptions.ColumnMappings ?? [],
                };
                _comparisonOptionsTemplate = options;
                CompareFormulas = options.CompareFormulas;
                CompareFormatting = options.CompareFormatting;
                CompareFonts = options.CompareFonts;
                CompareComments = options.CompareComments;
                CompareHyperlinks = options.CompareHyperlinks;
                CompareLayout = options.CompareLayout;
                CaseSensitive = options.CaseSensitive;
                _useKeyColumnAlignment = options.RowAlignmentMode == RowAlignmentMode.KeyColumns;
                OnPropertyChanged(nameof(UseKeyColumnAlignment));
                OnPropertyChanged(nameof(WorksheetSettingsSummary));
                StrictRowNumberComparison = options.RowAlignmentMode == RowAlignmentMode.StrictRowNumber;
                IncludeSubdirectories = value.IncludeSubdirectories;
                FolderFilePattern = value.EffectiveFilePattern;
            }
            finally
            {
                _applyingRecentComparison = false;
            }

            WarningText = string.Empty;
            StatusText = $"已载入{(value.IsProfile ? $"配置“{value.Name}”" : "最近对比")}，请点击“{PrimaryActionText}”继续";
        }
    }

    public bool IsFileMode
    {
        get => _mode == CompareMode.Files;
        set
        {
            if (value)
            {
                SetMode(CompareMode.Files);
            }
        }
    }

    public bool IsFolderMode
    {
        get => _mode == CompareMode.Folders;
        set
        {
            if (value)
            {
                SetMode(CompareMode.Folders);
            }
        }
    }

    public string PrimaryActionText => IsFileMode ? "开始对比" : "扫描目录";

    public string LeftPath
    {
        get => _leftPath;
        set
        {
            if (SetProperty(ref _leftPath, value))
            {
                ClearRecentComparisonSelection();
                InvalidateResultsForPathChange();
                OnPropertyChanged(nameof(CanSaveProfile));
                NotifyCommandStates();
            }
        }
    }

    public string RightPath
    {
        get => _rightPath;
        set
        {
            if (SetProperty(ref _rightPath, value))
            {
                ClearRecentComparisonSelection();
                InvalidateResultsForPathChange();
                OnPropertyChanged(nameof(CanSaveProfile));
                NotifyCommandStates();
            }
        }
    }

    public bool CompareFormulas
    {
        get => _compareFormulas;
        set
        {
            if (SetProperty(ref _compareFormulas, value))
            {
                ClearRecentComparisonSelection();
                MarkResultsStale();
            }
        }
    }

    public bool CompareFormatting
    {
        get => _compareFormatting;
        set
        {
            if (SetProperty(ref _compareFormatting, value))
            {
                ClearRecentComparisonSelection();
                MarkResultsStale();
            }
        }
    }

    public bool CompareFonts
    {
        get => _compareFonts;
        set
        {
            if (SetProperty(ref _compareFonts, value))
            {
                ClearRecentComparisonSelection();
                MarkResultsStale();
            }
        }
    }

    public bool CompareComments
    {
        get => _compareComments;
        set
        {
            if (SetProperty(ref _compareComments, value))
            {
                ClearRecentComparisonSelection();
                MarkResultsStale();
            }
        }
    }

    public bool CompareHyperlinks
    {
        get => _compareHyperlinks;
        set
        {
            if (SetProperty(ref _compareHyperlinks, value))
            {
                ClearRecentComparisonSelection();
                MarkResultsStale();
            }
        }
    }

    public bool CompareLayout
    {
        get => _compareLayout;
        set
        {
            if (SetProperty(ref _compareLayout, value))
            {
                ClearRecentComparisonSelection();
                MarkResultsStale();
            }
        }
    }

    public bool CaseSensitive
    {
        get => _caseSensitive;
        set
        {
            if (SetProperty(ref _caseSensitive, value))
            {
                ClearRecentComparisonSelection();
                MarkResultsStale();
            }
        }
    }

    public bool StrictRowNumberComparison
    {
        get => _strictRowNumberComparison;
        set
        {
            if (SetProperty(ref _strictRowNumberComparison, value))
            {
                if (value && _useKeyColumnAlignment)
                {
                    _useKeyColumnAlignment = false;
                    OnPropertyChanged(nameof(UseKeyColumnAlignment));
                }

                OnPropertyChanged(nameof(WorksheetSettingsSummary));
                ClearRecentComparisonSelection();
                MarkResultsStale();
            }
        }
    }

    public bool UseKeyColumnAlignment => _useKeyColumnAlignment;

    public ComparisonOptions CurrentComparisonOptions => CreateOptions();

    public string WorksheetSettingsSummary
    {
        get
        {
            var options = CreateOptions();
            var worksheetMode = options.WorksheetPairingMode switch
            {
                WorksheetPairingMode.Index => "按顺序配对",
                WorksheetPairingMode.Manual => $"手工配对 {options.ManualWorksheetPairs.Count} 组",
                _ => "按名称配对",
            };
            var alignment = options.RowAlignmentMode == RowAlignmentMode.KeyColumns
                ? $"关键列 {options.KeyColumnRules.Count} 条"
                : options.RowAlignmentMode == RowAlignmentMode.StrictRowNumber
                    ? "严格原行号"
                    : "保守行对齐";
            var columnMappings = options.ColumnMappings.Count == 0
                ? string.Empty
                : $" · 列映射 {options.ColumnMappings.Count} 组";
            return $"{worksheetMode} · {alignment}{columnMappings}";
        }
    }

    public bool IncludeSubdirectories
    {
        get => _includeSubdirectories;
        set
        {
            if (SetProperty(ref _includeSubdirectories, value))
            {
                ClearRecentComparisonSelection();
                MarkResultsStale();
            }
        }
    }

    public string FolderFilePattern
    {
        get => _folderFilePattern;
        set
        {
            if (SetProperty(ref _folderFilePattern, value ?? string.Empty))
            {
                ClearRecentComparisonSelection();
                MarkResultsStale();
                NotifyCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEditInputs));
                OnPropertyChanged(nameof(CanSaveProfile));
                OnPropertyChanged(nameof(CanExportReport));
                NotifyCommandStates();
            }
        }
    }

    public bool IsPreviewBusy
    {
        get => _isPreviewBusy;
        private set
        {
            if (SetProperty(ref _isPreviewBusy, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public bool CanEditInputs => !IsBusy;

    public bool CanSaveProfile => !IsBusy &&
        !string.IsNullOrWhiteSpace(LeftPath) &&
        !string.IsNullOrWhiteSpace(RightPath);

    public bool CanExportReport => !IsBusy && !_resultsAreStale &&
        ((IsWorkbookOpen && _activeComparison is not null) ||
         (IsFolderMode && !IsWorkbookOpen && FolderFiles.Any(static item => item.Status != ComparisonStatus.Pending)));

    public bool IsWorkbookOpen
    {
        get => _isWorkbookOpen;
        private set
        {
            if (SetProperty(ref _isWorkbookOpen, value))
            {
                OnPropertyChanged(nameof(ShowFolderResults));
                OnPropertyChanged(nameof(ShowStartHint));
                OnPropertyChanged(nameof(CanExportReport));
                NotifyCommandStates();
            }
        }
    }

    public bool ShowFolderResults => IsFolderMode && !IsWorkbookOpen;

    public bool ShowStartHint => IsFileMode && !IsWorkbookOpen;

    public bool ShowFolderDifferencesOnly
    {
        get => _showFolderDifferencesOnly;
        set
        {
            if (SetProperty(ref _showFolderDifferencesOnly, value))
            {
                OnPropertyChanged(nameof(ShowAllFolderFiles));
                FolderFilesView.Refresh();
            }
        }
    }

    public bool ShowAllFolderFiles
    {
        get => !ShowFolderDifferencesOnly;
        set
        {
            if (value)
            {
                ShowFolderDifferencesOnly = false;
            }
        }
    }

    public string FolderSearchText
    {
        get => _folderSearchText;
        set
        {
            if (SetProperty(ref _folderSearchText, value))
            {
                FolderFilesView.Refresh();
            }
        }
    }

    public FolderFileItemViewModel? FocusedFolderFile
    {
        get => _focusedFolderFile;
        set => SetProperty(ref _focusedFolderFile, value);
    }

    public bool ShowWorkbookDifferencesOnly
    {
        get => _showWorkbookDifferencesOnly;
        set
        {
            if (SetProperty(ref _showWorkbookDifferencesOnly, value))
            {
                OnPropertyChanged(nameof(ShowAllWorkbookRows));
                GridViewport.SetDifferencesOnly(value);
                ClearSelectedCell();
            }
        }
    }

    public bool ShowAllWorkbookRows
    {
        get => !ShowWorkbookDifferencesOnly;
        set
        {
            if (value)
            {
                ShowWorkbookDifferencesOnly = false;
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentProgressItem
    {
        get => _currentProgressItem;
        private set
        {
            if (SetProperty(ref _currentProgressItem, value))
            {
                OnPropertyChanged(nameof(CurrentProgressFileName));
            }
        }
    }

    public string CurrentProgressFileName => string.IsNullOrWhiteSpace(CurrentProgressItem)
        ? "—"
        : Path.GetFileName(CurrentProgressItem);

    public string OperationElapsedText
    {
        get => _operationElapsedText;
        private set => SetProperty(ref _operationElapsedText, value);
    }

    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public string OtherDetailsText
    {
        get => _otherDetailsText;
        private set
        {
            if (SetProperty(ref _otherDetailsText, value))
            {
                OnPropertyChanged(nameof(HasOtherDetails));
            }
        }
    }

    public bool HasOtherDetails => !string.IsNullOrWhiteSpace(OtherDetailsText);

    public int ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool ProgressIsIndeterminate
    {
        get => _progressIsIndeterminate;
        private set => SetProperty(ref _progressIsIndeterminate, value);
    }

    public WorksheetTabViewModel? SelectedWorksheet
    {
        get => _selectedWorksheet;
        set
        {
            if (SetProperty(ref _selectedWorksheet, value) && value is not null)
            {
                _ = LoadWorksheetSafeAsync(value);
            }
        }
    }

    public string CurrentLeftFile
    {
        get => _currentLeftFile;
        private set => SetProperty(ref _currentLeftFile, value);
    }

    public string CurrentRightFile
    {
        get => _currentRightFile;
        private set => SetProperty(ref _currentRightFile, value);
    }

    public string CurrentLeftFileName => string.IsNullOrEmpty(CurrentLeftFile) ? "（空白）" : Path.GetFileName(CurrentLeftFile);

    public string CurrentRightFileName => string.IsNullOrEmpty(CurrentRightFile) ? "（空白）" : Path.GetFileName(CurrentRightFile);

    public string SelectedAddress
    {
        get => _selectedAddress;
        private set
        {
            if (SetProperty(ref _selectedAddress, value))
            {
                OnPropertyChanged(nameof(HasSelectedCell));
            }
        }
    }

    public bool HasSelectedCell => !string.IsNullOrWhiteSpace(SelectedAddress) && SelectedAddress != "—";

    public string LeftSelectedRaw
    {
        get => _leftSelectedRaw;
        private set => SetProperty(ref _leftSelectedRaw, value);
    }

    public string RightSelectedRaw
    {
        get => _rightSelectedRaw;
        private set => SetProperty(ref _rightSelectedRaw, value);
    }

    public string LeftSelectedDisplay
    {
        get => _leftSelectedDisplay;
        private set => SetProperty(ref _leftSelectedDisplay, value);
    }

    public string RightSelectedDisplay
    {
        get => _rightSelectedDisplay;
        private set => SetProperty(ref _rightSelectedDisplay, value);
    }

    public string LeftSelectedFormula
    {
        get => _leftSelectedFormula;
        private set => SetProperty(ref _leftSelectedFormula, value);
    }

    public string RightSelectedFormula
    {
        get => _rightSelectedFormula;
        private set => SetProperty(ref _rightSelectedFormula, value);
    }

    public void SetDroppedPath(CompareSide side, string path)
    {
        if (!CanEditInputs)
        {
            return;
        }

        if (IsFileMode && (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "文件模式只接受 .xlsx 文件";
            return;
        }

        if (IsFolderMode && !Directory.Exists(path))
        {
            StatusText = "文件夹模式只接受文件夹";
            return;
        }

        if (side == CompareSide.Left)
        {
            LeftPath = path;
        }
        else
        {
            RightPath = path;
        }
    }

    public void SetSelectedFolderFiles(IEnumerable<FolderFileItemViewModel> items)
    {
        var markedItems = items.Distinct().ToHashSet();
        foreach (var item in FolderFiles)
        {
            item.IsMarkedForComparison = markedItems.Contains(item);
        }

        NotifyFolderMarksChanged();
    }

    public void SelectAllFolderFiles()
    {
        foreach (var item in FolderFiles)
        {
            item.IsMarkedForComparison = true;
        }

        NotifyFolderMarksChanged();
    }

    public bool SaveProfile(string name)
    {
        var trimmedName = name.Trim();
        if (_recentComparisonStore is null || trimmedName.Length == 0 || !CanSaveProfile)
        {
            StatusText = "请填写配置名称并选择左右路径";
            return false;
        }

        try
        {
            var entries = _recentComparisonStore.Record(new RecentComparisonEntry(
                IsFileMode ? RecentComparisonMode.Files : RecentComparisonMode.Folders,
                Path.GetFullPath(LeftPath),
                Path.GetFullPath(RightPath),
                DateTimeOffset.UtcNow,
                Name: trimmedName,
                IsProfile: true,
                Options: CreateOptions(),
                IncludeSubdirectories: IncludeSubdirectories,
                FilePattern: EffectiveFolderFilePattern()));
            ReplaceSavedComparisons(entries);
            _selectedRecentComparison = RecentComparisons.FirstOrDefault(item =>
                item.IsProfile && string.Equals(item.Name, trimmedName, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(SelectedRecentComparison));
            StatusText = $"配置“{trimmedName}”已保存；不会自动开始比较";
            WarningText = string.Empty;
            return _selectedRecentComparison is not null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            StatusText = $"保存配置失败：{exception.Message}";
            return false;
        }
    }

    public async Task ExportReportAsync(string outputPath, ComparisonReportFormat format)
    {
        if (!CanExportReport || string.IsNullOrWhiteSpace(outputPath))
        {
            StatusText = "当前没有可导出的比较结果";
            return;
        }

        var workbookResult = IsWorkbookOpen ? _activeComparison : null;
        var folderResult = workbookResult is null && IsFolderMode ? CreateFolderReportResult() : null;
        await RunOperationAsync(async cancellationToken =>
        {
            ResetOperationStatus();
            StatusText = $"正在导出 {format.ToString().ToUpperInvariant()} 报告…";
            if (workbookResult is not null)
            {
                await ComparisonReportExporter.ExportAsync(workbookResult, outputPath, format, cancellationToken);
            }
            else if (folderResult is not null)
            {
                await ComparisonReportExporter.ExportAsync(folderResult, outputPath, format, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("当前没有可导出的比较结果。");
            }

            ProgressIsIndeterminate = false;
            ProgressPercent = 100;
            StatusText = $"报告已导出：{outputPath}";
        });
    }

    public string ReportSuggestedFileName =>
        $"ZCompare-{(IsWorkbookOpen ? "workbook" : "folder")}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";

    public void ApplyWorksheetSettings(
        WorksheetPairingMode pairingMode,
        bool useKeyColumnAlignment,
        IReadOnlyList<WorksheetPair> manualPairs,
        IReadOnlyList<KeyColumnRule> keyColumnRules)
    {
        ApplyWorksheetSettings(
            pairingMode,
            useKeyColumnAlignment,
            manualPairs,
            keyColumnRules,
            _comparisonOptionsTemplate.ColumnMappings);
    }

    public void ApplyWorksheetSettings(
        WorksheetPairingMode pairingMode,
        bool useKeyColumnAlignment,
        IReadOnlyList<WorksheetPair> manualPairs,
        IReadOnlyList<KeyColumnRule> keyColumnRules,
        IReadOnlyList<WorksheetColumnMapping> columnMappings)
    {
        ArgumentNullException.ThrowIfNull(manualPairs);
        ArgumentNullException.ThrowIfNull(keyColumnRules);
        ArgumentNullException.ThrowIfNull(columnMappings);

        var options = CreateOptions() with
        {
            WorksheetPairingMode = pairingMode,
            ManualWorksheetPairs = manualPairs.ToArray(),
            KeyColumnRules = keyColumnRules.ToArray(),
            ColumnMappings = columnMappings.ToArray(),
            RowAlignmentMode = useKeyColumnAlignment
                ? RowAlignmentMode.KeyColumns
                : StrictRowNumberComparison
                    ? RowAlignmentMode.StrictRowNumber
                    : RowAlignmentMode.Conservative,
        };
        _comparisonOptionsTemplate = options;
        if (_useKeyColumnAlignment != useKeyColumnAlignment)
        {
            _useKeyColumnAlignment = useKeyColumnAlignment;
            OnPropertyChanged(nameof(UseKeyColumnAlignment));
        }
        if (useKeyColumnAlignment && _strictRowNumberComparison)
        {
            _strictRowNumberComparison = false;
            OnPropertyChanged(nameof(StrictRowNumberComparison));
        }

        OnPropertyChanged(nameof(CurrentComparisonOptions));
        OnPropertyChanged(nameof(WorksheetSettingsSummary));
        ClearRecentComparisonSelection();
        MarkResultsStale();
        if (!_hasComparisonResults)
        {
            StatusText = "工作表与行列设置已更新，将在下次比较时生效";
            WarningText = string.Empty;
        }
    }

    public void SelectGridCell(int displayRowIndex, int columnIndex)
    {
        var left = GridViewport.GetCell(CompareSide.Left, displayRowIndex, columnIndex);
        var right = GridViewport.GetCell(CompareSide.Right, displayRowIndex, columnIndex);
        var leftAddress = left?.Address is not (null or "—") ? left.Address : null;
        var rightAddress = right?.Address is not (null or "—") ? right.Address : null;
        SelectedAddress = leftAddress is not null && rightAddress is not null &&
            !string.Equals(leftAddress, rightAddress, StringComparison.OrdinalIgnoreCase)
                ? $"{leftAddress} ↔ {rightAddress}"
                : leftAddress ?? rightAddress ?? "—";
        LeftSelectedRaw = left?.RawValue ?? string.Empty;
        RightSelectedRaw = right?.RawValue ?? string.Empty;
        LeftSelectedDisplay = left?.DisplayValue ?? string.Empty;
        RightSelectedDisplay = right?.DisplayValue ?? string.Empty;
        LeftSelectedFormula = left?.Formula ?? string.Empty;
        RightSelectedFormula = right?.Formula ?? string.Empty;
    }

    public string GetCellDialogDetails(int displayRowIndex, int columnIndex) =>
        GetCellDetailsContent(displayRowIndex, columnIndex)?.ClipboardText ?? string.Empty;

    internal CellDetailsContent? GetCellDetailsContent(int displayRowIndex, int columnIndex)
    {
        var left = GridViewport.GetCell(CompareSide.Left, displayRowIndex, columnIndex);
        var right = GridViewport.GetCell(CompareSide.Right, displayRowIndex, columnIndex);
        if (left is null && right is null)
        {
            return null;
        }

        var valueDifference = left?.IsValueDifferent == true || right?.IsValueDifferent == true;
        var segments = new List<DetailTextSegment>();
        AppendCellValueDetails(segments, "左侧", left, right, valueDifference);
        AppendDetailText(segments, Environment.NewLine + Environment.NewLine);
        AppendCellValueDetails(segments, "右侧", right, left, valueDifference);

        var details = new[]
            {
                left?.DifferenceDetails,
                right?.DifferenceDetails,
                left?.AdvancedDifferenceDetails,
                right?.AdvancedDifferenceDetails,
            }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (details.Length > 0)
        {
            AppendDetailText(
                segments,
                Environment.NewLine + Environment.NewLine + "【差异说明】" + Environment.NewLine);
            AppendDetailText(
                segments,
                string.Join(Environment.NewLine + Environment.NewLine, details!));
        }

        return new CellDetailsContent(
            string.Concat(segments.Select(static segment => segment.ClipboardText)),
            segments);
    }

    private static void AppendCellValueDetails(
        List<DetailTextSegment> segments,
        string sideName,
        GridCellViewModel? cell,
        GridCellViewModel? oppositeCell,
        bool valueDifference)
    {
        AppendDetailText(
            segments,
            $"【{sideName}】地址：{cell?.Address ?? "—"}{Environment.NewLine}" +
            $"原始值：{Environment.NewLine}");
        AppendSelectedValue(segments, cell?.RawValue, oppositeCell?.RawValue, valueDifference);
        AppendDetailText(segments, Environment.NewLine + "显示值：" + Environment.NewLine);
        AppendSelectedValue(segments, cell?.DisplayValue, oppositeCell?.DisplayValue, valueDifference);
        if (!string.IsNullOrEmpty(cell?.Formula))
        {
            AppendDetailText(segments, Environment.NewLine + "公式：" + Environment.NewLine + cell.Formula);
        }
    }

    private static void AppendSelectedValue(
        List<DetailTextSegment> segments,
        string? value,
        string? oppositeValue,
        bool valueDifference)
    {
        value ??= string.Empty;
        oppositeValue ??= string.Empty;
        if (value.Length == 0)
        {
            AppendDetailText(
                segments,
                "（空值）",
                string.Empty,
                valueDifference && oppositeValue.Length > 0);
            return;
        }

        var visualizeWhitespace = string.IsNullOrWhiteSpace(value);
        foreach (var segment in TextDifferenceHighlighter.CreateSegments(value, oppositeValue, valueDifference))
        {
            AppendDetailText(
                segments,
                visualizeWhitespace ? VisualizeWhitespace(segment.Text) : segment.Text,
                segment.Text,
                segment.IsDifferent);
        }
    }

    private static string VisualizeWhitespace(string value) => value
        .Replace("\r", "␍", StringComparison.Ordinal)
        .Replace("\n", "␊", StringComparison.Ordinal)
        .Replace("\t", "⇥", StringComparison.Ordinal)
        .Replace(" ", "␠", StringComparison.Ordinal);

    private static void AppendDetailText(
        List<DetailTextSegment> segments,
        string text,
        bool isDifferent = false) =>
        AppendDetailText(segments, text, text, isDifferent);

    private static void AppendDetailText(
        List<DetailTextSegment> segments,
        string displayText,
        string clipboardText,
        bool isDifferent)
    {
        if (displayText.Length == 0 && clipboardText.Length == 0)
        {
            return;
        }

        if (segments.Count > 0 && segments[^1].IsDifferent == isDifferent)
        {
            var previous = segments[^1];
            segments[^1] = previous with
            {
                DisplayText = previous.DisplayText + displayText,
                ClipboardText = previous.ClipboardText + clipboardText,
            };
            return;
        }

        segments.Add(new DetailTextSegment(displayText, clipboardText, isDifferent));
    }

    private void SetMode(CompareMode mode)
    {
        if (_mode == mode || IsBusy)
        {
            return;
        }

        _mode = mode;
        ClearRecentComparisonSelection();
        OnPropertyChanged(nameof(IsFileMode));
        OnPropertyChanged(nameof(IsFolderMode));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(ShowFolderResults));
        OnPropertyChanged(nameof(ShowStartHint));
        CloseWorkbook();
        ClearFolderResults();
        StatusText = mode == CompareMode.Files ? "请选择两个 XLSX 文件" : "请选择两个文件夹并扫描";
        NotifyCommandStates();
    }

    private void Browse(string? sideName)
    {
        if (!CanEditInputs)
        {
            return;
        }

        var side = string.Equals(sideName, "Right", StringComparison.OrdinalIgnoreCase)
            ? CompareSide.Right
            : CompareSide.Left;
        var current = side == CompareSide.Left ? LeftPath : RightPath;
        var selected = IsFileMode
            ? _pathDialogService.SelectWorkbook(current)
            : _pathDialogService.SelectFolder(current);
        if (selected is not null)
        {
            SetDroppedPath(side, selected);
        }
    }

    private void SwapPaths()
    {
        (LeftPath, RightPath) = (RightPath, LeftPath);
        CloseWorkbook();
        ClearFolderResults();
        StatusText = "已交换左右两侧，请重新扫描或比较";
    }

    private bool CanStart() =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(LeftPath) &&
        !string.IsNullOrWhiteSpace(RightPath) &&
        (IsFileMode || !string.IsNullOrWhiteSpace(FolderFilePattern));

    private async Task StartPrimaryAsync()
    {
        if (!ValidateCurrentPaths())
        {
            return;
        }

        RecordRecentComparison();

        await RunOperationAsync(async cancellationToken =>
        {
            ResetOperationStatus();
            if (IsFileMode)
            {
                CloseWorkbook();
                var result = await _workbookComparer.CompareAsync(
                    LeftPath,
                    RightPath,
                    CreateOptions(),
                    new Progress<ComparisonProgress>(UpdateProgress),
                    cancellationToken);
                _hasComparisonResults = true;
                _resultsAreStale = false;
                OnPropertyChanged(nameof(CanExportReport));
                await OpenWorkbookAsync(result.LeftPath, result.RightPath, result, cancellationToken);
                StatusText = FormatCompletedStatus(result.Status, result.DifferenceCount, result.Elapsed);
            }
            else
            {
                CloseWorkbook();
                ClearFolderResults();
                var result = await _folderComparer.ScanAsync(
                    LeftPath,
                    RightPath,
                    CreateFolderScanOptions(),
                    new Progress<ComparisonProgress>(UpdateFolderScanProgress),
                    cancellationToken);
                foreach (var file in result.Files)
                {
                    UpsertFolderFile(file, refresh: false);
                }
                FolderFilesView.Refresh();

                _hasComparisonResults = result.Files.Count > 0;
                _resultsAreStale = false;
                _folderComparisonElapsed = result.Elapsed;
                OnPropertyChanged(nameof(CanExportReport));
                StatusText = $"扫描完成：找到 {result.Files.Count} 个对齐项";
            }

            ProgressPercent = 100;
            ProgressIsIndeterminate = false;
        });
    }

    private bool CanCompareSelected() =>
        !IsBusy && IsFolderMode && FolderFiles.Any(static item =>
            item.IsMarkedForComparison && item.HasLeft && item.HasRight);

    private async Task CompareSelectedAsync()
    {
        var pairedFiles = FolderFiles
            .Where(static item => item.IsMarkedForComparison && item.HasLeft && item.HasRight)
            .ToArray();
        if (pairedFiles.Length == 0)
        {
            StatusText = "请选择至少一个左右均存在的文件行";
            return;
        }

        var options = CreateOptions();
        var maximumConcurrency = Math.Clamp(options.MaxFolderConcurrency, 1, 2);
        var stopwatch = Stopwatch.StartNew();
        await RunOperationAsync(async cancellationToken =>
        {
            ResetOperationStatus();
            var successCount = 0;
            var warningCount = 0;
            var failureCount = 0;
            var completedCount = 0;
            var completed = new bool[pairedFiles.Length];
            var fractions = new double[pairedFiles.Length];
            using var concurrencyGate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
            ProgressIsIndeterminate = false;
            ProgressPercent = 0;

            async Task CompareOneAsync(FolderFileItemViewModel file, int index)
            {
                var enteredGate = false;
                try
                {
                    await concurrencyGate.WaitAsync(cancellationToken);
                    enteredGate = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    var progress = new Progress<ComparisonProgress>(itemProgress =>
                    {
                        if (itemProgress.Total > 0)
                        {
                            var candidate = Math.Clamp(
                                itemProgress.Processed / (double)itemProgress.Total,
                                0d,
                                1d);
                            fractions[index] = Math.Max(fractions[index], candidate);
                        }
                        CurrentProgressItem = file.RelativePath;
                        StatusText = $"正在比较 · 已完成 {Volatile.Read(ref completedCount)}/{pairedFiles.Length} · {itemProgress.Message}";
                        UpdateOperationElapsed();
                        ProgressIsIndeterminate = false;
                        ProgressPercent = Math.Max(
                            ProgressPercent,
                            (int)Math.Round(fractions.Sum() * 100d / pairedFiles.Length));
                    });

                    try
                    {
                        var comparison = await _workbookComparer.CompareAsync(
                            file.LeftPath!,
                            file.RightPath!,
                            options,
                            progress,
                            cancellationToken);
                        file.ApplyComparison(comparison);
                        if (comparison.Status is ComparisonStatus.Error or ComparisonStatus.Cancelled)
                        {
                            Interlocked.Increment(ref failureCount);
                        }
                        else if (comparison.Status == ComparisonStatus.Warning)
                        {
                            Interlocked.Increment(ref warningCount);
                            _folderComparisonCache.Set(
                                ComparisonCacheKey(file.LeftPath!, file.RightPath!),
                                comparison);
                        }
                        else
                        {
                            Interlocked.Increment(ref successCount);
                            _folderComparisonCache.Set(
                                ComparisonCacheKey(file.LeftPath!, file.RightPath!),
                                comparison);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        file.ApplyError(exception.Message);
                        Interlocked.Increment(ref failureCount);
                    }

                    completed[index] = true;
                    var finishedCount = Interlocked.Increment(ref completedCount);
                    fractions[index] = 1d;
                    CurrentProgressItem = file.RelativePath;
                    StatusText = $"已完成 {finishedCount}/{pairedFiles.Length}";
                    UpdateOperationElapsed();
                    ProgressIsIndeterminate = false;
                    ProgressPercent = Math.Max(
                        ProgressPercent,
                        (int)Math.Round(fractions.Sum() * 100d / pairedFiles.Length));
                    FolderFilesView.Refresh();
                }
                finally
                {
                    if (enteredGate)
                    {
                        concurrencyGate.Release();
                    }
                }
            }

            try
            {
                await Task.WhenAll(pairedFiles.Select(CompareOneAsync));
                cancellationToken.ThrowIfCancellationRequested();
                _hasComparisonResults = true;
                _resultsAreStale = false;
                OnPropertyChanged(nameof(CanExportReport));
                StatusText = $"选中项对比完成（{pairedFiles.Length}/{pairedFiles.Length}）：成功 {successCount}，警告 {warningCount}，失败 {failureCount}";
                WarningText = warningCount == 0 && failureCount == 0
                    ? string.Empty
                    : $"警告 {warningCount}，失败 {failureCount}；列表状态栏会直接显示原因，悬停可查看完整内容。";
                ProgressPercent = 100;
                ProgressIsIndeterminate = false;
            }
            catch (OperationCanceledException)
            {
                for (var index = 0; index < pairedFiles.Length; index++)
                {
                    if (!completed[index])
                    {
                        pairedFiles[index].ApplyCancelled();
                    }
                }

                FolderFilesView.Refresh();
                _hasComparisonResults = true;
                _resultsAreStale = false;
                OnPropertyChanged(nameof(CanExportReport));
                ProgressIsIndeterminate = false;
                throw;
            }
        });
        _folderComparisonElapsed = stopwatch.Elapsed;
        OnPropertyChanged(nameof(CanExportReport));
    }

    private async Task OpenFolderItemAsync(FolderFileItemViewModel? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        if (item.HasLeft && item.HasRight)
        {
            if (!_resultsAreStale && item.IsComparisonComplete && item.Status is not (ComparisonStatus.Error or ComparisonStatus.Cancelled))
            {
                await RunOperationAsync(async cancellationToken =>
                {
                    var cacheKey = ComparisonCacheKey(item.LeftPath!, item.RightPath!);
                    if (!_folderComparisonCache.TryGetValue(cacheKey, out var comparison) || comparison is null)
                    {
                        comparison = item.WorkbookComparison;
                    }
                    if (comparison is null)
                    {
                        StatusText = "详细结果已从缓存释放，正在重新比较…";
                        comparison = await _workbookComparer.CompareAsync(
                            item.LeftPath!,
                            item.RightPath!,
                            CreateOptions(),
                            new Progress<ComparisonProgress>(UpdateProgress),
                            cancellationToken);
                        item.ApplyComparison(comparison);
                    }
                    if (!_folderComparisonCache.TryGetValue(cacheKey, out _))
                    {
                        _folderComparisonCache.Set(cacheKey, comparison);
                    }
                    FolderFilesView.Refresh();

                    StatusText = "正在确认源文件未发生变化…";
                    if (!await IsCachedComparisonCurrentAsync(comparison, cancellationToken))
                    {
                        item.InvalidateCachedComparison();
                        FolderFilesView.Refresh();
                        StatusText = "源文件已变化，请重新选择该行并点击“对比”";
                        WarningText = "旧比较结果已作废，未与当前文件预览混合显示。";
                        return;
                    }

                    await OpenWorkbookAsync(item.LeftPath!, item.RightPath!, comparison, cancellationToken);
                });
            }
            else
            {
                StatusText = "请先选择该行并点击“对比”，完成后再双击查看详情";
            }

            return;
        }

        await RunOperationAsync(cancellationToken =>
            OpenWorkbookAsync(item.LeftPath ?? string.Empty, item.RightPath ?? string.Empty, null, cancellationToken));
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await operation(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "操作已取消";
            ProgressIsIndeterminate = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = $"操作失败：{exception.Message}";
            WarningText = "源文件未被修改；请确认文件可读且不是加密工作簿。";
            ProgressIsIndeterminate = false;
        }
        catch (Exception exception)
        {
            StatusText = $"发生错误：{exception.Message}";
            ProgressIsIndeterminate = false;
        }
        finally
        {
            if (_operationStopwatch.IsRunning)
            {
                _operationStopwatch.Stop();
                UpdateOperationElapsed();
            }
            _operationElapsedTimer.Stop();
            CurrentProgressItem = string.Empty;
            IsBusy = false;
        }
    }

    private ComparisonOptions CreateOptions() => _comparisonOptionsTemplate with
    {
        CompareFormulas = CompareFormulas,
        CompareFormatting = CompareFormatting,
        CompareFonts = CompareFonts,
        CompareComments = CompareComments,
        CompareHyperlinks = CompareHyperlinks,
        CompareLayout = CompareLayout,
        CaseSensitive = CaseSensitive,
        RowAlignmentMode = _useKeyColumnAlignment
            ? RowAlignmentMode.KeyColumns
            : StrictRowNumberComparison
                ? RowAlignmentMode.StrictRowNumber
                : RowAlignmentMode.Conservative,
    };

    private FolderScanOptions CreateFolderScanOptions() => new()
    {
        IncludeSubdirectories = IncludeSubdirectories,
        FilePattern = EffectiveFolderFilePattern(),
    };

    private string EffectiveFolderFilePattern() =>
        string.IsNullOrWhiteSpace(FolderFilePattern) ? "*.xlsx" : FolderFilePattern.Trim();

    private bool ValidateCurrentPaths()
    {
        if (IsFileMode)
        {
            return ValidateWorkbookPath(LeftPath, "左侧") && ValidateWorkbookPath(RightPath, "右侧");
        }

        if (!Directory.Exists(LeftPath))
        {
            StatusText = "左侧文件夹不存在";
            return false;
        }

        if (!Directory.Exists(RightPath))
        {
            StatusText = "右侧文件夹不存在";
            return false;
        }

        if (string.IsNullOrWhiteSpace(FolderFilePattern))
        {
            StatusText = "请输入文件通配符，例如 *.xlsx";
            return false;
        }

        return true;
    }

    private bool ValidateWorkbookPath(string path, string side)
    {
        if (!File.Exists(path))
        {
            StatusText = $"{side}文件不存在";
            return false;
        }

        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = $"{side}文件不是 .xlsx";
            return false;
        }

        return true;
    }

    private void ResetOperationStatus()
    {
        WarningText = string.Empty;
        ProgressPercent = 0;
        ProgressIsIndeterminate = true;
        CurrentProgressItem = string.Empty;
        _operationStopwatch.Restart();
        UpdateOperationElapsed();
        _operationElapsedTimer.Start();
    }

    private void UpdateProgress(ComparisonProgress progress)
    {
        CurrentProgressItem = progress.CurrentItem ?? string.Empty;
        StatusText = progress.Message;
        UpdateOperationElapsed();
        ProgressIsIndeterminate = progress.Total <= 0;
        ProgressPercent = progress.Total <= 0
            ? 0
            : Math.Clamp((int)Math.Round(progress.Processed * 100d / progress.Total), 0, 100);
        if (progress.CompletedFile is not null)
        {
            UpsertFolderFile(progress.CompletedFile);
        }
    }

    private void UpdateFolderScanProgress(ComparisonProgress progress)
    {
        UpdateProgress(progress);
        if (progress.Total > 0)
        {
            StatusText = $"扫描进度 {progress.Processed}/{progress.Total}：{progress.Message}";
        }
    }

    private void UpdateOperationElapsed()
    {
        var elapsed = _operationStopwatch.Elapsed;
        OperationElapsedText = $"耗时 {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void RecordRecentComparison()
    {
        if (_recentComparisonStore is null)
        {
            return;
        }

        var selectedBeforeRecord = SelectedRecentComparison;
        var entries = _recentComparisonStore.Record(new RecentComparisonEntry(
            IsFileMode ? RecentComparisonMode.Files : RecentComparisonMode.Folders,
            Path.GetFullPath(LeftPath),
            Path.GetFullPath(RightPath),
            DateTimeOffset.UtcNow,
            Options: CreateOptions(),
            IncludeSubdirectories: IncludeSubdirectories,
            FilePattern: EffectiveFolderFilePattern()));

        ReplaceSavedComparisons(entries);
        _selectedRecentComparison = selectedBeforeRecord is null
            ? null
            : RecentComparisons.FirstOrDefault(item => SavedComparisonIdentityEquals(item, selectedBeforeRecord));
        OnPropertyChanged(nameof(SelectedRecentComparison));
    }

    private void ReplaceSavedComparisons(IEnumerable<RecentComparisonEntry> entries)
    {
        RecentComparisons.Clear();
        foreach (var entry in entries)
        {
            RecentComparisons.Add(entry);
        }
    }

    private static bool SavedComparisonIdentityEquals(
        RecentComparisonEntry left,
        RecentComparisonEntry right) => left.IsProfile || right.IsProfile
        ? left.IsProfile == right.IsProfile &&
          string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
        : left.Mode == right.Mode &&
          string.Equals(left.LeftPath, right.LeftPath, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(left.RightPath, right.RightPath, StringComparison.OrdinalIgnoreCase);

    private void ClearRecentComparisonSelection()
    {
        if (!_applyingRecentComparison && _selectedRecentComparison is not null)
        {
            _selectedRecentComparison = null;
            OnPropertyChanged(nameof(SelectedRecentComparison));
        }
    }

    private void UpsertFolderFile(FolderFileResult file, bool refresh = true)
    {
        if (!_folderFilesByPath.TryGetValue(file.RelativePath, out var existing))
        {
            var item = new FolderFileItemViewModel(file);
            _folderFilesByPath.Add(file.RelativePath, item);
            FolderFiles.Add(item);
        }
        else
        {
            existing.Apply(file);
        }

        if (refresh)
        {
            FolderFilesView.Refresh();
            NotifyCommandStates();
        }
    }

    private void FolderFiles_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.OldItems is not null)
        {
            foreach (FolderFileItemViewModel item in eventArgs.OldItems)
            {
                item.PropertyChanged -= FolderFileItem_OnPropertyChanged;
            }
        }

        if (eventArgs.NewItems is not null)
        {
            foreach (FolderFileItemViewModel item in eventArgs.NewItems)
            {
                item.PropertyChanged -= FolderFileItem_OnPropertyChanged;
                item.PropertyChanged += FolderFileItem_OnPropertyChanged;
            }
        }

        NotifyFolderMarksChanged();
    }

    private void FolderFileItem_OnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(FolderFileItemViewModel.IsMarkedForComparison))
        {
            NotifyFolderMarksChanged();
        }
        else if (eventArgs.PropertyName is nameof(FolderFileItemViewModel.Status) or
                 nameof(FolderFileItemViewModel.DifferenceCount))
        {
            OnPropertyChanged(nameof(CanExportReport));
        }
    }

    private void NotifyFolderMarksChanged()
    {
        OnPropertyChanged(nameof(FolderSelectionSummary));
        NotifyCommandStates();
    }

    private FolderCompareResult CreateFolderReportResult()
    {
        var files = FolderFiles
            .Select(item => item.CoreResult as FolderFileResult ?? new FolderFileResult(
                item.RelativePath,
                item.LeftPath,
                item.RightPath,
                item.Status,
                item.DifferenceCount,
                null,
                item.Error))
            .ToArray();
        var status = files.Any(static file => file.Status == ComparisonStatus.Cancelled)
            ? ComparisonStatus.Cancelled
            : files.Any(static file => file.Status is
                ComparisonStatus.Different or ComparisonStatus.LeftOnly or ComparisonStatus.RightOnly)
                ? ComparisonStatus.Different
                : files.Any(static file => file.Status is ComparisonStatus.Warning or ComparisonStatus.Error)
                    ? ComparisonStatus.Warning
                    : files.Any(static file => file.Status == ComparisonStatus.Pending)
                        ? ComparisonStatus.Pending
                        : ComparisonStatus.Same;
        return new FolderCompareResult(
            Path.GetFullPath(LeftPath),
            Path.GetFullPath(RightPath),
            status,
            files,
            _folderComparisonElapsed);
    }

    private bool FilterFolderFile(object value)
    {
        if (value is not FolderFileItemViewModel item ||
            (ShowFolderDifferencesOnly && !item.IsDifferenceResult))
        {
            return false;
        }

        var query = FolderSearchText.Trim();
        return query.Length == 0 ||
            item.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearFolderResults()
    {
        foreach (var item in FolderFiles)
        {
            item.PropertyChanged -= FolderFileItem_OnPropertyChanged;
        }

        FocusedFolderFile = null;
        FolderFiles.Clear();
        _folderFilesByPath.Clear();
        _folderComparisonCache.Clear();
        _folderComparisonElapsed = TimeSpan.Zero;
        _hasComparisonResults = false;
        _resultsAreStale = false;
        OnPropertyChanged(nameof(CanExportReport));
        OnPropertyChanged(nameof(FolderSelectionSummary));
        FolderFilesView.Refresh();
        NotifyCommandStates();
    }

    private void MarkResultsStale()
    {
        if (!_hasComparisonResults)
        {
            return;
        }

        _resultsAreStale = true;
        OnPropertyChanged(nameof(CanExportReport));
        _folderComparisonCache.Clear();
        foreach (var file in FolderFiles)
        {
            file.InvalidateCachedComparison();
        }
        FolderFilesView.Refresh();

        const string message = "比较选项已更改，请重新比对以更新结果。";
        StatusText = message;
        WarningText = message;
    }

    private async Task OpenWorkbookAsync(
        string leftPath,
        string rightPath,
        WorkbookCompareResult? comparison,
        CancellationToken cancellationToken)
    {
        _previewCache.Clear();
        _worksheetResults.Clear();
        _leftWorksheetInfo.Clear();
        _rightWorksheetInfo.Clear();
        _staticDifferences.Clear();
        Worksheets.Clear();
        Differences.Clear();
        GridViewport.Clear();
        OtherDetailsText = string.Empty;

        CurrentLeftFile = leftPath;
        CurrentRightFile = rightPath;
        OnPropertyChanged(nameof(CurrentLeftFileName));
        OnPropertyChanged(nameof(CurrentRightFileName));

        var leftMetadataTask = string.IsNullOrEmpty(leftPath)
            ? Task.FromResult<WorkbookInfo?>(null)
            : ReadMetadataAsync(leftPath, cancellationToken);
        var rightMetadataTask = string.IsNullOrEmpty(rightPath)
            ? Task.FromResult<WorkbookInfo?>(null)
            : ReadMetadataAsync(rightPath, cancellationToken);
        await Task.WhenAll(leftMetadataTask, rightMetadataTask);
        var leftMetadata = await leftMetadataTask;
        var rightMetadata = await rightMetadataTask;

        foreach (var worksheet in leftMetadata?.Worksheets ?? [])
        {
            _leftWorksheetInfo[worksheet.Name] = worksheet;
        }

        foreach (var worksheet in rightMetadata?.Worksheets ?? [])
        {
            _rightWorksheetInfo[worksheet.Name] = worksheet;
        }

        foreach (var result in comparison?.Worksheets ?? [])
        {
            _worksheetResults[result.WorksheetName] = result;
        }

        if (comparison is not null)
        {
            foreach (var result in comparison.Worksheets)
            {
                var hasExplicitSideNames = result.LeftWorksheetName is not null ||
                    result.RightWorksheetName is not null;
                var effectiveLeftName = hasExplicitSideNames
                    ? result.LeftWorksheetName
                    : result.WorksheetName;
                var effectiveRightName = hasExplicitSideNames
                    ? result.RightWorksheetName
                    : result.WorksheetName;
                var leftName = effectiveLeftName is not null && _leftWorksheetInfo.ContainsKey(effectiveLeftName)
                    ? effectiveLeftName
                    : null;
                var rightName = effectiveRightName is not null && _rightWorksheetInfo.ContainsKey(effectiveRightName)
                    ? effectiveRightName
                    : null;
                var isOneSided = (leftName is null) != (rightName is null);
                Worksheets.Add(new WorksheetTabViewModel
                {
                    Name = result.WorksheetName,
                    LeftWorksheetName = leftName,
                    RightWorksheetName = rightName,
                    IsOneSided = isOneSided,
                    DifferenceCount = result.DistinctDifferenceCount > 0
                        ? result.DistinctDifferenceCount
                        : isOneSided ? 1 : 0,
                });
            }
        }
        else
        {
            var worksheetNames = (leftMetadata?.Worksheets.Select(static sheet => sheet.Name) ?? [])
                .Concat(rightMetadata?.Worksheets.Select(static sheet => sheet.Name) ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var name in worksheetNames)
            {
                var hasLeft = _leftWorksheetInfo.ContainsKey(name);
                var hasRight = _rightWorksheetInfo.ContainsKey(name);
                Worksheets.Add(new WorksheetTabViewModel
                {
                    Name = name,
                    LeftWorksheetName = hasLeft ? name : null,
                    RightWorksheetName = hasRight ? name : null,
                    IsOneSided = hasLeft != hasRight,
                    DifferenceCount = hasLeft != hasRight ? null : 0,
                });
            }
        }

        if (comparison is not null)
        {
            AddComparisonDifferences(comparison);
            WarningText = string.Join(Environment.NewLine, comparison.Warnings);
        }

        BuildOtherDetails(comparison?.Warnings ?? []);
        StartSourceMonitoring(comparison);
        IsWorkbookOpen = true;
        SelectedWorksheet = Worksheets.FirstOrDefault(sheet => sheet.IsOneSided || sheet.DifferenceCount > 0) ?? Worksheets.FirstOrDefault();
    }

    private async Task<WorkbookInfo?> ReadMetadataAsync(string filePath, CancellationToken cancellationToken) =>
        await Task.Run(
            () => _workbookReader.ReadMetadataAsync(filePath, cancellationToken),
            cancellationToken);

    private void AddComparisonDifferences(WorkbookCompareResult comparison)
    {
        var items = comparison.WorkbookDifferences
            .Concat(comparison.Worksheets.SelectMany(static worksheet => worksheet.Differences))
            .Select(ToDifferenceItem)
            .ToArray();
        _staticDifferences.AddRange(items);
        Differences.AddRange(items);
    }

    private void BuildOtherDetails(IReadOnlyList<string> warnings)
    {
        OtherDetailsText = string.Join(
            Environment.NewLine + Environment.NewLine,
            warnings
                .Concat(_staticDifferences
                    .Where(static item => ExcelAddress.TryParse(item.Address) is null)
                    .Select(static item => FormatDifferenceItem(item)))
                .Where(static detail => !string.IsNullOrWhiteSpace(detail))
                .Distinct(StringComparer.Ordinal));
    }

    private async Task LoadWorksheetSafeAsync(WorksheetTabViewModel worksheet)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var previewCancellation = new CancellationTokenSource();
        _previewCancellation = previewCancellation;
        IsPreviewBusy = true;
        GridViewport.Clear();
        _loadedWorksheetName = null;
        ClearSelectedCell();
        try
        {
            var comparisonAtStart = _activeComparison;
            await LoadWorksheetAsync(worksheet, previewCancellation.Token);
            if (comparisonAtStart is not null &&
                ReferenceEquals(_activeComparison, comparisonAtStart) &&
                !await IsCachedComparisonCurrentAsync(comparisonAtStart, previewCancellation.Token))
            {
                InvalidateActiveComparison("加载预览期间源文件发生变化，请重新比较");
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_previewCancellation, previewCancellation))
            {
                StatusText = "工作表预览已取消";
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_previewCancellation, previewCancellation))
            {
                GridViewport.Clear();
                _loadedWorksheetName = null;
                StatusText = $"工作表预览失败：{exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, previewCancellation))
            {
                IsPreviewBusy = false;
            }
        }
    }

    private async Task LoadWorksheetAsync(WorksheetTabViewModel worksheet, CancellationToken cancellationToken)
    {
        StatusText = $"正在加载工作表：{worksheet.Name}";
        var leftWorksheetName = worksheet.LeftWorksheetName;
        var rightWorksheetName = worksheet.RightWorksheetName;
        var leftTask = LoadPreviewIfPresentAsync(
            CurrentLeftFile,
            leftWorksheetName ?? string.Empty,
            leftWorksheetName is not null && _leftWorksheetInfo.ContainsKey(leftWorksheetName),
            cancellationToken);
        var rightTask = LoadPreviewIfPresentAsync(
            CurrentRightFile,
            rightWorksheetName ?? string.Empty,
            rightWorksheetName is not null && _rightWorksheetInfo.ContainsKey(rightWorksheetName),
            cancellationToken);
        await Task.WhenAll(leftTask, rightTask);
        cancellationToken.ThrowIfCancellationRequested();
        var left = await leftTask;
        var right = await rightTask;

        var coreDifferences = _worksheetResults.TryGetValue(worksheet.Name, out var worksheetResult)
            ? worksheetResult.Differences.ToList()
            : [];
        var oneSidedCellCount = (left is null) != (right is null)
            ? left?.Cells.Count ?? right?.Cells.Count ?? 0
            : 0;
        var displayedDifferenceCount = coreDifferences.Count + oneSidedCellCount;
        if (worksheet.IsOneSided && displayedDifferenceCount == 0)
        {
            displayedDifferenceCount = 1;
        }
        if (worksheet.IsOneSided)
        {
            worksheet.DifferenceCount = displayedDifferenceCount;
        }
        ReplaceDynamicWorksheetDifferences([]);

        GridViewport.SetPreviews(
            left,
            right,
            coreDifferences,
            worksheetResult?.RowAlignments,
            worksheetResult?.AppliedColumnPairs,
            ShowWorkbookDifferencesOnly);
        _loadedWorksheetName = worksheet.Name;
        ClearSelectedCell();
        StatusText = $"{worksheet.Name}：{displayedDifferenceCount} 处差异";
    }

    private async Task<WorksheetPreview?> LoadPreviewIfPresentAsync(
        string filePath,
        string worksheetName,
        bool worksheetExists,
        CancellationToken cancellationToken)
    {
        if (!worksheetExists || string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        var key = $"{filePath}\0{worksheetName}";
        if (_previewCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var preview = await Task.Run(
            () => _workbookReader.LoadWorksheetPreviewAsync(filePath, worksheetName, cancellationToken),
            cancellationToken);
        _previewCache.Set(key, preview);
        return preview;
    }

    private void ReplaceDynamicWorksheetDifferences(IReadOnlyList<Difference> differences) =>
        Differences.ReplaceAll(_staticDifferences.Concat(differences.Select(ToDifferenceItem)));

    private static DifferenceItemViewModel ToDifferenceItem(Difference difference) => new()
    {
        Worksheet = difference.WorksheetName ?? "工作簿",
        Address = difference.CellReference ?? "—",
        Description = DifferencePresentation.FormatDetail(difference),
        CoreDifference = difference,
    };

    private async Task MoveDifferenceAsync(int offset)
    {
        var navigable = Differences
            .Where(static item => ExcelAddress.TryParse(item.Address) is not null)
            .GroupBy(
                static item => item.Worksheet + "\0" + item.Address,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        if (navigable.Length == 0)
        {
            StatusText = HasOtherDetails ? "当前差异没有可跳转的单元格，请查看其他详情" : "没有可跳转的单元格差异";
            return;
        }

        _differenceNavigationIndex = _differenceNavigationIndex < 0
            ? offset < 0 ? navigable.Length - 1 : 0
            : (_differenceNavigationIndex + offset + navigable.Length) % navigable.Length;
        await NavigateToDifferenceAsync(navigable[_differenceNavigationIndex]);
    }

    private async Task NavigateToDifferenceAsync(DifferenceItemViewModel difference)
    {
        var worksheet = Worksheets.FirstOrDefault(item =>
            string.Equals(item.Name, difference.Worksheet, StringComparison.OrdinalIgnoreCase));
        var address = ExcelAddress.TryParse(difference.Address);
        if (worksheet is null || address is null)
        {
            return;
        }

        if (!ReferenceEquals(SelectedWorksheet, worksheet)
            || !string.Equals(_loadedWorksheetName, worksheet.Name, StringComparison.OrdinalIgnoreCase))
        {
            _selectedWorksheet = worksheet;
            OnPropertyChanged(nameof(SelectedWorksheet));
            await LoadWorksheetSafeAsync(worksheet);
            if (!string.Equals(_loadedWorksheetName, worksheet.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var displayRowIndex = GridViewport.GetDisplayRowIndex(address.Value.Row);
        if (displayRowIndex >= 0)
        {
            GridNavigationRequested?.Invoke(
                this,
                new GridNavigationEventArgs(displayRowIndex, address.Value.Column - 1));
        }
    }

    private void Cancel()
    {
        StatusText = "正在取消…";
        _operationCancellation?.Cancel();
        _previewCancellation?.Cancel();
    }

    private void CloseWorkbook()
    {
        StopSourceMonitoring();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        IsPreviewBusy = false;
        IsWorkbookOpen = false;
        SelectedWorksheet = null;
        Worksheets.Clear();
        Differences.Clear();
        GridViewport.Clear();
        OtherDetailsText = string.Empty;
        _loadedWorksheetName = null;
        _differenceNavigationIndex = -1;
        ClearSelectedCell();
    }

    private void StartSourceMonitoring(WorkbookCompareResult? comparison)
    {
        StopSourceMonitoring();
        _activeComparison = comparison;
        OnPropertyChanged(nameof(CanExportReport));
        if (comparison is null || _uiContext is null)
        {
            return;
        }

        foreach (var path in new[] { comparison.LeftPath, comparison.RightPath }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(directory))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(directory, Path.GetFileName(fullPath))
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName |
                        NotifyFilters.CreationTime |
                        NotifyFilters.LastWrite |
                        NotifyFilters.Size,
                };
                watcher.Changed += ComparedSource_OnChanged;
                watcher.Created += ComparedSource_OnChanged;
                watcher.Deleted += ComparedSource_OnChanged;
                watcher.Renamed += ComparedSource_OnRenamed;
                watcher.Error += ComparedSource_OnError;
                watcher.EnableRaisingEvents = true;
                _sourceWatchers.Add(watcher);
            }
            catch (IOException)
            {
                // The SHA-256 check after every preview load remains the fallback.
            }
            catch (UnauthorizedAccessException)
            {
                // The SHA-256 check after every preview load remains the fallback.
            }
        }
    }

    private void StopSourceMonitoring()
    {
        foreach (var watcher in _sourceWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _sourceWatchers.Clear();
        _activeComparison = null;
        OnPropertyChanged(nameof(CanExportReport));
        _sourceVerificationScheduled = false;
        _sourceVerificationPending = false;
    }

    private void ComparedSource_OnChanged(object sender, FileSystemEventArgs eventArgs) =>
        ScheduleSourceVerification();

    private void ComparedSource_OnRenamed(object sender, RenamedEventArgs eventArgs) =>
        ScheduleSourceVerification();

    private void ComparedSource_OnError(object sender, ErrorEventArgs eventArgs) =>
        ScheduleSourceVerification();

    private void ScheduleSourceVerification()
    {
        if (_uiContext is null)
        {
            return;
        }

        _uiContext.Post(
            static state => ((MainWindowViewModel)state!).ScheduleSourceVerificationOnUiThread(),
            this);
    }

    private void ScheduleSourceVerificationOnUiThread()
    {
        if (_activeComparison is null)
        {
            return;
        }

        if (_sourceVerificationScheduled)
        {
            _sourceVerificationPending = true;
            return;
        }

        _sourceVerificationScheduled = true;
        _ = VerifyActiveComparisonAsync();
    }

    private async Task VerifyActiveComparisonAsync()
    {
        var comparison = _activeComparison;
        if (comparison is null)
        {
            _sourceVerificationScheduled = false;
            return;
        }

        try
        {
            await Task.Delay(150);
            if (ReferenceEquals(_activeComparison, comparison) &&
                !await IsCachedComparisonCurrentAsync(comparison, CancellationToken.None))
            {
                InvalidateActiveComparison("源文件已变化，当前比较结果已作废，请重新比较");
            }
        }
        catch (IOException)
        {
            if (ReferenceEquals(_activeComparison, comparison))
            {
                InvalidateActiveComparison("源文件已变化或暂时无法读取，请重新比较");
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (ReferenceEquals(_activeComparison, comparison))
            {
                InvalidateActiveComparison("源文件访问权限发生变化，请重新比较");
            }
        }
        finally
        {
            _sourceVerificationScheduled = false;
            if (_sourceVerificationPending && _activeComparison is not null)
            {
                _sourceVerificationPending = false;
                ScheduleSourceVerificationOnUiThread();
            }
        }
    }

    private void InvalidateActiveComparison(string message)
    {
        var comparison = _activeComparison;
        if (comparison is null)
        {
            return;
        }

        if (IsFolderMode)
        {
            foreach (var item in FolderFiles.Where(item =>
                         string.Equals(item.LeftPath, comparison.LeftPath, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(item.RightPath, comparison.RightPath, StringComparison.OrdinalIgnoreCase)))
            {
                item.InvalidateCachedComparison();
            }

            _folderComparisonCache.Clear();
            FolderFilesView.Refresh();
        }
        else
        {
            _hasComparisonResults = false;
            _resultsAreStale = true;
        }

        CloseWorkbook();
        StatusText = message;
        WarningText = "旧差异摘要与新文件内容没有混合显示。";
    }

    private void InvalidateResultsForPathChange()
    {
        if (IsBusy || (!_hasComparisonResults && FolderFiles.Count == 0 && !IsWorkbookOpen))
        {
            return;
        }

        CloseWorkbook();
        ClearFolderResults();
        _hasComparisonResults = false;
        _resultsAreStale = true;
        WarningText = string.Empty;
        StatusText = "路径已更改，请重新扫描或比较";
    }

    private static async Task<bool> IsCachedComparisonCurrentAsync(
        WorkbookCompareResult comparison,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(comparison.LeftPath) || !File.Exists(comparison.RightPath))
            {
                return false;
            }

            var leftHash = await ComputeSha256Async(comparison.LeftPath, cancellationToken);
            var rightHash = await ComputeSha256Async(comparison.RightPath, cancellationToken);
            return string.Equals(leftHash, comparison.LeftSha256, StringComparison.Ordinal) &&
                string.Equals(rightHash, comparison.RightSha256, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ComparisonCacheKey(string leftPath, string rightPath) =>
        Path.GetFullPath(leftPath) + "\0" + Path.GetFullPath(rightPath);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private void ClearSelectedCell()
    {
        SelectedAddress = "—";
        LeftSelectedRaw = string.Empty;
        RightSelectedRaw = string.Empty;
        LeftSelectedDisplay = string.Empty;
        RightSelectedDisplay = string.Empty;
        LeftSelectedFormula = string.Empty;
        RightSelectedFormula = string.Empty;
    }

    private void NotifyCommandStates()
    {
        (SwapCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (StartCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (CancelCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RefreshCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (BackToFolderCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (CompareSelectedCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    private static string FormatCompletedStatus(ComparisonStatus status, int count, TimeSpan elapsed) => status switch
    {
        ComparisonStatus.Same => $"语义相同，用时 {elapsed.TotalSeconds:N1} 秒",
        ComparisonStatus.Different => $"比较完成：{count} 处差异，用时 {elapsed.TotalSeconds:N1} 秒",
        ComparisonStatus.Warning => $"比较完成但存在警告：{count} 处差异",
        _ => $"比较结束：{status}",
    };

    private static string FormatDifferenceItem(DifferenceItemViewModel item) =>
        $"[{item.Worksheet}] {item.Address}\n{item.Description}";

}

internal sealed class GridNavigationEventArgs(int rowIndex, int columnIndex) : EventArgs
{
    public int RowIndex { get; } = rowIndex;

    public int ColumnIndex { get; } = columnIndex;
}
