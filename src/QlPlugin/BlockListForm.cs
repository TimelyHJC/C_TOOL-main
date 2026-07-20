using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QlPlugin;

/// <summary>
/// 图块列表显示窗口
/// </summary>
public class BlockListForm : Form
{
    private readonly DataGridView _grid;
    private readonly Label _lblTitle;
    private readonly Button _btnDelete;
    private List<BlockInfo> _blocks;
    private readonly bool _fromSelection;
    private readonly HashSet<string>? _selectedBlockNames;

    /// <summary>
    /// 双击时待插入的图块名称，关闭窗体后由命令读取
    /// </summary>
    public string? BlockNameToInsert { get; private set; }

    public BlockListForm(List<BlockInfo> blocks, bool fromSelection = false)
    {
        _blocks = blocks;
        _fromSelection = fromSelection;
        _selectedBlockNames = fromSelection ? blocks.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        Text = fromSelection ? "C_TOOL 图块列表 - 选中图块" : "C_TOOL 图块列表 - 按大小排序";
        Size = new Size(750, 550);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(500, 350);
        QlCadTheme.ApplyWindow(this);

        _lblTitle = new Label
        {
            Text = GetHeaderSummaryText(blocks.Count),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0)
        };
        QlCadTheme.ApplyLabel(_lblTitle, header: true);

        _btnDelete = new Button
        {
            Text = "删除选中图块",
            Size = new Size(128, 34),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        QlCadTheme.ApplyButton(_btnDelete);
        _btnDelete.Click += BtnDelete_Click;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            RowHeadersVisible = false,
            AllowUserToResizeRows = false
        };
        QlCadTheme.ApplyGrid(_grid);

        // 列定义
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colRank",
            HeaderText = "序号",
            Width = 50,
            DataPropertyName = "Rank"
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colName",
            HeaderText = "图块名称",
            Width = 200,
            DataPropertyName = "Name",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colEntityCount",
            HeaderText = "实体数量",
            Width = 90,
            DataPropertyName = "EntityCount"
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colRefCount",
            HeaderText = "引用数量",
            Width = 90,
            DataPropertyName = "ReferenceCount"
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colSize",
            HeaderText = "估算大小(MB)",
            Width = 110,
            DataPropertyName = "EstimatedSizeMb"
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colAnonymous",
            HeaderText = "匿名块",
            Width = 80,
            DataPropertyName = "IsAnonymous"
        });

        _grid.CellDoubleClick += Grid_CellDoubleClick;
        _grid.CellMouseEnter += (_, e) =>
        {
            if (e.RowIndex >= 0)
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = QlCadTheme.GridRowHover;
        };
        _grid.CellMouseLeave += (_, e) =>
        {
            if (e.RowIndex >= 0)
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = QlCadTheme.GridRow;
        };

        var header = CreatePluginHeader();
        var statusPanel = new Panel
        {
            Dock = DockStyle.Fill
        };
        QlCadTheme.ApplyStatusPanel(statusPanel);
        statusPanel.Controls.Add(_btnDelete);
        statusPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(QlCadTheme.StatusLine);
            e.Graphics.DrawLine(pen, 0, 0, statusPanel.Width, 0);
        };
        statusPanel.Resize += (_, _) => PositionDeleteButton(statusPanel);
        PositionDeleteButton(statusPanel);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = QlCadTheme.GridBg,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(statusPanel, 0, 2);

        Controls.Add(layout);

        RefreshGrid();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CadWinFormsTitleBarHelper.TryApplyDarkTitleBar(this, QlCadTheme.Panel, QlCadTheme.Text);
    }

    private Control CreatePluginHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        QlCadTheme.ApplyToolbar(header);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var pluginTitle = new Label
        {
            Text = "C_TOOL 图块列表",
            AutoSize = true,
            AutoEllipsis = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 18, 0)
        };
        QlCadTheme.ApplyLabel(pluginTitle, header: true);

        header.Controls.Add(pluginTitle, 0, 0);
        header.Controls.Add(_lblTitle, 1, 0);
        return header;
    }

    private void PositionDeleteButton(Panel panel)
    {
        var x = Math.Max(12, panel.ClientSize.Width - _btnDelete.Width - 14);
        _btnDelete.Location = new Point(x, 11);
    }

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _blocks.Count) return;

        var block = _blocks[e.RowIndex];
        if (block.Name.StartsWith("*"))
        {
            MessageBox.Show("匿名块无法直接插入。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        BlockNameToInsert = block.Name;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void BtnDelete_Click(object? sender, EventArgs e)
    {
        var selected = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Where(r => r.Index >= 0 && r.Index < _blocks.Count)
            .Select(r => _blocks[r.Index])
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择要删除的图块。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var names = selected.Select(b => b.Name).ToList();
        var systemBlocks = names.Where(n => n.StartsWith("*") || n.Equals("MODEL_SPACE", StringComparison.OrdinalIgnoreCase) || n.Equals("PAPER_SPACE", StringComparison.OrdinalIgnoreCase)).ToList();
        if (systemBlocks.Count > 0)
        {
            MessageBox.Show($"无法删除系统块: {string.Join(", ", systemBlocks)}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var toDelete = names.Except(systemBlocks).ToList();
        if (toDelete.Count == 0) return;

        var msg = toDelete.Count == 1
            ? $"确定要删除图块 \"{toDelete[0]}\" 及其所有引用吗？"
            : $"确定要删除选中的 {toDelete.Count} 个图块及其所有引用吗？\n\n{string.Join("\n", toDelete)}";
        if (MessageBox.Show(msg, "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var failed = new List<string>();
        foreach (var name in toDelete)
        {
            var (success, message) = BlockDeleter.DeleteBlock(name);
            if (!success) failed.Add($"{name}: {message}");
        }

        if (failed.Count > 0)
            MessageBox.Show("部分删除失败：\n" + string.Join("\n", failed), "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        RefreshGrid();
    }

    private void RefreshGrid()
    {
        _blocks = _fromSelection && _selectedBlockNames != null
            ? BlockInfoCollector.CollectBlocks().Where(b => _selectedBlockNames.Contains(b.Name)).ToList()
            : BlockInfoCollector.CollectBlocks();
        _lblTitle.Text = GetHeaderSummaryText(_blocks.Count);

        var displayItems = _blocks.Select((b, i) => new
        {
            Rank = i + 1,
            b.Name,
            b.EntityCount,
            b.ReferenceCount,
            EstimatedSizeMb = b.EstimatedSizeMb.ToString("N3"),
            IsAnonymous = b.IsAnonymous ? "是" : "否"
        }).ToList();

        _grid.DataSource = null;
        _grid.DataSource = displayItems;
    }

    private string GetHeaderSummaryText(int count) =>
        _fromSelection ? $"共 {count} 个选中图块" : $"共 {count} 个图块（按大小排序）";
}

internal static class CadWinFormsTitleBarHelper
{
    private const int DwAttributeUseImmersiveDarkMode = 20;
    private const int DwAttributeUseImmersiveDarkModeLegacy = 19;
    private const int DwAttributeBorderColor = 34;
    private const int DwAttributeCaptionColor = 35;
    private const int DwAttributeTextColor = 36;

    internal static void TryApplyDarkTitleBar(Form form, Color background, Color text)
    {
        try
        {
            if (form.Handle == IntPtr.Zero)
                return;

            SetDwmIntAttribute(form.Handle, DwAttributeUseImmersiveDarkMode, 1);
            SetDwmIntAttribute(form.Handle, DwAttributeUseImmersiveDarkModeLegacy, 1);
            SetDwmIntAttribute(form.Handle, DwAttributeBorderColor, ToColorRef(background));
            SetDwmIntAttribute(form.Handle, DwAttributeCaptionColor, ToColorRef(background));
            SetDwmIntAttribute(form.Handle, DwAttributeTextColor, ToColorRef(text));
        }
        catch
        {
            // 标题栏着色是增强项；当前系统不支持时保持默认标题栏。
        }
    }

    private static void SetDwmIntAttribute(IntPtr hwnd, int attribute, int value)
    {
        _ = DwmSetWindowAttribute(hwnd, attribute, ref value, Marshal.SizeOf(typeof(int)));
    }

    private static int ToColorRef(Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
