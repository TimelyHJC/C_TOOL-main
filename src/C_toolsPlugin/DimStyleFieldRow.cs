using System.ComponentModel;

namespace C_toolsPlugin;

/// <summary>标注样式批量面板中「列表」一行的视图模型（属性 / 值 / 说明）。</summary>
public sealed class DimStyleFieldRow : INotifyPropertyChanged
{
    public DimStyleFieldRow(string rowKey, string label, string hint)
    {
        RowKey = rowKey;
        Label = label;
        Hint = hint;
    }

    public string RowKey { get; }

    public string Label { get; }

    public string Hint { get; }

    private string _textValue = "";

    public string TextValue
    {
        get => _textValue;
        set
        {
            if (_textValue == value)
                return;
            _textValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextValue)));
        }
    }

    private bool _boolValue;

    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (_boolValue == value)
                return;
            _boolValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BoolValue)));
        }
    }

    private string _arrowBlockName = "";

    /// <summary>对应 DIMBLK：空字符串表示默认闭合箭头。</summary>
    public string ArrowBlockName
    {
        get => _arrowBlockName;
        set
        {
            if (_arrowBlockName == value)
                return;
            _arrowBlockName = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArrowBlockName)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
