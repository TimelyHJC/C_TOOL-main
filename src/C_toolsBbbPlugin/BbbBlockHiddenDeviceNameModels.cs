using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsBbbPlugin;

public sealed class BbbBlockHiddenDeviceNameTarget
{
    internal ObjectId BlockReferenceId { get; set; }
    public string HandleText { get; set; } = "";
    public string BlockName { get; set; } = "";
    public string StateDisplayText { get; set; } = "";
    public string ExistingDeviceNamesText { get; set; } = "";
}

public sealed class BbbBlockHiddenDeviceNameOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class BbbBlockHiddenDeviceNameCaptureResult
{
    internal List<BbbBlockHiddenDeviceNameTarget> Targets { get; } = new();
    internal List<string> PreselectedDeviceNames { get; } = new();
    internal List<string> UnrecognizedDynamicStateMessages { get; } = new();
    internal int OrdinaryBlockCount { get; set; }
    internal int SkippedInvalidCount { get; set; }
}
