using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class WallFinishOutlineBuilderTests
{
    [TestMethod]
    public void TryBuildOutline_RejectsGuideWithTooFewVertices()
    {
        using var guide = CreatePolyline(new Point2d(0.0, 0.0));

        var success = WallFinishCommandService.OutlineBuilder.TryBuildOutline(
            guide,
            sideSign: 1,
            offsetDistance: 120.0,
            out var outline,
            out var error);

        Assert.IsFalse(success);
        Assert.IsNull(outline);
        StringAssert.Contains(error, "墙面线至少需要两个点");
    }

    [TestMethod]
    public void TryBuildOutline_RejectsNonPositiveOffset()
    {
        using var guide = CreatePolyline(
            new Point2d(0.0, 0.0),
            new Point2d(1000.0, 0.0));

        var success = WallFinishCommandService.OutlineBuilder.TryBuildOutline(
            guide,
            sideSign: 1,
            offsetDistance: 0.0,
            out var outline,
            out var error);

        Assert.IsFalse(success);
        Assert.IsNull(outline);
        StringAssert.Contains(error, "偏移量必须大于 0");
    }

    private static Polyline CreatePolyline(params Point2d[] points)
    {
        var polyline = new Polyline(points.Length);
        polyline.Normal = Vector3d.ZAxis;
        for (var i = 0; i < points.Length; i++)
            polyline.AddVertexAt(i, points[i], 0, 0, 0);

        return polyline;
    }
}
