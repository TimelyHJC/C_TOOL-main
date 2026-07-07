using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    internal static partial class OutlineBuilder
    {
        internal static bool TryBuildOutline(
            Polyline guidePolyline,
            int sideSign,
            double offsetDistance,
            out Polyline? outline,
            out string error)
        {
            outline = null;
            error = "";

            if (guidePolyline.NumberOfVertices < 2)
            {
                error = "墙面线至少需要两个点。";
                return false;
            }

            if (double.IsNaN(offsetDistance) || double.IsInfinity(offsetDistance) || offsetDistance <= PointTolerance)
            {
                error = "偏移量必须大于 0。";
                return false;
            }

            // AutoCAD offset's positive/negative convention is opposite to our side-sign resolution.
            if (!TryGetOffsetPolyline(guidePolyline, -sideSign * offsetDistance, out var offsetPolyline, out error) ||
                offsetPolyline == null)
            {
                return false;
            }

            if (guidePolyline.Closed && offsetPolyline.Closed)
            {
                outline = offsetPolyline;
                return true;
            }

            using (offsetPolyline)
            {
                outline = StitchOutline(guidePolyline, offsetPolyline, guidePolyline.Elevation);
                if (outline.NumberOfVertices < 3)
                {
                    outline.Dispose();
                    outline = null;
                    error = "完成面轮廓点数不足，无法生成。";
                    return false;
                }

                return true;
            }
        }

        private static bool TryGetOffsetPolyline(
            Polyline guidePolyline,
            double signedDistance,
            out Polyline? offsetPolyline,
            out string error)
        {
            offsetPolyline = null;
            error = "";

            DBObjectCollection? offsetCurves = null;
            try
            {
                offsetCurves = guidePolyline.GetOffsetCurves(signedDistance);
                foreach (DBObject curve in offsetCurves)
                {
                    if (curve is not Polyline polyline)
                        continue;

                    offsetPolyline = GeometryHelpers.ClonePolyline(polyline);
                    return true;
                }

                error = "偏移失败，未得到有效的多段线。";
                return false;
            }
            catch (InvalidOperationException ex)
            {
                error = "偏移失败：" + ex.Message;
                C_toolsDiagnostics.LogNonFatal($"{CommandName} 偏移墙面线失败（无效操作）", ex);
                return false;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                error = "偏移失败：" + ex.Message;
                C_toolsDiagnostics.LogNonFatal($"{CommandName} 偏移墙面线失败（CAD）", ex);
                return false;
            }
            finally
            {
                if (offsetCurves != null)
                {
                    foreach (DBObject curve in offsetCurves)
                        curve.Dispose();
                }
            }
        }

        private static Polyline StitchOutline(Polyline sourceBoundary, Polyline offsetBoundary, double elevation)
        {
            var vertices = BuildOpenOutlineVertices(sourceBoundary, offsetBoundary);

            if (vertices.Count > 1 && GeometryHelpers.ArePointsEqual(vertices[0], vertices[^1]))
                vertices.RemoveAt(vertices.Count - 1);

            var outline = new Polyline(vertices.Count);
            outline.Normal = Vector3d.ZAxis;
            outline.Elevation = elevation;

            for (var i = 0; i < vertices.Count; i++)
                outline.AddVertexAt(i, vertices[i], 0, 0, 0);

            outline.Closed = true;
            return outline;
        }

        private static List<Point2d> BuildOpenOutlineVertices(Polyline sourceBoundary, Polyline offsetBoundary)
        {
            var vertices = new List<Point2d>();
            AppendBoundaryVertices(vertices, sourceBoundary, reverse: false);
            AppendBoundaryVertices(vertices, offsetBoundary, reverse: true);
            return vertices;
        }
        private static void AppendBoundaryVertices(List<Point2d> target, Polyline polyline, bool reverse)
        {
            if (!reverse)
            {
                for (var i = 0; i < polyline.NumberOfVertices; i++)
                    GeometryHelpers.AddVertexIfDistinct(target, polyline.GetPoint2dAt(i));
                return;
            }

            for (var i = polyline.NumberOfVertices - 1; i >= 0; i--)
                GeometryHelpers.AddVertexIfDistinct(target, polyline.GetPoint2dAt(i));
        }
    }
}
