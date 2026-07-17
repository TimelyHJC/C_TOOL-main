using Autodesk.AutoCAD.Geometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class FoldBreakCommandServiceTests
{
    [TestMethod]
    public void BuildFoldPoints_DefaultRatioPlacesTurnPointNearUpperLeft()
    {
        var settings = FoldBreakSettingsStore.Normalize(new FoldBreakSettingsDto
        {
            HorizontalLeftPart = 1.0,
            HorizontalRightPart = 7.0,
            VerticalTopPart = 1.0,
            VerticalBottomPart = 7.0,
            ColorIndex = 8
        });

        var points = FoldBreakCommandService.BuildFoldPoints(
            left: 0.0,
            bottom: 0.0,
            right: 80.0,
            top: 160.0,
            elevation: 0.0,
            settings);

        AssertPoint(points[0], 0.0, 0.0);
        AssertPoint(points[1], 10.0, 140.0);
        AssertPoint(points[2], 80.0, 160.0);
    }

    private static void AssertPoint(Point3d point, double x, double y)
    {
        Assert.AreEqual(x, point.X, 1e-9);
        Assert.AreEqual(y, point.Y, 1e-9);
        Assert.AreEqual(0.0, point.Z, 1e-9);
    }
}
