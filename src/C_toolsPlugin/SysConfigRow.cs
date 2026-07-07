using System.ComponentModel;
using System.Windows.Media;

namespace C_toolsPlugin;

/// <summary>系统配置表一行：变量名、可编辑值、说明（默认由内置列表提供）。</summary>
public sealed class SysConfigRow : INotifyPropertyChanged
{
    public SysConfigRow(string varName, string initialValue, string comment, string? argRegistryKey = null,
        bool argIsDword = false)
    {
        VarName = varName;
        _value = initialValue;
        Comment = comment;
        ArgRegistryKey = argRegistryKey;
        ArgIsDword = argIsDword;
        TabKey = SysConfigTabClassifier.GetTabKey(varName);
    }

    public string VarName { get; }

    /// <summary>所属配置标签（DimStyle / PlotLayout / Paths，与系统配置窗标签一致）。</summary>
    public string TabKey { get; }

    public string Comment { get; }

    /// <summary>非 null 时表示 .arg 注册表键（历史字段；当前不读写 .arg）。</summary>
    public string? ArgRegistryKey { get; }

    public bool ArgIsDword { get; }

    /// <summary>DSP_* 行在「值」列旁显示 RGB 色块。</summary>
    public bool ShowValueColorSwatch => CadDisplayPreferenceColors.IsDisplayColorKey(VarName);

    /// <summary>由当前 <see cref="Value"/> 解析的色块画刷；非 DSP_* 为透明。</summary>
    public Brush ValueColorBrush
    {
        get
        {
            if (!CadDisplayPreferenceColors.IsDisplayColorKey(VarName))
                return Brushes.Transparent;
            if (CadDisplayPreferenceColors.TryGetSwatchColor(VarName, _value, out var c))
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }

            var fb = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            fb.Freeze();
            return fb;
        }
    }

    private string _value;

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
                return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueColorBrush)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
