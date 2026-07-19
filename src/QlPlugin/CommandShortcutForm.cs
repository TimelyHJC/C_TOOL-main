using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;

namespace QlPlugin;

/// <summary>
/// 命令快捷方式管理窗体
/// </summary>
public class CommandShortcutForm : Form
{
    private readonly DataGridView _grid;
    private readonly TextBox _txtAlias;
    private readonly ComboBox _cmbCommand;
    private readonly Button _btnAdd;
    private readonly Button _btnCopyPgp;
    private readonly Button _btnOpenPgp;
    private readonly ListBox _lstCustom;
    private readonly Button _btnRemove;
    private List<ShortcutEntry> _customShortcuts = [];

    private static readonly List<CommandInfo> Commands =
    [
        new("F_QL", "图块列表", "显示图块列表，按大小排序。先选中对象则只显示选中图块。双击可插入图块。"),
        new("QLL", "一键清理", "执行 AUDIT、PURGE、RegApps、OVERKILL 清理 DWG 文件。先选中对象则先对选中执行 OVERKILL。"),
        new("KKK", "快捷命令", "打开本页面，查看和修改命令快捷方式。")
    ];

    public CommandShortcutForm()
    {
        Text = "统一快捷命令设置 - 支持本插件及其他插件";
        Size = new Size(650, 480);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(650, 420);
        QlCadTheme.ApplyWindow(this);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
        };
        QlCadTheme.ApplyGrid(_grid);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "命令", Width = 80, DataPropertyName = "Command" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明", Width = 200, DataPropertyName = "Description", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "快捷命令", Width = 100, DataPropertyName = "Shortcut" });

        _grid.DataSource = Commands.Select(c => new { c.Command, c.Description, Shortcut = c.Command }).ToList();

        var btnFont = new Font("Microsoft YaHei UI", 10.5f);
        var lblCustom = new Label { Text = "自定义快捷键（支持本插件及任意其他插件。点击「写入 PGP」可追加到文件末尾，优先级最高）", Dock = DockStyle.Bottom, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 14, 0), AutoEllipsis = true };
        QlCadTheme.ApplyLabel(lblCustom, header: true);
        var panelCustom = new Panel { Dock = DockStyle.Bottom, Height = 178 };
        QlCadTheme.ApplyToolbar(panelCustom);
        var lblAlias = new Label { Text = "快捷键:", Location = new Point(8, 14), AutoSize = true, Font = btnFont };
        _txtAlias = new TextBox { Width = 90, Height = 28, Location = new Point(70, 10), Font = btnFont };
        var lblCmd = new Label { Text = "命令:", Location = new Point(170, 14), AutoSize = true, Font = btnFont };
        _cmbCommand = new ComboBox { Width = 160, Height = 28, Location = new Point(220, 10), DropDownStyle = ComboBoxStyle.DropDown, Font = btnFont };
        _cmbCommand.Text = "F_QL";
        _btnAdd = new Button { Text = "添加", Size = new Size(70, 36), Location = new Point(390, 8), Font = btnFont };
        var btnLoadCmds = new Button { Text = "加载全部命令", Size = new Size(120, 36), Location = new Point(470, 8), Font = btnFont };
        _lstCustom = new ListBox { Width = 220, Height = 100, Location = new Point(8, 52), Font = btnFont };
        _btnRemove = new Button { Text = "移除选中", Size = new Size(100, 36), Location = new Point(240, 52), Font = btnFont };
        _btnCopyPgp = new Button { Text = "复制 PGP 内容", Size = new Size(130, 36), Location = new Point(350, 52), Font = btnFont };
        _btnOpenPgp = new Button { Text = "打开 PGP 文件", Size = new Size(120, 36), Location = new Point(490, 52), Font = btnFont };
        var btnWritePgp = new Button { Text = "写入 PGP（末尾追加，优先级最高）", Size = new Size(280, 36), Location = new Point(240, 96), Font = btnFont };

        foreach (var label in new[] { lblAlias, lblCmd })
        {
            QlCadTheme.ApplyLabel(label);
        }

        QlCadTheme.ApplyTextBox(_txtAlias);
        QlCadTheme.ApplyComboBox(_cmbCommand);
        QlCadTheme.ApplyListBox(_lstCustom);
        QlCadTheme.ApplyButton(_btnAdd, primary: true);
        QlCadTheme.ApplyButton(btnLoadCmds);
        QlCadTheme.ApplyButton(_btnRemove);
        QlCadTheme.ApplyButton(_btnCopyPgp);
        QlCadTheme.ApplyButton(_btnOpenPgp);
        QlCadTheme.ApplyButton(btnWritePgp, primary: true);

        _btnAdd.Click += BtnAdd_Click;
        _btnRemove.Click += BtnRemove_Click;
        _btnCopyPgp.Click += BtnCopyPgp_Click;
        _btnOpenPgp.Click += BtnOpenPgp_Click;
        btnWritePgp.Click += BtnWritePgp_Click;
        btnLoadCmds.Click += BtnLoadCmds_Click;

        LoadCommandList();

        panelCustom.Controls.AddRange([lblAlias, _txtAlias, lblCmd, _cmbCommand, _btnAdd, btnLoadCmds, _lstCustom, _btnRemove, _btnCopyPgp, _btnOpenPgp, btnWritePgp]);
        panelCustom.Paint += (_, e) =>
        {
            using var pen = new Pen(QlCadTheme.StatusLine);
            e.Graphics.DrawLine(pen, 0, 0, panelCustom.Width, 0);
        };
        panelCustom.Resize += (_, _) => ArrangeShortcutPanelButtons();
        ArrangeShortcutPanelButtons();

        void ArrangeShortcutPanelButtons()
        {
            var right = panelCustom.ClientSize.Width - panelCustom.Padding.Right;
            _btnOpenPgp.Left = Math.Max(350, right - _btnOpenPgp.Width);
            _btnCopyPgp.Left = _btnOpenPgp.Left - _btnCopyPgp.Width - 10;
            btnLoadCmds.Left = Math.Max(_btnAdd.Right + 10, right - btnLoadCmds.Width);
            btnWritePgp.Left = Math.Max(_btnRemove.Right + 10, right - btnWritePgp.Width);
        }

        Controls.Add(_grid);
        Controls.Add(panelCustom);
        Controls.Add(lblCustom);

        LoadCustomShortcuts();
        RefreshCustomList();
    }

    private void BtnAdd_Click(object? sender, EventArgs e)
    {
        var alias = _txtAlias.Text.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(alias))
        {
            MessageBox.Show("请输入快捷键。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (alias.Length > 10)
        {
            MessageBox.Show("快捷键不宜过长。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var cmd = _cmbCommand.Text.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(cmd) || cmd.StartsWith("---"))
        {
            MessageBox.Show("请输入或选择要绑定的命令。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_customShortcuts.Any(s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
        {
            _customShortcuts.RemoveAll(s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
        }
        _customShortcuts.Add(new ShortcutEntry(alias, cmd));
        ShortcutConfig.Save(_customShortcuts);
        RefreshCustomList();
        _txtAlias.Clear();
        _cmbCommand.Text = cmd;
    }

    private void BtnRemove_Click(object? sender, EventArgs e)
    {
        var sel = _lstCustom.SelectedItem as string;
        if (sel == null) return;
        var parts = sel.Split(new[] { " → " }, StringSplitOptions.None);
        if (parts.Length == 2)
        {
            _customShortcuts.RemoveAll(s => s.Alias == parts[0] && s.Command == parts[1]);
            ShortcutConfig.Save(_customShortcuts);
            RefreshCustomList();
        }
    }

    private void BtnOpenPgp_Click(object? sender, EventArgs e)
    {
        try
        {
            var pgpPath = FindPgpPath();
            if (!string.IsNullOrEmpty(pgpPath) && File.Exists(pgpPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pgpPath,
                    UseShellExecute = true
                });
            }
            else
            {
                MessageBox.Show("未找到 acad.pgp 文件。\n\n可手动在 AutoCAD 中运行 ALIASEDIT 命令，或通过 选项→文件→支持文件搜索路径 查找。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"打开失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string? FindPgpPath() => PgpPaths.GetWritePgpPath() ?? PgpPaths.GetAllPgpPaths().FirstOrDefault();

    private void BtnWritePgp_Click(object? sender, EventArgs e)
    {
        if (_customShortcuts.Count == 0)
        {
            MessageBox.Show("暂无自定义快捷键，请先添加。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var pgpPath = FindPgpPath();
        if (string.IsNullOrEmpty(pgpPath) || !File.Exists(pgpPath))
        {
            MessageBox.Show("未找到 acad.pgp 文件。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var ourAliases = _customShortcuts.Select(s => s.Alias.ToUpperInvariant()).ToHashSet();
            var lines = File.ReadAllLines(pgpPath).ToList();
            lines.RemoveAll(line =>
            {
                var t = line.Trim();
                if (string.IsNullOrEmpty(t) || t.StartsWith(";")) return false;
                var comma = t.IndexOf(',');
                if (comma <= 0) return false;
                var alias = t[..comma].Trim().ToUpperInvariant();
                return ourAliases.Contains(alias);
            });
            var block = new List<string>
            {
                "",
                ";; ===== QL Plugin 快捷命令（末尾追加，优先级最高） =====",
                ";; 以下别名会覆盖文件中之前的同名定义（PGP 规则：后定义优先）"
            };
            block.AddRange(_customShortcuts.Select(s => $"{s.Alias}, *{s.Command}"));
            lines.AddRange(block);
            File.WriteAllLines(pgpPath, lines);
            MessageBox.Show("已写入 acad.pgp 末尾。\n\n请运行 REINIT 命令，勾选「PGP 文件」重新加载。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"写入失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void BtnCopyPgp_Click(object? sender, EventArgs e)
    {
        var lines = _customShortcuts.Select(s => $"{s.Alias}, *{s.Command}").ToList();
        if (lines.Count == 0)
        {
            MessageBox.Show("暂无自定义快捷键。添加后点击此处可复制到剪贴板，粘贴到 acad.pgp 中。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var text = string.Join(Environment.NewLine, lines);
        Clipboard.SetText(text);
        MessageBox.Show("已复制到剪贴板。\n\n请打开 acad.pgp 文件，将内容粘贴到命令别名区域，然后运行 REINIT 命令选择 PGP 文件重新加载。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void LoadCommandList()
    {
        _cmbCommand.Items.Clear();
        _cmbCommand.Items.Add("--- 本插件 ---");
        _cmbCommand.Items.AddRange(Commands.Select(c => c.Command).ToArray());
        _cmbCommand.Items.Add("--- 常用 CAD 命令 ---");
        _cmbCommand.Items.AddRange(CommonCommands);
        _cmbCommand.Text = "F_QL";
    }

    private void BtnLoadCmds_Click(object? sender, EventArgs e)
    {
        try
        {
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Commands.Select(x => x.Command)) all.Add(c);
            foreach (var c in CommonCommands) all.Add(c);
            foreach (var c in GetCommandsFromPgp()) all.Add(c);
            foreach (var c in GetCommandsFromNetPlugins()) all.Add(c);
            var sorted = all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            _cmbCommand.Items.Clear();
            _cmbCommand.Items.Add("--- 全部命令 ---");
            _cmbCommand.Items.AddRange(sorted.ToArray());
            _cmbCommand.Text = sorted.FirstOrDefault(s => s == "F_QL")
                ?? sorted.FirstOrDefault(s => s == "QL")
                ?? sorted.FirstOrDefault()
                ?? "";
            var gridData = Commands.Select(c => new { c.Command, c.Description, Shortcut = c.Command }).ToList();
            gridData.AddRange(sorted.Except(Commands.Select(x => x.Command), StringComparer.OrdinalIgnoreCase)
                .Select(c => new { Command = c, Description = GetCommandDescription(c), Shortcut = c }));
            _grid.DataSource = gridData;
            MessageBox.Show($"已加载 {sorted.Count} 个命令（含本插件、PGP、其他插件及 CAD 内置）。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"加载失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string GetCommandDescription(string cmd)
    {
        var known = Commands.FirstOrDefault(c => c.Command.Equals(cmd, StringComparison.OrdinalIgnoreCase));
        if (known?.Description != null)
        {
            return known.Description;
        }

        return CommonDescriptions.TryGetValue(cmd.ToUpperInvariant(), out var description)
            ? description
            : "";
    }

    private static List<string> GetCommandsFromPgp()
    {
        var result = new List<string>();
        foreach (var path in PgpPaths.GetAllPgpPaths())
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var t = line.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith(";")) continue;
                    var comma = t.IndexOf(',');
                    if (comma <= 0) continue;
                    var cmd = t[(comma + 1)..].Trim().TrimStart('*').Trim();
                    if (!string.IsNullOrEmpty(cmd)) result.Add(cmd);
                }
            }
            catch { }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> GetCommandsFromNetPlugins()
    {
        var result = new List<string>();
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetExportedTypes())
                    {
                        foreach (var method in type.GetMethods())
                        {
                            var attrs = method.GetCustomAttributes(typeof(CommandMethodAttribute), true);
                            foreach (CommandMethodAttribute attr in attrs)
                            {
                                var name = attr.GlobalName;
                                if (!string.IsNullOrEmpty(name)) result.Add(name);
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly string[] CommonCommands =
    [
        "ZOOM", "PAN", "LINE", "L", "CIRCLE", "C", "ARC", "A", "PLINE", "PL", "RECTANG", "REC",
        "COPY", "CO", "MOVE", "M", "ERASE", "E", "TRIM", "TR", "EXTEND", "EX", "OFFSET", "O",
        "ROTATE", "RO", "SCALE", "SC", "MIRROR", "MI", "ARRAY", "AR", "FILLET", "F", "CHAMFER", "CHA",
        "BLOCK", "B", "INSERT", "I", "WBLOCK", "W", "EXPLODE", "X", "HATCH", "H", "BHATCH",
        "LAYER", "LA", "DIMLINEAR", "DLI", "DIMRADIUS", "DRA", "DIMDIAMETER", "DDI", "TEXT", "MTEXT", "MT",
        "PURGE", "AUDIT", "OVERKILL", "REINIT", "OPTIONS", "OP", "SAVE", "OPEN", "NEW", "QUIT"
    ];

    private static readonly Dictionary<string, string> CommonDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZOOM"] = "缩放视图", ["Z"] = "缩放", ["PAN"] = "平移视图", ["P"] = "平移",
        ["LINE"] = "直线", ["L"] = "直线", ["CIRCLE"] = "圆", ["C"] = "圆", ["ARC"] = "圆弧", ["A"] = "圆弧",
        ["PLINE"] = "多段线", ["PL"] = "多段线", ["RECTANG"] = "矩形", ["REC"] = "矩形",
        ["COPY"] = "复制", ["CO"] = "复制", ["CP"] = "复制", ["MOVE"] = "移动", ["M"] = "移动",
        ["ERASE"] = "删除", ["E"] = "删除", ["TRIM"] = "修剪", ["TR"] = "修剪", ["EXTEND"] = "延伸", ["EX"] = "延伸",
        ["OFFSET"] = "偏移", ["O"] = "偏移", ["ROTATE"] = "旋转", ["RO"] = "旋转",
        ["SCALE"] = "缩放", ["SC"] = "缩放", ["MIRROR"] = "镜像", ["MI"] = "镜像",
        ["ARRAY"] = "阵列", ["AR"] = "阵列", ["FILLET"] = "圆角", ["F"] = "圆角", ["CHAMFER"] = "倒角", ["CHA"] = "倒角",
        ["BLOCK"] = "创建块", ["B"] = "创建块", ["INSERT"] = "插入块", ["I"] = "插入块",
        ["WBLOCK"] = "写块", ["W"] = "写块", ["EXPLODE"] = "分解", ["X"] = "分解",
        ["HATCH"] = "填充", ["H"] = "填充", ["BHATCH"] = "边界填充",
        ["LAYER"] = "图层", ["LA"] = "图层", ["DIMLINEAR"] = "线性标注", ["DLI"] = "线性标注",
        ["DIMRADIUS"] = "半径标注", ["DRA"] = "半径标注", ["DIMDIAMETER"] = "直径标注", ["DDI"] = "直径标注",
        ["TEXT"] = "单行文字", ["MTEXT"] = "多行文字", ["MT"] = "多行文字",
        ["PURGE"] = "清理", ["AUDIT"] = "检查图形", ["OVERKILL"] = "删除重复对象",
        ["REINIT"] = "重新初始化", ["OPTIONS"] = "选项", ["OP"] = "选项",
        ["SAVE"] = "保存", ["OPEN"] = "打开", ["NEW"] = "新建", ["QUIT"] = "退出",
        ["STRETCH"] = "拉伸", ["S"] = "拉伸", ["BREAK"] = "打断", ["BR"] = "打断",
        ["JOIN"] = "合并", ["J"] = "合并", ["SPLINE"] = "样条曲线", ["SPL"] = "样条曲线",
        ["ELLIPSE"] = "椭圆", ["EL"] = "椭圆", ["POLYGON"] = "多边形", ["POL"] = "多边形",
        ["REGION"] = "面域", ["REG"] = "面域", ["BOUNDARY"] = "边界", ["BO"] = "边界",
        ["MATCHPROP"] = "特性匹配", ["MA"] = "特性匹配", ["DIMSTYLE"] = "标注样式", ["D"] = "标注样式",
        ["LEADER"] = "引线", ["LE"] = "引线", ["QLEADER"] = "快速引线",
        ["UCS"] = "用户坐标系", ["RE"] = "重生成", ["REGEN"] = "重生成",
        ["UNDO"] = "放弃", ["U"] = "放弃", ["REDO"] = "重做", ["OOPS"] = "恢复删除",
        ["PROPERTIES"] = "特性", ["CHPROP"] = "修改特性", ["LIST"] = "列表",
        ["DIST"] = "距离", ["AREA"] = "面积", ["ID"] = "点坐标",
        ["EXT"] = "拉伸", ["REV"] = "旋转", ["SWEEP"] = "扫掠", ["LOFT"] = "放样",
        ["SLICE"] = "剖切", ["SL"] = "剖切", ["UNION"] = "并集", ["UNI"] = "并集",
        ["SUBTRACT"] = "差集", ["SU"] = "差集", ["INTERSECT"] = "交集", ["IN"] = "交集",
        ["3DORBIT"] = "三维动态观察", ["3DO"] = "三维动态观察", ["VPOINT"] = "视点",
        ["ALIGN"] = "对齐", ["AL"] = "对齐", ["DIVIDE"] = "定数等分", ["DIV"] = "定数等分",
        ["MEASURE"] = "定距等分", ["ME"] = "定距等分", ["PEDIT"] = "编辑多段线", ["PE"] = "编辑多段线",
        ["LENGTHEN"] = "拉长", ["LEN"] = "拉长", ["SCALETEXT"] = "缩放文字",
        ["QSELECT"] = "快速选择", ["FILTER"] = "对象过滤器", ["GROUP"] = "编组", ["G"] = "编组",
        ["XREF"] = "外部参照", ["XR"] = "外部参照", ["IMAGE"] = "图像", ["IM"] = "图像",
        ["-PURGE"] = "清理(命令行)"
    };

    private void LoadCustomShortcuts()
    {
        _customShortcuts = ShortcutConfig.Load();
    }

    private void RefreshCustomList()
    {
        _lstCustom.Items.Clear();
        foreach (var s in _customShortcuts)
        {
            _lstCustom.Items.Add($"{s.Alias} → {s.Command}");
        }
    }

    private record CommandInfo(string Command, string Description, string Shortcut);
}
