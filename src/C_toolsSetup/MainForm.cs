using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace C_toolsSetup;

public class MainForm : Form
{
    private bool _allowCloseWhileInstalling;
    private bool _isInstalling;
    private int _installProgressPercent;
    private int _logFlushPending;
    private readonly ConcurrentQueue<string> _pendingLogLines = new();
    private readonly TextBox _txtFullPath = new() { Width = 420 };
    private readonly TextBox _txtInitialFolderPath = new() { Width = 420, PlaceholderText = "留空则使用默认初始化文件夹" };
    private readonly ComboBox _cbbAcadVersion = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 480 };
    private readonly RadioButton _rbUser = new()
    {
        Text = "启动项：当前用户（HKCU\\…\\Applications，推荐）",
        AutoSize = true,
        Checked = true
    };
    private readonly RadioButton _rbAll = new()
    {
        Text = "启动项：所有用户（HKLM\\…\\Applications，需管理员）",
        AutoSize = true
    };
    private readonly CheckBox _chkAutoLaunchCad = new()
    {
        Text = "安装后是否自动启动AutoCAD程序",
        AutoSize = true,
        Checked = true
    };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9f)
    };
    private readonly Button _btnBrowse = new() { Text = "浏览…", AutoSize = true };
    private readonly Button _btnBrowseInitialFolder = new() { Text = "浏览…", AutoSize = true };
    private readonly Button _btnInstall = new() { Text = "安装", Width = 120, Height = 32 };
    private readonly Button _btnUpdate = new() { Text = "检查更新", Width = 120, Height = 32 };
    private readonly Label _lblInstallStatus = new()
    {
        Dock = DockStyle.Top,
        Height = 22,
        TextAlign = ContentAlignment.MiddleLeft,
        Visible = false
    };
    private readonly ProgressBar _installProgressBar = new()
    {
        Dock = DockStyle.Top,
        Height = 18,
        Style = ProgressBarStyle.Continuous,
        Minimum = 0,
        Maximum = 100,
        Visible = false
    };
    private readonly Panel _installProgressPanel = new()
    {
        Dock = DockStyle.Top,
        Height = 52,
        Padding = new Padding(12, 0, 12, 8),
        Visible = false
    };

    public MainForm()
    {
        Text = "C_TOOL 安装程序";
        Width = 720;
        Height = 1200;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 1200);
        MaximumSize = new Size(720, 1200);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        _txtFullPath.Text = GetDefaultInstallRootPath();
        _txtInitialFolderPath.Text = GetDefaultInitialFolderPathOrEmpty();

        Load += OnMainFormLoad;
        FormClosing += OnMainFormClosing;
        _cbbAcadVersion.SelectedIndexChanged += (_, _) =>
        {
            SaveSelectedAcadChoice();
            RefreshAcadSelectionUi();
        };

        _btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择完整安装目录（将在该目录下创建 Plugin 和 User 子文件夹）",
                UseDescriptionForTitle = true,
                SelectedPath = GetBrowseStartPath()
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtFullPath.Text = dlg.SelectedPath;
        };

        _btnBrowseInitialFolder.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择初始化文件夹（文件夹内可放 初始化文件.md、.arg 等初始数据）",
                UseDescriptionForTitle = true,
                SelectedPath = GetInitialFolderBrowseStartPath()
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtInitialFolderPath.Text = dlg.SelectedPath;
        };

        _btnInstall.Click += async (_, _) => await RunInstallAsync();
        _btnUpdate.Click += async (_, _) => await RunUpdateAsync();

        var table = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 4,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void AddRow(int row, string label, Control c, Control? extra = null)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            table.Controls.Add(c, 1, row);
            if (extra != null)
                table.Controls.Add(extra, 2, row);
            else
                table.SetColumnSpan(c, 2);
        }

        var flowBrowse = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        flowBrowse.Controls.Add(_txtFullPath);
        flowBrowse.Controls.Add(_btnBrowse);
        AddRow(0, "完整安装路径", flowBrowse);

        var flowInitialFolder = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        flowInitialFolder.Controls.Add(_txtInitialFolderPath);
        flowInitialFolder.Controls.Add(_btnBrowseInitialFolder);
        AddRow(1, "初始化文件夹路径", flowInitialFolder);

        AddRow(2, "", _chkAutoLaunchCad);

        var panelRadio = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0)
        };
        panelRadio.Controls.Add(_rbUser);
        panelRadio.Controls.Add(_rbAll);
        table.Controls.Add(panelRadio, 1, 3);
        table.SetColumnSpan(panelRadio, 2);
        table.Controls.Add(new Label { Text = "插件启动项", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);

        var panelTop = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(8, 8, 8, 0),
            Dock = DockStyle.Top
        };
        panelTop.Controls.Add(table);

        var panelBottom = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Padding = new Padding(12, 0, 12, 12),
            Dock = DockStyle.Bottom
        };
        panelBottom.Controls.Add(_btnInstall);
        panelBottom.Controls.Add(_btnUpdate);

        _installProgressPanel.Controls.Add(_installProgressBar);
        _installProgressPanel.Controls.Add(_lblInstallStatus);

        _log.Dock = DockStyle.Fill;
        _log.Padding = new Padding(8);

        Controls.Add(_log);
        Controls.Add(panelBottom);
        Controls.Add(_installProgressPanel);
        Controls.Add(panelTop);
    }

    private void OnMainFormLoad(object? sender, EventArgs e)
    {
        _chkAutoLaunchCad.Checked = true;

        _cbbAcadVersion.Items.Clear();
        foreach (var c in AcadInstallationScanner.EnumerateChoices())
            _cbbAcadVersion.Items.Add(c);
        _cbbAcadVersion.DisplayMember = "Display";

        var (lastVersionKey, lastProductKey) = BundleInstall.ReadLastAcadSelection();
        if (!TryRestoreAcadChoice(lastVersionKey, lastProductKey) && _cbbAcadVersion.Items.Count > 0)
            _cbbAcadVersion.SelectedIndex = 0;

        RefreshAcadSelectionUi();

    }

    private bool TryRestoreAcadChoice(string? versionKey, string? productKey)
    {
        if (string.IsNullOrWhiteSpace(versionKey) || string.IsNullOrWhiteSpace(productKey))
            return false;

        for (var i = 0; i < _cbbAcadVersion.Items.Count; i++)
        {
            if (_cbbAcadVersion.Items[i] is not AcadInstallationScanner.Choice choice)
                continue;

            if (!string.Equals(choice.VersionKey, versionKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(choice.ProductKey, productKey, StringComparison.OrdinalIgnoreCase))
                continue;

            _cbbAcadVersion.SelectedIndex = i;
            return true;
        }

        return false;
    }

    private void SaveSelectedAcadChoice()
    {
        if (_cbbAcadVersion.SelectedItem is not AcadInstallationScanner.Choice choice)
            return;

        BundleInstall.WriteLastAcadSelection(choice.VersionKey, choice.ProductKey);
    }

    private void RefreshAcadSelectionUi()
    {
        if (_cbbAcadVersion.SelectedItem is not AcadInstallationScanner.Choice choice)
            return;

        if (choice.IsManualInstallOnly)
        {
            _chkAutoLaunchCad.Checked = false;
            _chkAutoLaunchCad.Enabled = false;
            _chkAutoLaunchCad.Text = "安装后是否自动启动AutoCAD程序（未检测到 AutoCAD 2024，暂不可用）";
            return;
        }

        _chkAutoLaunchCad.Enabled = !_isInstalling;
        _chkAutoLaunchCad.Text = "安装后是否自动启动AutoCAD程序";
    }

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isInstalling || _allowCloseWhileInstalling)
            return;

        e.Cancel = true;
        MessageBox.Show(this, "安装仍在进行中，请等待当前操作完成后再关闭窗口。", Text, MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static string GetDefaultInstallRootPath()
    {
        return Path.Combine(BundleInstall.DefaultParentInstallPath(), BundleInstall.DefaultInstallFolderName);
    }

    private string GetBrowseStartPath()
    {
        var installRoot = GetInstallRootOrEmpty();
        if (installRoot.Length > 0)
        {
            if (Directory.Exists(installRoot))
                return installRoot;

            try
            {
                var parent = Path.GetDirectoryName(installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                    return parent;
            }
            catch (Exception ex)
            {
                // 忽略并回退到默认目录。
                System.Diagnostics.Debug.WriteLine($"TryGetExistingInstallRootParent: {ex.Message}");
            }
        }

        return BundleInstall.DefaultParentInstallPath();
    }

    private string GetInitialFolderBrowseStartPath()
    {
        var current = _txtInitialFolderPath.Text.Trim();
        if (current.Length > 0)
        {
            try
            {
                var fullPath = Path.GetFullPath(current);
                if (Directory.Exists(fullPath))
                    return fullPath;

                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                    return parent;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetInitialFolderBrowseStartPath: {ex.Message}");
            }
        }

        var setupDir = BundleInstall.GetSetupExeDirectory();
        var defaultFolder = BundleInstall.TryResolveDefaultInitialUserDataFolderPath(setupDir)
                            ?? BundleInstall.TryResolveDefaultInitialUserDataFolderPath(AppContext.BaseDirectory)
                            ?? BundleInstall.TryResolveDefaultInitialUserDataFolderPath(Environment.CurrentDirectory);
        if (!string.IsNullOrWhiteSpace(defaultFolder))
            return defaultFolder;

        return BundleInstall.DefaultParentInstallPath();
    }

    private static string GetDefaultInitialFolderPathOrEmpty()
    {
        var setupDir = BundleInstall.GetSetupExeDirectory();
        return BundleInstall.TryResolveDefaultInitialUserDataFolderPath(setupDir)
               ?? BundleInstall.TryResolveDefaultInitialUserDataFolderPath(AppContext.BaseDirectory)
               ?? BundleInstall.TryResolveDefaultInitialUserDataFolderPath(Environment.CurrentDirectory)
               ?? "";
    }

    private string GetInstallRootOrEmpty()
    {
        var installRoot = _txtFullPath.Text.Trim();
        if (installRoot.Length == 0)
            return "";

        try
        {
            return Path.GetFullPath(installRoot);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetInstallRootOrEmpty: {ex.Message}");
            return "";
        }
    }

    private bool TryBuildInstallOptions(
        AcadInstallationScanner.Choice choice,
        out InstallOptions options,
        out string validationMessage)
    {
        options = default!;
        validationMessage = "";
        if (!TrySplitInstallRoot(_txtFullPath.Text, out var parent, out var folderName))
        {
            validationMessage = "请输入有效的完整安装路径，例如 D:\\C_tool插件。\r\n不能直接使用磁盘根目录。";
            return false;
        }

        if (!TryResolveInitialFolderPath(out var initialFolderPath, out validationMessage))
            return false;

        options = new InstallOptions(
            parent,
            folderName,
            _rbAll.Checked,
            choice.VersionKey,
            choice.ProductKey,
            initialFolderPath);
        return true;
    }

    private bool TryResolveInitialFolderPath(out string? initialFolderPath, out string validationMessage)
    {
        initialFolderPath = null;
        validationMessage = "";

        var text = _txtInitialFolderPath.Text.Trim();
        if (text.Length == 0)
            return true;

        try
        {
            var fullPath = Path.GetFullPath(text);
            if (!Directory.Exists(fullPath))
            {
                validationMessage = "初始化文件夹不存在：\r\n" + fullPath;
                return false;
            }

            initialFolderPath = fullPath;
            return true;
        }
        catch (Exception ex)
        {
            validationMessage = "初始化文件夹路径无效：\r\n" + ex.Message;
            return false;
        }
    }

    private static bool TrySplitInstallRoot(string installRootText, out string parent, out string folderName)
    {
        parent = "";
        folderName = "";

        if (string.IsNullOrWhiteSpace(installRootText))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(installRootText.Trim());
            var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            parent = Path.GetDirectoryName(normalized) ?? "";
            folderName = Path.GetFileName(normalized);
            return parent.Length > 0 && folderName.Length > 0;
        }
        catch
        {
            return false;
        }
    }


    private void AppendLog(string s)
    {
        if (IsDisposed || Disposing)
            return;

        _pendingLogLines.Enqueue(s);
        RequestLogFlush();
    }

    private void RequestLogFlush()
    {
        if (IsDisposed || Disposing)
            return;

        if (Interlocked.Exchange(ref _logFlushPending, 1) != 0)
            return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(FlushPendingLogLines));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _logFlushPending, 0);
            }
            return;
        }

        FlushPendingLogLines();
    }

    private void FlushPendingLogLines()
    {
        if (IsDisposed || Disposing)
            return;

        if (InvokeRequired)
        {
            RequestLogFlush();
            return;
        }

        var builder = new StringBuilder();
        while (_pendingLogLines.TryDequeue(out var line))
            builder.AppendLine(line);

        Interlocked.Exchange(ref _logFlushPending, 0);
        if (builder.Length == 0)
            return;

        _log.AppendText(builder.ToString());
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();

        if (!_pendingLogLines.IsEmpty)
            RequestLogFlush();
    }

    private void SetInstallUiBusy(bool isBusy)
    {
        _isInstalling = isBusy;
        if (IsDisposed || Disposing)
            return;

        if (!isBusy)
            FlushPendingLogLines();

        UseWaitCursor = isBusy;
        _installProgressPercent = 0;
        _installProgressPanel.Visible = isBusy;
        _lblInstallStatus.Visible = isBusy;
        _installProgressBar.Visible = isBusy;
        _installProgressBar.Style = ProgressBarStyle.Continuous;
        _installProgressBar.Value = 0;
        _lblInstallStatus.Text = isBusy ? "正在准备安装… 0%" : string.Empty;
        _btnInstall.Enabled = !isBusy;
        _btnUpdate.Enabled = !isBusy;
        _btnInstall.Text = isBusy ? "安装中…" : "安装";
        _btnBrowse.Enabled = !isBusy;
        _btnBrowseInitialFolder.Enabled = !isBusy;
        _txtFullPath.Enabled = !isBusy;
        _txtInitialFolderPath.Enabled = !isBusy;
        _cbbAcadVersion.Enabled = !isBusy;
        _rbUser.Enabled = !isBusy;
        _rbAll.Enabled = !isBusy;
        _chkAutoLaunchCad.Enabled = !isBusy;
    }

    private void SetInstallProgress(string status, int percent)
    {
        if (IsDisposed || Disposing)
            return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<string, int>(SetInstallProgressCore), status, percent);
            }
            catch (InvalidOperationException)
            {
                // 窗口正在关闭，忽略后台状态更新。
            }

            return;
        }

        SetInstallProgressCore(status, percent);
    }

    private void SetInstallProgressCore(string status, int percent)
    {
        _installProgressPercent = Math.Clamp(percent, 0, 100);
        _installProgressPanel.Visible = true;
        _lblInstallStatus.Visible = true;
        _installProgressBar.Visible = true;
        _installProgressBar.Style = ProgressBarStyle.Continuous;
        _installProgressBar.Value = _installProgressPercent;
        _lblInstallStatus.Text = $"{status} {_installProgressPercent}%";
    }

    private void CloseAfterSuccessfulCompletion()
    {
        if (IsDisposed || Disposing)
            return;

        _allowCloseWhileInstalling = true;
        Close();
    }

    private async Task RunInstallAsync()
    {
        var closeAfterSuccess = false;
        if (_cbbAcadVersion.Items.Count == 0 ||
            _cbbAcadVersion.SelectedItem is not AcadInstallationScanner.Choice choice)
        {
            MessageBox.Show(this,
                "未检测到可用于写入的 AutoCAD 产品注册表项。\r\n\r\n" +
                "安装器会优先匹配带 " + AcadInstallationScanner.ProductKeyZhCnLocaleSuffix + " 的产品键；若不存在，也会自动回退到其它语言产品键。\r\n\r\n" +
                "请确认已安装并至少启动过一次目标 AutoCAD，然后重试。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (!TryBuildInstallOptions(choice, out var options, out var validationMessage))
        {
            MessageBox.Show(this,
                validationMessage,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        SetInstallUiBusy(true);
        _log.Clear();
        try
        {
            SetInstallProgress("正在检查安装配置…", 3);
            if (choice.IsManualInstallOnly)
            {
                AppendLog("当前选择：仅复制插件文件（手动模式）。本次不会自动启动 AutoCAD；若本机没有已注册的 AutoCAD 产品键，也不会写入 Applications / TRUSTEDPATHS。");
            }

            var bundleSourceRoot = BundleInstall.GetSetupExeDirectory();

            SetInstallProgress("正在处理插件并写入 CAD 配置…", 10);
            AppendLog("开始安装，请稍候…");

            var result = await Task.Run(() =>
                InstallEngine.Execute(
                    options,
                    bundleSourceRoot,
                    AppendLog,
                    update => ReportOverallProgress(10, 95, update)));

            switch (result.Code)
            {
                case InstallExitCode.Ok when result.InstallRoot != null && result.InstalledBundleNames != null:
                    SetInstallProgress("安装已完成，正在整理结果…", 96);
                    var cadHint = choice.IsManualInstallOnly
                        ? "本次按手动模式完成：插件文件已复制到安装目录。若后续需要自动加载，请在目标 AutoCAD 环境准备好后重新运行安装器，或手动配置受信任路径与启动项。"
                        : "插件从安装目录加载；已合并所选范围的受信任路径，并写入 Applications 启动项（若日志中提示未写入，请先运行过一次对应 AutoCAD 再重装）。";
                    MessageBox.Show(this,
                        $"安装完成。\r\n\r\n安装目录：{result.InstallRoot}\r\n（已更新 Plugin；User 设置文件与 User\\C_TOOL 自动数据已保留）\r\n\r\n已安装 bundle：{string.Join(", ", result.InstalledBundleNames)}\r\n\r\n" +
                        cadHint,
                        Text,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    if (!choice.IsManualInstallOnly && _chkAutoLaunchCad.Checked)
                    {
                        SetInstallProgress("正在启动 AutoCAD…", 98);
                        if (AcadLauncher.TryLaunch(choice.VersionKey, choice.ProductKey, out var launchErr))
                        {
                            AppendLog(
                                "已启动 AutoCAD 2024。若随后出现 Autodesk Licensing Manager / AdskLicensingAgent 报错，通常属于 Autodesk 启动或许可环境，与本次插件复制无关。");
                        }
                        else
                        {
                            MessageBox.Show(this,
                                "已勾选自动启动，但未能启动 AutoCAD 2024。\r\n\r\n" +
                                (launchErr ?? "请手动从开始菜单启动。"),
                                Text,
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                    }

                    SetInstallProgress("安装完成。", 100);
                    closeAfterSuccess = true;

                    break;
                case InstallExitCode.InvalidInput:
                case InstallExitCode.NoBundles:
                    if (!string.IsNullOrEmpty(result.DialogMessage))
                        MessageBox.Show(this, result.DialogMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                case InstallExitCode.Unauthorized:
                case InstallExitCode.FileLocked:
                    if (!string.IsNullOrEmpty(result.DialogMessage))
                        MessageBox.Show(this, result.DialogMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                case InstallExitCode.Error:
                    if (!string.IsNullOrEmpty(result.DialogMessage))
                        MessageBox.Show(this, result.DialogMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    break;
            }
        }
        finally
        {
            SetInstallUiBusy(false);
            RefreshAcadSelectionUi();

            if (closeAfterSuccess)
            {
                CloseAfterSuccessfulCompletion();
            }
            else
            {
                _allowCloseWhileInstalling = false;
            }
        }
    }

    private async Task RunUpdateAsync()
    {
        var closeAfterSuccess = false;
        var manifestUrl = UpdateSettings.GetManifestUrl();
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            MessageBox.Show(this,
                "还没有配置在线更新地址或共享路径。\r\n\r\n" +
                $"可设置环境变量 {UpdateSettings.EnvironmentVariableName}，或写入注册表 HKCU\\{BundleInstall.RegistryKeyPath}\\{BundleInstall.RegistryValueUpdateManifestUrl}。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_cbbAcadVersion.Items.Count == 0 ||
            _cbbAcadVersion.SelectedItem is not AcadInstallationScanner.Choice choice)
        {
            MessageBox.Show(this,
                "未检测到可用于写入的 AutoCAD 2024 注册表项。请先确认目标 AutoCAD 已安装并至少启动过一次。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (!TryBuildInstallOptions(choice, out var options, out var validationMessage))
        {
            MessageBox.Show(this,
                validationMessage,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        string? tempRoot = null;
        SetInstallUiBusy(true);
        _log.Clear();
        try
        {
            var currentVersion = UpdateSettings.GetCurrentProductVersion();
            SetInstallProgress("正在检查更新...", 5);
            AppendLog($"当前版本：{currentVersion}");
            AppendLog($"更新清单：{manifestUrl}");

            var update = await UpdateClient.CheckAsync(manifestUrl, currentVersion, CancellationToken.None);
            var latestVersion = ProductVersionComparer.NormalizeForDisplay(update.Manifest.Version);
            AppendLog($"服务器版本：{latestVersion}");

            if (!update.IsNewer)
            {
                SetInstallProgress("已经是最新版本。", 100);
                MessageBox.Show(this,
                    $"当前已是最新版本。\r\n\r\n当前版本：{update.CurrentVersion}\r\n服务器版本：{latestVersion}",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var releaseNotes = string.IsNullOrWhiteSpace(update.Manifest.ReleaseNotes)
                ? ""
                : "\r\n\r\n更新说明：\r\n" + update.Manifest.ReleaseNotes.Trim();
            var confirm = MessageBox.Show(this,
                $"发现新版本 {latestVersion}。\r\n\r\n当前版本：{update.CurrentVersion}\r\n更新包：{update.BundleZipUri}{releaseNotes}\r\n\r\n安装前请关闭正在使用该插件的 AutoCAD 2024。",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                AppendLog("已取消在线更新。");
                return;
            }

            tempRoot = Path.Combine(Path.GetTempPath(), "C_TOOL_Update_" + Guid.NewGuid().ToString("N"));
            var downloadRoot = Path.Combine(tempRoot, "download");
            var extractRoot = Path.Combine(tempRoot, "extract");

            SetInstallProgress("正在下载更新包...", 20);
            var zipPath = await UpdateClient.DownloadBundleZipAsync(
                update,
                downloadRoot,
                progress => ReportOverallProgress(20, 58, progress),
                CancellationToken.None);

            SetInstallProgress("正在校验更新包...", 60);
            UpdateClient.ValidateDownloadedBundle(zipPath, update.Manifest);

            SetInstallProgress("正在解压更新包...", 66);
            BundleInstall.ExtractZipToDirectory(zipPath, extractRoot);

            SetInstallProgress("正在安装更新...", 70);
            var result = await Task.Run(() =>
                InstallEngine.Execute(
                    options,
                    extractRoot,
                    AppendLog,
                    progress => ReportOverallProgress(70, 96, progress)));

            switch (result.Code)
            {
                case InstallExitCode.Ok when result.InstallRoot != null && result.InstalledBundleNames != null:
                    SetInstallProgress("更新完成。", 100);
                    MessageBox.Show(this,
                        $"更新完成。\r\n\r\n版本：{latestVersion}\r\n安装目录：{result.InstallRoot}\r\n已更新 bundle：{string.Join(", ", result.InstalledBundleNames)}",
                        Text,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);

                    if (!choice.IsManualInstallOnly && _chkAutoLaunchCad.Checked)
                    {
                        SetInstallProgress("正在启动 AutoCAD...", 98);
                        if (AcadLauncher.TryLaunch(choice.VersionKey, choice.ProductKey, out var launchErr))
                        {
                        }
                        else
                        {
                            MessageBox.Show(this,
                                "更新已完成，但未能自动启动 AutoCAD 2024。\r\n\r\n" +
                                (launchErr ?? "请手动从开始菜单启动。"),
                                Text,
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                    }

                    SetInstallProgress("更新完成。", 100);
                    closeAfterSuccess = true;

                    break;
                case InstallExitCode.InvalidInput:
                case InstallExitCode.NoBundles:
                case InstallExitCode.Unauthorized:
                case InstallExitCode.FileLocked:
                case InstallExitCode.Error:
                    if (!string.IsNullOrEmpty(result.DialogMessage))
                        MessageBox.Show(this, result.DialogMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
            }
        }
        catch (HttpRequestException ex)
        {
            AppendLog("在线更新失败：" + ex.Message);
            MessageBox.Show(this,
                "无法连接更新服务器，或服务器返回了错误。\r\n\r\n" + ex.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (IOException ex)
        {
            AppendLog("在线更新失败：" + ex.Message);
            MessageBox.Show(this,
                "无法读取更新文件。请确认共享目录已连接、路径正确，并且当前用户有读取权限。\r\n\r\n" + ex.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendLog("在线更新失败：" + ex.Message);
            MessageBox.Show(this,
                "读取更新文件时被系统拒绝访问。请确认共享目录权限允许当前用户读取。\r\n\r\n" + ex.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (InvalidDataException ex)
        {
            AppendLog("更新包无效：" + ex.Message);
            MessageBox.Show(this,
                "更新包校验失败。\r\n\r\n" + ex.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendLog("在线更新失败：" + ex);
            MessageBox.Show(this,
                "在线更新失败。\r\n\r\n" + ex.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempRoot))
            {
                try
                {
                    if (Directory.Exists(tempRoot))
                        Directory.Delete(tempRoot, recursive: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Cleanup update temp failed: {ex.Message}");
                }
            }

            SetInstallUiBusy(false);
            RefreshAcadSelectionUi();

            if (closeAfterSuccess)
            {
                CloseAfterSuccessfulCompletion();
            }
            else
            {
                _allowCloseWhileInstalling = false;
            }
        }
    }

    private void ReportOverallProgress(int rangeStart, int rangeEnd, SetupProgressUpdate update)
    {
        var localPercent = Math.Clamp(update.Percent, 0, 100);
        var overallPercent = rangeStart +
                             (int)Math.Round((rangeEnd - rangeStart) * (localPercent / 100d),
                                 MidpointRounding.AwayFromZero);
        SetInstallProgress(update.Status, overallPercent);
    }

}
