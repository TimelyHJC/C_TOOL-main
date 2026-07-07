using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsQqqPlugin;

internal sealed class QqqPlotFrameInfo : INotifyPropertyChanged
{
    private int _addedOrder;
    private bool _isSelected = true;
    private string _status = "待打印";
    private string _outputFile = "";
    private string _paperSize = "自动匹配";
    private string _plotScale = "自定义";

    public int AddedOrder
    {
        get => _addedOrder;
        set
        {
            if (_addedOrder == value)
                return;
            _addedOrder = value;
            OnPropertyChanged();
        }
    }
    public string Key { get; set; } = "";
    public string LayoutName { get; set; } = "";
    public string SpaceName { get; set; } = "";
    public string FrameType { get; set; } = "";
    public string FrameName { get; set; } = "";
    public string LayerName { get; set; } = "";
    public string BlockName { get; set; } = "";
    public string RecognitionSource { get; set; } = "";
    public string HandleText { get; set; } = "";
    public double Width { get; set; }
    public double Height { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public Extents3d WcsExtents { get; set; }

    public string SizeText => $"{Width:0.##}×{Height:0.##}";
    public double Area => Width * Height;

    public string PaperSizeBadge
    {
        get
        {
            var s = (_paperSize ?? "").Trim();
            if (s.StartsWith("ISO ", StringComparison.OrdinalIgnoreCase))
                return s.Substring(4).Trim();
            return s.Length == 0 || string.Equals(s, "自动匹配", StringComparison.Ordinal) ? "—" : s;
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string PaperSize
    {
        get => _paperSize;
        set
        {
            if (string.Equals(_paperSize, value, StringComparison.Ordinal))
                return;
            _paperSize = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PaperSizeBadge));
        }
    }

    public string PlotScale
    {
        get => _plotScale;
        set
        {
            if (string.Equals(_plotScale, value, StringComparison.Ordinal))
                return;
            _plotScale = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (string.Equals(_status, value, StringComparison.Ordinal))
                return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public string OutputFile
    {
        get => _outputFile;
        set
        {
            if (string.Equals(_outputFile, value, StringComparison.Ordinal))
                return;
            _outputFile = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
