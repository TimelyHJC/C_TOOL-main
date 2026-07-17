using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class RectangleCenterAlignCommandServiceTests
{
    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_FindsGridCellAroundTarget()
    {
        var targetExtents = new Extents3d(
            new Point3d(120.0, 40.0, 0.0),
            new Point3d(140.0, 60.0, 0.0));
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(0.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(100.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(200.0, 0.0, 200.0)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            targetExtents,
            segments,
            out var frameExtents);

        Assert.IsTrue(success);
        AssertExtents(frameExtents, minX: 100.0, minY: 0.0, maxX: 200.0, maxY: 100.0);
    }

    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_MergesSplitBoundarySegments()
    {
        var targetExtents = new Extents3d(
            new Point3d(120.0, 45.0, 0.0),
            new Point3d(140.0, 55.0, 0.0));
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, 0.0, 49.99995),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, 50.00005, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, 0.0, 49.99995),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, 50.00005, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, 100.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(100.0, 100.0, 200.0)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            targetExtents,
            segments,
            out var frameExtents);

        Assert.IsTrue(success);
        AssertExtents(frameExtents, minX: 100.0, minY: 0.0, maxX: 200.0, maxY: 100.0);
    }

    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_IgnoresShortGuideLineNearTarget()
    {
        var targetExtents = new Extents3d(
            new Point3d(120.0, 45.0, 0.0),
            new Point3d(140.0, 55.0, 0.0));
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, 0.0, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(110.0, 45.0, 55.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, 0.0, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, 100.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(100.0, 100.0, 200.0)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            targetExtents,
            segments,
            out var frameExtents);

        Assert.IsTrue(success);
        AssertExtents(frameExtents, minX: 100.0, minY: 0.0, maxX: 200.0, maxY: 100.0);
    }

    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_ReturnsFalseWhenBoundaryIsMissing()
    {
        var targetExtents = new Extents3d(
            new Point3d(120.0, 40.0, 0.0),
            new Point3d(140.0, 60.0, 0.0));
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, 0.0, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, 0.0, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, 100.0, 200.0)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            targetExtents,
            segments,
            out _);

        Assert.IsFalse(success);
    }

    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_ByPoint_FindsCellFromInteriorPick()
    {
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(0.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(100.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(200.0, 0.0, 200.0)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            new Point3d(150.0, 50.0, 0.0),
            segments,
            out var frameExtents);

        Assert.IsTrue(success);
        AssertExtents(frameExtents, minX: 100.0, minY: 0.0, maxX: 200.0, maxY: 100.0);
    }

    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_ByPoint_OnSharedBoundaryChoosesAdjacentCell()
    {
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(0.0, 0.0, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, 0.0, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, 0.0, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, 0.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(100.0, 0.0, 200.0)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            new Point3d(100.0, 50.0, 0.0),
            segments,
            out var frameExtents);

        Assert.IsTrue(success);
        AssertExtents(frameExtents, minX: 100.0, minY: 0.0, maxX: 200.0, maxY: 100.0);
    }

    [TestMethod]
    public void TryCreateAxisAlignedDirectionalSegment_CreatesInfiniteVerticalBoundaryForXline()
    {
        var success = RectangleCenterAlignCommandService.TryCreateAxisAlignedDirectionalSegment(
            new Point3d(100.0, 50.0, 0.0),
            new Vector3d(0.0, 1.0, 0.0),
            isRay: false,
            out var segment);

        Assert.IsTrue(success);
        Assert.IsTrue(segment.IsVertical);
        Assert.AreEqual(100.0, segment.Coordinate, 1e-9);
        Assert.IsTrue(double.IsNegativeInfinity(segment.RangeStart));
        Assert.IsTrue(double.IsPositiveInfinity(segment.RangeEnd));
    }

    [TestMethod]
    public void TryCreateAxisAlignedDirectionalSegment_CreatesSemiInfiniteBoundaryForRay()
    {
        var success = RectangleCenterAlignCommandService.TryCreateAxisAlignedDirectionalSegment(
            new Point3d(0.0, 100.0, 0.0),
            new Vector3d(1.0, 0.0, 0.0),
            isRay: true,
            out var segment);

        Assert.IsTrue(success);
        Assert.IsFalse(segment.IsVertical);
        Assert.AreEqual(100.0, segment.Coordinate, 1e-9);
        Assert.AreEqual(0.0, segment.RangeStart, 1e-9);
        Assert.IsTrue(double.IsPositiveInfinity(segment.RangeEnd));
    }

    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_FindsCellFromInfiniteGridLines()
    {
        var targetExtents = new Extents3d(
            new Point3d(120.0, 40.0, 0.0),
            new Point3d(140.0, 60.0, 0.0));
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, double.NegativeInfinity, double.PositiveInfinity),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, double.NegativeInfinity, double.PositiveInfinity),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, double.NegativeInfinity, double.PositiveInfinity),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(100.0, double.NegativeInfinity, double.PositiveInfinity)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            targetExtents,
            segments,
            out var frameExtents);

        Assert.IsTrue(success);
        AssertExtents(frameExtents, minX: 100.0, minY: 0.0, maxX: 200.0, maxY: 100.0);
    }

    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_FindsRangeFromExtendedBoundaryLines()
    {
        var targetExtents = new Extents3d(
            new Point3d(120.0, 40.0, 0.0),
            new Point3d(140.0, 60.0, 0.0));
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, 40.0, 60.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, 40.0, 60.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, 120.0, 140.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(100.0, 120.0, 140.0)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            targetExtents,
            segments,
            out var frameExtents);

        Assert.IsTrue(success);
        AssertExtents(frameExtents, minX: 100.0, minY: 0.0, maxX: 200.0, maxY: 100.0);
    }

    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_PrefersSmallTextCellOverOuterFrame()
    {
        var targetExtents = new Extents3d(
            new Point3d(120.0, 40.0, 0.0),
            new Point3d(140.0, 60.0, 0.0));
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(0.0, 0.0, 300.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(300.0, 0.0, 300.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, 0.0, 300.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(300.0, 0.0, 300.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, 0.0, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, 0.0, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, 100.0, 200.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(100.0, 100.0, 200.0)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            targetExtents,
            segments,
            out var frameExtents);

        Assert.IsTrue(success);
        AssertExtents(frameExtents, minX: 100.0, minY: 0.0, maxX: 200.0, maxY: 100.0);
    }

    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_FindsTextCellInTableRowLike2350()
    {
        var targetExtents = new Extents3d(
            new Point3d(1088.0, 660.0, 0.0),
            new Point3d(1288.0, 724.0, 0.0));
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(0.0, 260.0, 955.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(668.0, 608.0, 780.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(1717.0, 260.0, 955.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(2510.0, 260.0, 955.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(260.0, 0.0, 2510.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(435.0, 0.0, 2510.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(608.0, 0.0, 2510.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(780.0, 0.0, 2510.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(955.0, 0.0, 2510.0)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            targetExtents,
            segments,
            out var frameExtents);

        Assert.IsTrue(success);
        AssertExtents(frameExtents, minX: 668.0, minY: 608.0, maxX: 1717.0, maxY: 780.0);
    }

    [TestMethod]
    public void TryInferAxisAlignedRectangleBounds_IgnoresCornerFragmentsAwayFromText()
    {
        var targetExtents = new Extents3d(
            new Point3d(120.0, 40.0, 0.0),
            new Point3d(140.0, 60.0, 0.0));
        var segments = new[]
        {
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(100.0, 0.0, 10.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(200.0, 90.0, 100.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(0.0, 100.0, 110.0),
            RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(100.0, 190.0, 200.0)
        };

        var success = RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
            targetExtents,
            segments,
            out _);

        Assert.IsFalse(success);
    }

    [TestMethod]
    public void PlanarPolygonGeometry_TryComputeCentroid_UsesConcaveBoundaryArea()
    {
        var vertices = new[]
        {
            new Point2d(0.0, 0.0),
            new Point2d(100.0, 0.0),
            new Point2d(100.0, 40.0),
            new Point2d(40.0, 40.0),
            new Point2d(40.0, 100.0),
            new Point2d(0.0, 100.0)
        };

        var success = PlanarPolygonGeometry.TryComputeCentroid(
            vertices,
            elevation: 0.0,
            out var centroid,
            out var area);

        Assert.IsTrue(success);
        Assert.AreEqual(6400.0, area, 1e-9);
        Assert.AreEqual(38.75, centroid.X, 1e-9);
        Assert.AreEqual(38.75, centroid.Y, 1e-9);
        Assert.IsTrue(PlanarPolygonGeometry.ContainsPoint(vertices, new Point2d(20.0, 80.0)));
        Assert.IsFalse(PlanarPolygonGeometry.ContainsPoint(vertices, new Point2d(80.0, 80.0)));
    }

    private static void AssertExtents(Extents3d extents, double minX, double minY, double maxX, double maxY)
    {
        Assert.AreEqual(minX, extents.MinPoint.X, 1e-9);
        Assert.AreEqual(minY, extents.MinPoint.Y, 1e-9);
        Assert.AreEqual(maxX, extents.MaxPoint.X, 1e-9);
        Assert.AreEqual(maxY, extents.MaxPoint.Y, 1e-9);
    }
}
