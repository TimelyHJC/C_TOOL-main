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
        Text = fromSelection ? "图块列表 - 选中图块" : "图块列表 - 按大小排序";
        Size = new Size(750, 550);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(500, 350);

        _lblTitle = new Label
        {
            Text = fromSelection ? $"共 {blocks.Count} 个选中图块" : $"共 {blocks.Count} 个图块（按大小排序）",
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(8, 8, 0, 0),
            Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold)
        };

        _btnDelete = new Button
        {
            Text = "删除选中图块",
            Size = new Size(120, 32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Font = new Font("Microsoft YaHei UI", 9)
        };
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
            AllowUserToResizeRows = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            EnableHeadersVisualStyles = true,
            GridColor = Color.FromArgb(240, 240, 240),
            Font = new Font("Microsoft YaHei UI", 9)
        };

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

        var panel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
        panel.Controls.Add(_btnDelete);
        panel.Resize += (_, _) => _btnDelete.Location = new Point(panel.Width - 130, 6);
        _btnDelete.Location = new Point(panel.Width - 130, 6);

        Controls.Add(_grid);
        Controls.Add(panel);
        Controls.Add(_lblTitle);

        RefreshGrid();
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
        _lblTitle.Text = _fromSelection ? $"共 {_blocks.Count} 个选中图块" : $"共 {_blocks.Count} 个图块（按大小排序）";

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
}
