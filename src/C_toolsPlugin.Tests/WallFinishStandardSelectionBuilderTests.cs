using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class WallFinishStandardSelectionBuilderTests
{
    [TestMethod]
    public void CreateClosedLoopGuideSelection_CreatesClosedGuideForLoop()
    {
        using var guideSelection = WallFinishCommandService.StandardSelectionBuilder.CreateClosedLoopGuideSelection(
            ObjectId.Null,
            new[]
            {
                new Point2d(0.0, 0.0),
                new Point2d(100.0, 0.0),
                new Point2d(100.0, 100.0),
                new Point2d(0.0, 100.0)
            },
            elevation: 0.0);

        Assert.IsNotNull(guideSelection);
        Assert.IsFalse(guideSelection!.UseForDirectionResolution);
        Assert.IsTrue(guideSelection.GuidePolyline.Closed);
        Assert.AreEqual(4, guideSelection.GuidePolyline.NumberOfVertices);
        Assert.AreNotEqual(0, guideSelection.RecognizedInteriorSideSign);
    }

    [TestMethod]
    public void CanRecognizeFigureInterior_ReturnsTrueForWideAndTallGuides()
    {
        using var horizontal = new WallFinishCommandService.GuideChainSelection(
            ObjectId.Null,
            CreatePolyline(new Point2d(0.0, 0.0), new Point2d(100.0, 0.0)));
        using var vertical = new WallFinishCommandService.GuideChainSelection(
            ObjectId.Null,
            CreatePolyline(new Point2d(100.0, 0.0), new Point2d(100.0, 100.0)));

        var guideSelections = new[] { horizontal, vertical };

        var canRecognize = WallFinishCommandService.StandardSelectionBuilder.CanRecognizeFigureInterior(guideSelections);
        var center = WallFinishCommandService.StandardSelectionBuilder.ComputeRecognizedFigureCenter(guideSelections);

        Assert.IsTrue(canRecognize);
        Assert.AreEqual(50.0, center.X, 1e-9);
        Assert.AreEqual(50.0, center.Y, 1e-9);
        Assert.AreEqual(0.0, center.Z, 1e-9);
    }

    [TestMethod]
    public void ComputeRecognizedFigureCenter_UsesClosedConcaveGuideCentroid()
    {
        using var guide = new WallFinishCommandService.GuideChainSelection(
            ObjectId.Null,
            CreatePolyline(
                true,
                new Point2d(0.0, 0.0),
                new Point2d(100.0, 0.0),
                new Point2d(100.0, 40.0),
                new Point2d(40.0, 40.0),
                new Point2d(40.0, 100.0),
                new Point2d(0.0, 100.0)));

        var center = WallFinishCommandService.StandardSelectionBuilder.ComputeRecognizedFigureCenter(new[] { guide });

        Assert.AreEqual(38.75, center.X, 1e-9);
        Assert.AreEqual(38.75, center.Y, 1e-9);
        Assert.AreEqual(0.0, center.Z, 1e-9);
    }

    [TestMethod]
    public void CreatePolylineApproximationPoints_SplitsBulgeSegments()
    {
        using var polyline = new Polyline(2);
        polyline.Normal = Vector3d.ZAxis;
        polyline.AddVertexAt(0, new Point2d(0.0, 0.0), 1.0, 0.0, 0.0);
        polyline.AddVertexAt(1, new Point2d(100.0, 0.0), 0.0, 0.0, 0.0);

        var points = WallFinishCommandService.GeometryHelpers.CreatePolylineApproximationPoints(polyline);

        Assert.IsTrue(points.Count > 2);
        Assert.AreEqual(0.0, points[0].X, 1e-9);
        Assert.AreEqual(100.0, points[^1].X, 1e-9);
    }

    [TestMethod]
    public void CreateArcApproximationPoints_SplitsArc()
    {
        using var arc = new Arc(new Point3d(0.0, 0.0, 0.0), 100.0, 0.0, Math.PI / 2.0);

        var points = WallFinishCommandService.GeometryHelpers.CreateArcApproximationPoints(arc);

        Assert.IsTrue(points.Count > 2);
        Assert.AreEqual(100.0, points[0].X, 1e-9);
        Assert.AreEqual(0.0, points[0].Y, 1e-9);
        Assert.AreEqual(0.0, points[^1].X, 1e-9);
        Assert.AreEqual(100.0, points[^1].Y, 1e-9);
    }

    private static Polyline CreatePolyline(params Point2d[] points)
        => CreatePolyline(closed: false, points);

    private static Polyline CreatePolyline(bool closed, params Point2d[] points)
    {
        var polyline = new Polyline(points.Length);
        polyline.Normal = Vector3d.ZAxis;
        for (var i = 0; i < points.Length; i++)
            polyline.AddVertexAt(i, points[i], 0, 0, 0);

        polyline.Closed = closed;
        return polyline;
    }
}
