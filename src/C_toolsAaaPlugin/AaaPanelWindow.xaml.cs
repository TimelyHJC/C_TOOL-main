using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsShared;

namespace C_toolsAaaPlugin;

public partial class AaaPanelWindow : Window, IModelessWindowPlacement
{
    public string PlacementKey => "C_TOOL_AaaPanel";

    private const string InitialStatusText = "请选择图块文件夹。";
    private const string SelectFolderTooltip = "请先选择有效的图块文件夹。";
    private const string RefreshFolderTooltip = "重新读取当前图库目录。";
    private const string ImportBlocksTooltip = "当前显示独立图库。隐藏面板后可在当前图纸中点选或框选多个图块，并添加到当前图库目录。";
    private const string ImportComboTooltip = "当前显示组合图库。隐藏面板后可在当前图纸中多选图块，输入组合名称并添加为组合定义。";
    private const string UpdateBlocksTooltip = "当前显示独立图库。隐藏面板后可在当前图纸中选择新块，并按设备名称替换图库内时间戳不同的旧块。";
    private const string UpdateComboTooltip = "当前显示组合图库。隐藏面板后可在当前图纸中多选图块，输入组合名称与基点，并按设备名称替换图库内时间戳不同的旧组合定义。";
    private const string UpdateBlocksEmptyTooltip = "当前独立图库中没有可更新的图块。";
    private const string UpdateComboEmptyTooltip = "当前组合图库中没有可更新的组合定义。";
    private readonly ObservableCollection<AaaBlockCatalogItem> _blocks = new();
    private readonly ObservableCollection<AaaFavoriteFolderItem> _favoriteFolders = new();
    private readonly ObservableCollection<AaaCategoryTagItem> _categoryTags = new();
    private readonly ICollectionView _blocksView;
    private string _folderPath = "";
    private string _searchText = "";
    private string _activeCategoryKey = "";
    private bool _isInitializing;
    private bool _isSyncingFavoriteSelection;
    private bool _isSyncingCategorySelection;

    public AaaPanelWindow()
    {
        InitializeComponent();
        WindowDpiHelper.InstallWindowSizeFromCurrentPixels(this);
        BlocksGrid.ItemsSource = _blocks;
        FavoriteFoldersComboBox.ItemsSource = _favoriteFolders;
        CategoryTagsListBox.ItemsSource = _categoryTags;
        _blocksView = System.Windows.Data.CollectionViewSource.GetDefaultView(_blocks);
        _blocksView.Filter = FilterBlock;
        UpdateCommandButtonsState();
        UpdateSummary();
        SetStatus(InitialStatusText);
        UpdatePreviewPanel(null);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowTitleBarHelper.TryApplyDarkTitleBar(this);

        try
        {
            _isInitializing = true;
            var settings = AaaFolderBlockStore.Load();
            _folderPath = AaaFolderBlockStore.NormalizeFolderPath(settings.FolderPath);
            SetFavoriteFolders(settings.FavoriteFolders);
            FolderPathTextBox.Text = _folderPath;
            IncludeSubfoldersCheckBox.IsChecked = settings.IncludeSubfolders;
            SyncFavoriteSelection();
            UpdateCommandButtonsState();
            _isInitializing = false;

            if (Directory.Exists(_folderPath))
            {
                RefreshBlockList();
            }
            else
            {
                RefreshCategoryTags();
                UpdateSummary();
                SetStatus(InitialStatusText);
            }
        }
        catch (Exception ex)
        {
            _isInitializing = false;
            _isSyncingFavoriteSelection = false;
            _isSyncingCategorySelection = false;
            _folderPath = "";
            _searchText = "";
            _activeCategoryKey = "";
            _blocks.Clear();
            SetFavoriteFolders(Array.Empty<string>());
            FolderPathTextBox.Text = "";
            IncludeSubfoldersCheckBox.IsChecked = false;
            RefreshCategoryTags();
            _blocksView.Refresh();
            BlocksGrid.SelectedItem = null;
            UpdateSummary();
            UpdateCommandButtonsState();
            SyncFavoriteSelection();
            C_toolsDiagnostics.LogNonFatal("V_AAA 面板初始化失败", ex);
            SetStatus(UIMessages.Common.BuildSimpleFailure("初始化", ex));
            UpdatePreviewPanel(null);
        }
    }

    internal void EnsureShown()
    {
        Show();
        ShowActivated = false;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择图块文件夹",
            SelectedPath = Directory.Exists(_folderPath)
                ? _folderPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = false
        };

        var owner = new Win32Window(new WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK)
            return;

        _folderPath = AaaFolderBlockStore.NormalizeFolderPath(dialog.SelectedPath);
        FolderPathTextBox.Text = _folderPath;
        SyncFavoriteSelection();
        UpdateCommandButtonsState();
        SaveSettings();
        RefreshBlockList();
    }

    private void RefreshBlocks_Click(object sender, RoutedEventArgs e)
    {
        RefreshBlockList();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchTextBox.Text?.Trim() ?? "";
        RefreshFilteredView();
    }

    private void IncludeSubfoldersChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        SaveSettings();
        if (Directory.Exists(_folderPath))
            RefreshBlockList();
        else
            SetStatus("请先选择图块文件夹。");
    }

    private void ImportCurrentLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_folderPath) || !Directory.Exists(_folderPath))
        {
            SetStatus("请选择有效的图块文件夹。");
            return;
        }

        if (IsComboLibraryActive())
            BeginImportComboFromDrawing();
        else
            BeginImportFromDrawing();
    }

    private void UpdateCurrentLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_folderPath) || !Directory.Exists(_folderPath))
        {
            SetStatus("请选择有效的图块文件夹。");
            return;
        }

        if (IsComboLibraryActive())
            BeginUpdateComboFromDrawing();
        else
            BeginUpdateBlocksFromDrawing();
    }

    private void FavoriteFoldersComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isSyncingFavoriteSelection)
            return;
        if (FavoriteFoldersComboBox.SelectedItem is not AaaFavoriteFolderItem favorite)
            return;
        if (string.Equals(_folderPath, favorite.FullPath, StringComparison.OrdinalIgnoreCase))
            return;

        _folderPath = favorite.FullPath;
        FolderPathTextBox.Text = _folderPath;
        UpdateCommandButtonsState();
        SaveSettings();
        RefreshBlockList($"已切换到收藏图库：{favorite.DisplayName}");
    }

    private void AddFavoriteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_folderPath) || !Directory.Exists(_folderPath))
        {
            SetStatus("请选择有效的图库目录后再收藏。");
            return;
        }

        var normalized = AaaFolderBlockStore.NormalizeFolderPath(_folderPath);
        if (_favoriteFolders.Any(x => string.Equals(x.FullPath, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SyncFavoriteSelection();
            SetStatus("当前图库目录已在收藏列表中。");
            return;
        }

        _favoriteFolders.Add(new AaaFavoriteFolderItem { FullPath = normalized });
        SyncFavoriteSelection();
        SaveSettings();
        SetStatus($"已收藏图库目录：{normalized}");
    }

    private void RemoveFavoriteFolder_Click(object sender, RoutedEventArgs e)
    {
        var targetPath = (FavoriteFoldersComboBox.SelectedItem as AaaFavoriteFolderItem)?.FullPath;
        if (string.IsNullOrWhiteSpace(targetPath))
            targetPath = _folderPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            SetStatus("当前没有可取消收藏的图库目录。");
            return;
        }

        var favorite = _favoriteFolders.FirstOrDefault(x =>
            string.Equals(x.FullPath, targetPath, StringComparison.OrdinalIgnoreCase));
        if (favorite == null)
        {
            SetStatus("当前图库目录不在收藏列表中。");
            return;
        }

        _favoriteFolders.Remove(favorite);
        SyncFavoriteSelection();
        SaveSettings();
        SetStatus($"已取消收藏：{targetPath}");
    }

    private void CategoryTagsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingCategorySelection)
            return;

        _activeCategoryKey = CategoryTagsListBox.SelectedItem is AaaCategoryTagItem tag
            ? tag.Key
            : "";
        RefreshFilteredView();
    }

    private void BlocksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePreviewPanel(BlocksGrid.SelectedItem as AaaBlockCatalogItem);
    }

    private void BlocksGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BlocksGrid.SelectedItem is AaaBlockCatalogItem)
            BeginInsertSelectedBlock();
    }

    private void BlockNameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AaaBlockCatalogItem item)
            return;

        BlocksGrid.SelectedItem = item;
        BeginInsertBlock(item, closeWindowBeforeInsert: true);
        e.Handled = true;
    }

    private void InsertSelectedBlock_Click(object sender, RoutedEventArgs e)
    {
        BeginInsertSelectedBlock();
    }

    private void RefreshBlockList(string? statusOverride = null)
    {
        UpdateCommandButtonsState();
        if (string.IsNullOrWhiteSpace(_folderPath) || !Directory.Exists(_folderPath))
        {
            _blocks.Clear();
            RefreshCategoryTags();
            _blocksView.Refresh();
            UpdateSummary();
            SetStatus("请选择有效的图块文件夹。");
            return;
        }

        try
        {
            var items = AaaFolderBlockCatalog.Load(_folderPath, IncludeSubfoldersCheckBox.IsChecked == true);
            _blocks.Clear();
            foreach (var item in items)
                _blocks.Add(item);

            UpdateCommandButtonsState();
            RefreshCategoryTags();
            _blocksView.Refresh();
            EnsureValidSelection();
            UpdateSummary();
            UpdatePreviewPanel(BlocksGrid.SelectedItem as AaaBlockCatalogItem);
            SyncFavoriteSelection();
            SaveSettings();
            var defaultMessage = items.Count == 0
                ? "当前文件夹中未找到图块或组合定义。"
                : $"已读取 {items.Count} 个图块/组合；点击右侧添加图标可按当前显示的图库类型添加到当前图库，点击更新图标可按设备名称替换当前图库中的旧项，点击刷新图标可重新读取当前目录。";
            SetStatus(statusOverride ?? defaultMessage);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块目录失败", ex);
            SetStatus($"读取图块目录失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块目录失败（权限）", ex);
            SetStatus($"读取图块目录失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块目录失败", ex);
            SetStatus($"读取图块目录失败：{ex.Message}");
        }
    }

    private bool FilterBlock(object obj)
    {
        if (obj is not AaaBlockCatalogItem item)
            return false;
        if (!MatchesActiveCategory(item))
            return false;
        if (_searchText.Length == 0)
            return true;
        return item.SearchText.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void BeginImportFromDrawing()
    {
        Hide();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var document = AcAp.DocumentManager.MdiActiveDocument;
                    var result = document == null
                        ? AaaImportResult.Failure("当前没有活动图纸。")
                        : AaaBlockLibraryImportService.PromptAndImport(document, _folderPath);

                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        new Action(() =>
                        {
                            EnsureShown();
                            if (result.ImportedCount > 0)
                                RefreshBlockList(result.Message);
                            else
                                SetStatus(result.Message);
                        }));
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 发起图块添加失败", ex);
            EnsureShown();
            SetStatus($"添加图块失败：{ex.Message}");
        }
    }

    private void BeginUpdateBlocksFromDrawing()
    {
        Hide();

        try
        {
            var librarySnapshot = _blocks
                .Where(x => !x.IsCombo)
                .ToList();

            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var document = AcAp.DocumentManager.MdiActiveDocument;
                    var result = document == null
                        ? AaaImportResult.Failure("当前没有活动图纸。")
                        : AaaBlockLibraryUpdateService.PromptAndUpdate(document, _folderPath, librarySnapshot);

                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        new Action(() =>
                        {
                            EnsureShown();
                            if (result.ImportedCount > 0)
                                RefreshBlockList(result.Message);
                            else
                                SetStatus(result.Message);
                        }));
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 发起图库更新失败", ex);
            EnsureShown();
            SetStatus($"更新图库块失败：{ex.Message}");
        }
    }

    private void BeginUpdateComboFromDrawing()
    {
        Hide();

        try
        {
            var librarySnapshot = _blocks
                .Where(x => x.IsCombo)
                .ToList();

            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var document = AcAp.DocumentManager.MdiActiveDocument;
                    var result = document == null
                        ? AaaImportResult.Failure("当前没有活动图纸。")
                        : AaaBlockComboLibraryUpdateService.PromptAndUpdateCombo(document, _folderPath, librarySnapshot);

                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        new Action(() =>
                        {
                            EnsureShown();
                            if (result.ImportedCount > 0)
                                RefreshBlockList(result.Message);
                            else
                                SetStatus(result.Message);
                        }));
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 发起组合图库更新失败", ex);
            EnsureShown();
            SetStatus($"更新组合图库失败：{ex.Message}");
        }
    }

    private void BeginInsertSelectedBlock()
    {
        if (BlocksGrid.SelectedItem is not AaaBlockCatalogItem item)
        {
            SetStatus("请先选择要插入的图块/组合。");
            return;
        }

        BeginInsertBlock(item, closeWindowBeforeInsert: false);
    }

    private void BeginInsertBlock(AaaBlockCatalogItem? item, bool closeWindowBeforeInsert)
    {
        if (item == null)
        {
            SetStatus("请先选择要插入的图块/组合。");
            return;
        }

        if (!item.ExistsOnDisk)
        {
            SetStatus($"资源不存在：{item.FullPath}");
            return;
        }

        if (closeWindowBeforeInsert)
            Close();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var document = AcAp.DocumentManager.MdiActiveDocument;
                    var result = document == null
                        ? AaaInsertResult.Failure("当前没有活动图纸。")
                        : AaaBlockInsertService.PromptAndInsert(document, item);

                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        new Action(() =>
                        {
                            if (!closeWindowBeforeInsert)
                            {
                                SetStatus(result.Message);
                            }
                            else if (document != null && !result.Success && !string.IsNullOrWhiteSpace(result.Message))
                            {
                                document.Editor.WriteMessage($"\nV_AAA：{result.Message}");
                            }
                        }));
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 发起图块插入失败", ex);
            SetStatus($"插入图块/组合失败：{ex.Message}");
        }
    }

    private void BeginImportComboFromDrawing()
    {
        Hide();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var document = AcAp.DocumentManager.MdiActiveDocument;
                    var result = document == null
                        ? AaaImportResult.Failure("当前没有活动图纸。")
                        : AaaBlockComboLibraryImportService.PromptAndImportCombo(document, _folderPath);

                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        new Action(() =>
                        {
                            EnsureShown();
                            if (result.ImportedCount > 0)
                                RefreshBlockList(result.Message);
                            else
                                SetStatus(result.Message);
                        }));
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 发起组合添加失败", ex);
            EnsureShown();
            SetStatus($"添加组合失败：{ex.Message}");
        }
    }

    private void SaveSettings()
    {
        AaaFolderBlockStore.Save(new AaaFolderBlockSettings
        {
            FolderPath = _folderPath,
            IncludeSubfolders = IncludeSubfoldersCheckBox.IsChecked == true,
            FavoriteFolders = _favoriteFolders.Select(x => x.FullPath).ToList()
        });
    }

    private void UpdateSummary()
    {
        var visibleCount = _blocksView.Cast<object>().Count();
        if (_blocks.Count == 0)
        {
            SummaryTextBlock.Text = "共 0 个图块/组合";
            return;
        }

        var parts = new List<string>
        {
            _searchText.Length == 0 && visibleCount == _blocks.Count
                ? $"共 {visibleCount} 个图块/组合"
                : $"匹配 {visibleCount} / {_blocks.Count} 个图块/组合"
        };
        var categoryName = GetCategoryDisplayName(_activeCategoryKey);
        if (categoryName.Length > 0)
            parts.Add($"分类：{categoryName}");

        if (_searchText.Length > 0)
            parts.Add($"搜索：{_searchText}");

        SummaryTextBlock.Text = string.Join(" · ", parts);
    }

    private void SetStatus(string message)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(message) ? InitialStatusText : message;
    }

    private void UpdatePreviewPanel(AaaBlockCatalogItem? item)
    {
        if (item == null)
        {
            PreviewImage.Source = null;
            PreviewPlaceholderText.Text = "选择一个图块或组合以查看缩略图。";
            PreviewPlaceholderText.Visibility = Visibility.Visible;
            return;
        }

        var preview = item.ThumbnailImage;
        PreviewImage.Source = preview;
        PreviewPlaceholderText.Text = preview == null
            ? $"“{item.DisplayName}”当前没有可用缩略图。"
            : "";
        PreviewPlaceholderText.Visibility = preview == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshFilteredView()
    {
        _blocksView.Refresh();
        EnsureValidSelection();
        UpdateSummary();
        UpdateCommandButtonsState();
    }

    private void UpdateCommandButtonsState()
    {
        var hasValidFolder = !string.IsNullOrWhiteSpace(_folderPath) && Directory.Exists(_folderPath);

        RefreshBlocksButton.IsEnabled = hasValidFolder;
        RefreshBlocksButton.ToolTip = hasValidFolder ? RefreshFolderTooltip : SelectFolderTooltip;

        ImportBlocksFromDrawingButton.IsEnabled = hasValidFolder;
        ImportBlocksFromDrawingButton.ToolTip = hasValidFolder ? GetCurrentImportTooltip() : SelectFolderTooltip;

        if (UpdateBlocksFromDrawingButton != null)
        {
            var canUpdateBlocks = hasValidFolder && HasUpdateCandidatesInCurrentLibrary();
            UpdateBlocksFromDrawingButton.IsEnabled = canUpdateBlocks;
            UpdateBlocksFromDrawingButton.ToolTip = hasValidFolder
                ? GetCurrentUpdateTooltip()
                : SelectFolderTooltip;
        }
    }

    private bool MatchesActiveCategory(AaaBlockCatalogItem item)
    {
        return _activeCategoryKey switch
        {
            AaaCategoryTagItem.ComboLibraryKey => item.IsCombo,
            AaaCategoryTagItem.SingleLibraryKey => !item.IsCombo,
            _ => true
        };
    }

    private void EnsureValidSelection()
    {
        var visibleBlocks = _blocksView.Cast<AaaBlockCatalogItem>().ToList();
        if (visibleBlocks.Count == 0)
        {
            BlocksGrid.SelectedItem = null;
            return;
        }

        if (BlocksGrid.SelectedItem is AaaBlockCatalogItem selected && visibleBlocks.Contains(selected))
            return;

        BlocksGrid.SelectedItem = visibleBlocks[0];
        BlocksGrid.ScrollIntoView(visibleBlocks[0]);
    }

    private void RefreshCategoryTags()
    {
        var selectedKey = _activeCategoryKey;
        var items = BuildCategoryTagItems(_blocks);

        _isSyncingCategorySelection = true;
        _categoryTags.Clear();
        foreach (var item in items)
            _categoryTags.Add(item);

        var selected = _categoryTags.FirstOrDefault(x =>
                           string.Equals(x.Key, selectedKey, StringComparison.OrdinalIgnoreCase) && x.Count > 0)
                       ?? _categoryTags.FirstOrDefault(x => x.Count > 0)
                       ?? _categoryTags.FirstOrDefault(x =>
                           string.Equals(x.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
                       ?? _categoryTags.FirstOrDefault();
        CategoryTagsListBox.SelectedItem = selected;
        _activeCategoryKey = selected?.Key ?? "";
        _isSyncingCategorySelection = false;
        UpdateCommandButtonsState();
    }

    private bool IsComboLibraryActive()
    {
        return string.Equals(_activeCategoryKey, AaaCategoryTagItem.ComboLibraryKey, StringComparison.OrdinalIgnoreCase);
    }

    private string GetCurrentImportTooltip()
    {
        return IsComboLibraryActive() ? ImportComboTooltip : ImportBlocksTooltip;
    }

    private string GetCurrentUpdateTooltip()
    {
        if (IsComboLibraryActive())
            return _blocks.Any(x => x.IsCombo) ? UpdateComboTooltip : UpdateComboEmptyTooltip;

        return _blocks.Any(x => !x.IsCombo) ? UpdateBlocksTooltip : UpdateBlocksEmptyTooltip;
    }

    private bool HasUpdateCandidatesInCurrentLibrary()
    {
        return IsComboLibraryActive()
            ? _blocks.Any(x => x.IsCombo)
            : _blocks.Any(x => !x.IsCombo);
    }

    private static IReadOnlyList<AaaCategoryTagItem> BuildCategoryTagItems(IEnumerable<AaaBlockCatalogItem> blocks)
    {
        var items = blocks.ToList();
        return new List<AaaCategoryTagItem>
        {
            new()
            {
                Key = AaaCategoryTagItem.SingleLibraryKey,
                DisplayName = "独立图库",
                Count = items.Count(x => !x.IsCombo)
            },
            new()
            {
                Key = AaaCategoryTagItem.ComboLibraryKey,
                DisplayName = "组合图库",
                Count = items.Count(x => x.IsCombo)
            }
        };
    }

    private static string GetCategoryDisplayName(string categoryKey)
    {
        return categoryKey switch
        {
            AaaCategoryTagItem.ComboLibraryKey => "组合图库",
            AaaCategoryTagItem.SingleLibraryKey => "独立图库",
            _ => ""
        };
    }

    private void SetFavoriteFolders(IEnumerable<string> favorites)
    {
        _favoriteFolders.Clear();
        foreach (var favorite in AaaFolderBlockStore.NormalizeFolderList(favorites))
            _favoriteFolders.Add(new AaaFavoriteFolderItem { FullPath = favorite });
    }

    private void SyncFavoriteSelection()
    {
        _isSyncingFavoriteSelection = true;
        var selected = _favoriteFolders.FirstOrDefault(x =>
            string.Equals(x.FullPath, _folderPath, StringComparison.OrdinalIgnoreCase));
        FavoriteFoldersComboBox.SelectedItem = selected;
        FavoriteFoldersComboBox.ToolTip = selected?.FullPath ?? "从收藏目录快速切换当前图库";
        _isSyncingFavoriteSelection = false;
    }

    private sealed class Win32Window : System.Windows.Forms.IWin32Window
    {
        public Win32Window(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
    }
}
