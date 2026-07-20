using System.Drawing;
using System.Windows.Forms;

namespace QlPlugin;

internal static class QlCadTheme
{
    internal static readonly Color Panel = Color.FromArgb(38, 44, 56);
    internal static readonly Color ToolbarBg = Color.FromArgb(50, 59, 74);
    internal static readonly Color GridBg = Color.FromArgb(22, 25, 31);
    internal static readonly Color GridHeaderBg = Color.FromArgb(27, 31, 40);
    internal static readonly Color GridRow = Color.FromArgb(18, 20, 26);
    internal static readonly Color GridRowAlt = Color.FromArgb(22, 25, 31);
    internal static readonly Color GridRowHover = Color.FromArgb(34, 39, 49);
    internal static readonly Color GridLine = Color.FromArgb(37, 43, 54);
    internal static readonly Color InputBg = Color.FromArgb(38, 44, 56);
    internal static readonly Color ButtonBg = Color.FromArgb(45, 51, 59);
    internal static readonly Color ButtonBorder = Color.FromArgb(68, 76, 86);
    internal static readonly Color PrimaryButtonBg = Color.FromArgb(30, 119, 180);
    internal static readonly Color Accent = Color.FromArgb(88, 166, 255);
    internal static readonly Color Text = Color.FromArgb(230, 232, 234);
    internal static readonly Color Muted = Color.FromArgb(139, 148, 158);
    internal static readonly Color StatusLine = Color.FromArgb(48, 54, 61);

    internal static readonly Font BaseFont = new("Microsoft YaHei UI", 9F);
    internal static readonly Font HeaderFont = new("Microsoft YaHei UI", 10F, FontStyle.Bold);
    internal static readonly Font ButtonFont = new("Microsoft YaHei UI", 9.5F, FontStyle.Bold);

    internal static void ApplyWindow(Form form)
    {
        form.BackColor = Panel;
        form.ForeColor = Text;
        form.Font = BaseFont;
    }

    internal static void ApplyToolbar(Panel panel)
    {
        panel.BackColor = ToolbarBg;
        panel.ForeColor = Text;
        panel.Padding = new Padding(14, 10, 14, 10);
    }

    internal static void ApplyToolbar(TableLayoutPanel panel)
    {
        panel.BackColor = ToolbarBg;
        panel.ForeColor = Text;
        panel.Padding = new Padding(14, 0, 14, 0);
    }

    internal static void ApplyStatusPanel(Panel panel)
    {
        panel.BackColor = Panel;
        panel.ForeColor = Text;
        panel.Padding = new Padding(14, 8, 14, 8);
    }

    internal static void ApplyLabel(Label label, bool header = false, bool muted = false)
    {
        label.BackColor = Color.Transparent;
        label.ForeColor = muted ? Muted : Text;
        label.Font = header ? HeaderFont : BaseFont;
    }

    internal static void ApplyButton(Button button, bool primary = false)
    {
        button.BackColor = primary ? PrimaryButtonBg : ButtonBg;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? PrimaryButtonBg : ButtonBorder;
        button.FlatAppearance.MouseOverBackColor = primary
            ? Color.FromArgb(35, 134, 204)
            : Color.FromArgb(54, 61, 71);
        button.FlatAppearance.MouseDownBackColor = primary
            ? Color.FromArgb(22, 100, 168)
            : Color.FromArgb(30, 38, 48);
        button.Font = ButtonFont;
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
    }

    internal static void ApplyTextBox(TextBox textBox)
    {
        textBox.BackColor = InputBg;
        textBox.ForeColor = Text;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.Font = BaseFont;
    }

    internal static void ApplyComboBox(ComboBox comboBox)
    {
        comboBox.BackColor = InputBg;
        comboBox.ForeColor = Text;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.Font = BaseFont;
    }

    internal static void ApplyListBox(ListBox listBox)
    {
        listBox.BackColor = GridBg;
        listBox.ForeColor = Text;
        listBox.BorderStyle = BorderStyle.FixedSingle;
        listBox.Font = BaseFont;
    }

    internal static void ApplyGrid(DataGridView grid)
    {
        grid.BackgroundColor = GridBg;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.EnableHeadersVisualStyles = false;
        grid.GridColor = GridLine;
        grid.RowHeadersVisible = false;
        grid.AllowUserToResizeRows = false;
        grid.Font = BaseFont;

        grid.DefaultCellStyle.BackColor = GridRow;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(42, 52, 65);
        grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.DefaultCellStyle.Padding = new Padding(8, 4, 8, 4);

        grid.AlternatingRowsDefaultCellStyle.BackColor = GridRowAlt;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = Text;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(42, 52, 65);
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Text;

        grid.ColumnHeadersDefaultCellStyle.BackColor = GridHeaderBg;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = GridHeaderBg;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle.Font = HeaderFont;
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 8, 8, 8);

        grid.RowTemplate.Height = 34;
        grid.ColumnHeadersHeight = 42;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
    }
}
