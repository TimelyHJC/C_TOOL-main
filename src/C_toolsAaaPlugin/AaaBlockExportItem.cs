using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsAaaPlugin;

internal sealed class AaaBlockExportItem
{
    internal ObjectId BlockReferenceId { get; set; }
    internal string BlockHandle { get; set; } = "";
    internal string DisplayName { get; set; } = "";
    internal Point3d BasePoint { get; set; }
    internal double Rotation { get; set; }
    internal double ScaleX { get; set; } = 1d;
    internal double ScaleY { get; set; } = 1d;
    internal double ScaleZ { get; set; } = 1d;
    internal string LayerName { get; set; } = "";
}
