using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using C_toolsBbbPlugin;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsBbbPlugin.Tests;

[TestClass]
public sealed class BbbDeviceBlockCreateServiceTests
{
    [TestMethod]
    public void ResolveAnchorPointForExtents_UsesWorldCoordinatesWhenUcsIsIdentity()
    {
        var extents = new Extents3d(new Point3d(10, 20, 0), new Point3d(30, 60, 8));

        var topLeft = BbbDeviceBlockCreateService.ResolveAnchorPointForExtents(
            extents,
            BbbDeviceBlockAnchor.TopLeft,
            Matrix3d.Identity);
        var center = BbbDeviceBlockCreateService.ResolveAnchorPointForExtents(
            extents,
            BbbDeviceBlockAnchor.Center,
            Matrix3d.Identity);
        var bottomRight = BbbDeviceBlockCreateService.ResolveAnchorPointForExtents(
            extents,
            BbbDeviceBlockAnchor.BottomRight,
            Matrix3d.Identity);

        AssertPoint(topLeft, 10, 60, 0);
        AssertPoint(center, 20, 40, 0);
        AssertPoint(bottomRight, 30, 20, 0);
    }

    [TestMethod]
    public void ResolveAnchorPointForExtents_UsesCurrentUcsForTopAndBottom()
    {
        var extents = new Extents3d(new Point3d(0, 0, 0), new Point3d(10, 20, 0));
        var ucsToWorld = Matrix3d.Rotation(Math.PI / 2, Vector3d.ZAxis, Point3d.Origin);

        var topLeft = BbbDeviceBlockCreateService.ResolveAnchorPointForExtents(
            extents,
            BbbDeviceBlockAnchor.TopLeft,
            ucsToWorld);
        var bottomRight = BbbDeviceBlockCreateService.ResolveAnchorPointForExtents(
            extents,
            BbbDeviceBlockAnchor.BottomRight,
            ucsToWorld);

        AssertPoint(topLeft, 0, 0, 0);
        AssertPoint(bottomRight, 10, 20, 0);
    }

    private static void AssertPoint(Point3d actual, double x, double y, double z)
    {
        const double tolerance = 1e-8;
        Assert.AreEqual(x, actual.X, tolerance);
        Assert.AreEqual(y, actual.Y, tolerance);
        Assert.AreEqual(z, actual.Z, tolerance);
    }
}
