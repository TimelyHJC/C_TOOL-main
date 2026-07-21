using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class RectangleMergeGeometryTests
{
    [TestMethod]
    public void TryBuildMergedBoundaryLoops_MergesAdjacentRectanglesIntoOneLoop()
    {
        var rectangles = new[]
        {
            BuildRectangle(0.0, 0.0, 100.0, 100.0),
            BuildRectangle(100.0, 0.0, 200.0, 100.0)
        };

        var success = RectangleMergeGeometry.TryBuildMergedBoundaryLoops(rectangles, out var loops, out var error);

        Assert.IsTrue(success, error);
        Assert.AreEqual(1, loops.Count);
        Assert.AreEqual(4, loops[0].Count);
        AssertLoopBounds(loops[0], 0.0, 0.0, 200.0, 100.0);
    }

    [TestMethod]
    public void TryBuildMergedBoundaryLoops_KeepsConcaveOutline()
    {
        var rectangles = new[]
        {
            BuildRectangle(0.0, 0.0, 100.0, 100.0),
            BuildRectangle(100.0, 0.0, 200.0, 100.0),
            BuildRectangle(0.0, 100.0, 100.0, 200.0)
        };

        var success = RectangleMergeGeometry.TryBuildMergedBoundaryLoops(rectangles, out var loops, out var error);

        Assert.IsTrue(success, error);
        Assert.AreEqual(1, loops.Count);
        Assert.AreEqual(6, loops[0].Count);
        AssertLoopBounds(loops[0], 0.0, 0.0, 200.0, 200.0);
    }

    [TestMethod]
    public void TryBuildMergedBoundaryLoops_SupportsCommonRotation()
    {
        var origin = new Point3d(100.0, 200.0, 0.0);
        var axisU = new Vector3d(Math.Cos(Math.PI / 4.0), Math.Sin(Math.PI / 4.0), 0.0);
        var axisV = new Vector3d(-Math.Sin(Math.PI / 4.0), Math.Cos(Math.PI / 4.0), 0.0);

        var rectangles = new[]
        {
            BuildRotatedRectangle(origin, axisU, axisV, 0.0, 0.0, 100.0, 100.0),
            BuildRotatedRectangle(origin, axisU, axisV, 100.0, 0.0, 200.0, 100.0)
        };

        var success = RectangleMergeGeometry.TryBuildMergedBoundaryLoops(rectangles, out var loops, out var error);

        Assert.IsTrue(success, error);
        Assert.AreEqual(1, loops.Count);
        Assert.AreEqual(4, loops[0].Count);
    }

    private static RectangleMergeGeometry.RectangleFootprint BuildRectangle(
        double left,
        double bottom,
        double right,
        double top)
    {
        return RectangleMergeGeometry.CreateFootprint(
            new Point3d(left, bottom, 0.0),
            new Point3d(right, bottom, 0.0),
            new Point3d(right, top, 0.0),
            new Point3d(left, top, 0.0));
    }

    private static RectangleMergeGeometry.RectangleFootprint BuildRotatedRectangle(
        Point3d origin,
        Vector3d axisU,
        Vector3d axisV,
        double left,
        double bottom,
        double right,
        double top)
    {
        return RectangleMergeGeometry.CreateFootprint(
            origin + (axisU * left) + (axisV * bottom),
            origin + (axisU * right) + (axisV * bottom),
            origin + (axisU * right) + (axisV * top),
            origin + (axisU * left) + (axisV * top));
    }

    private static void AssertLoopBounds(IReadOnlyList<Point3d> loop, double minX, double minY, double maxX, double maxY)
    {
        Assert.AreEqual(minX, loop.Min(point => point.X), 1e-9);
        Assert.AreEqual(minY, loop.Min(point => point.Y), 1e-9);
        Assert.AreEqual(maxX, loop.Max(point => point.X), 1e-9);
        Assert.AreEqual(maxY, loop.Max(point => point.Y), 1e-9);
    }
}
