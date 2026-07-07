using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Gi = Autodesk.AutoCAD.GraphicsInterface;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    internal static class GeometryHelpers
    {
        private const double ArcApproximationMaxAngle = Math.PI / 18.0;
        private const int ArcApproximationMaxSegments = 96;

        internal static void AddVertexIfDistinct(List<Point2d> target, Point2d point)
        {
            if (target.Count > 0 && ArePointsEqual(target[^1], point))
                return;

            target.Add(point);
        }

        internal static bool ArePointsEqual(Point2d a, Point2d b) =>
            a.GetDistanceTo(b) <= PointTolerance;

        internal static double DotProduct(Point2d origin, Point2d point, Vector2d axis) =>
            ((point.X - origin.X) * axis.X) + ((point.Y - origin.Y) * axis.Y);

        internal static double GetPointProjection(Point2d point, Vector2d axis) =>
            (point.X * axis.X) + (point.Y * axis.Y);

        internal static void GetQuickSegmentProjectionRange(
            QuickSegment segment,
            Vector2d axis,
            out double minProjection,
            out double maxProjection)
        {
            var startProjection = GetPointProjection(segment.StartPoint, axis);
            var endProjection = GetPointProjection(segment.EndPoint, axis);
            if (startProjection <= endProjection)
            {
                minProjection = startProjection;
                maxProjection = endProjection;
                return;
            }

            minProjection = endProjection;
            maxProjection = startProjection;
        }

        internal static Point2d AveragePoint(Point2d first, Point2d second) =>
            new((first.X + second.X) * 0.5, (first.Y + second.Y) * 0.5);

        internal static double ComputeRepresentativeDistance(IReadOnlyList<double> values, double fallback)
        {
            if (values.Count == 0)
                return fallback;

            var ordered = values
                .Where(x => !double.IsNaN(x) && !double.IsInfinity(x) && x > PointTolerance)
                .OrderBy(x => x)
                .ToList();
            if (ordered.Count == 0)
                return fallback;

            var middleIndex = ordered.Count / 2;
            if ((ordered.Count % 2) == 1)
                return ordered[middleIndex];

            return (ordered[middleIndex - 1] + ordered[middleIndex]) * 0.5;
        }

        internal static string GetQuickSegmentKey(QuickSegment segment) =>
            NormalizeCoordinate(segment.StartPoint.X).ToString(CultureInfo.InvariantCulture) + "|" +
            NormalizeCoordinate(segment.StartPoint.Y).ToString(CultureInfo.InvariantCulture) + "|" +
            NormalizeCoordinate(segment.EndPoint.X).ToString(CultureInfo.InvariantCulture) + "|" +
            NormalizeCoordinate(segment.EndPoint.Y).ToString(CultureInfo.InvariantCulture);

        internal static int ResolveSideSign(Polyline guidePolyline, Point3d samplePoint, int fallbackSign)
        {
            var resolvedFallback = fallbackSign < 0 ? -1 : 1;
            if (!TryGetClosestSegment(guidePolyline, samplePoint, out var segmentStart, out var segmentEnd, out var closestPoint))
                return resolvedFallback;

            var tangent = segmentEnd - segmentStart;
            if (tangent.Length <= PointTolerance)
                return resolvedFallback;

            var normal = new Vector3d(-tangent.Y, tangent.X, 0);
            if (normal.Length <= PointTolerance)
                return resolvedFallback;

            normal = normal.GetNormal();
            var dot = (samplePoint - closestPoint).DotProduct(normal);
            if (Math.Abs(dot) <= PointTolerance)
                return resolvedFallback;

            return dot >= 0 ? 1 : -1;
        }

        internal static bool TryGetClosestSegment(
            Polyline guidePolyline,
            Point3d samplePoint,
            out Point3d segmentStart,
            out Point3d segmentEnd,
            out Point3d closestPoint)
        {
            segmentStart = Point3d.Origin;
            segmentEnd = Point3d.Origin;
            closestPoint = Point3d.Origin;

            var bestDistanceSquared = double.MaxValue;
            var found = false;

            var segmentCount = guidePolyline.Closed
                ? guidePolyline.NumberOfVertices
                : guidePolyline.NumberOfVertices - 1;
            for (var i = 0; i < segmentCount; i++)
            {
                var start = guidePolyline.GetPoint3dAt(i);
                var end = guidePolyline.GetPoint3dAt((i + 1) % guidePolyline.NumberOfVertices);
                var delta = end - start;
                var lengthSquared = delta.LengthSqrd;
                if (lengthSquared <= PointTolerance * PointTolerance)
                    continue;

                var raw = (samplePoint - start).DotProduct(delta) / lengthSquared;
                var parameter = raw < 0 ? 0 : raw > 1 ? 1 : raw;
                var projected = start + (delta * parameter);
                var dx = samplePoint.X - projected.X;
                var dy = samplePoint.Y - projected.Y;
                var dz = samplePoint.Z - projected.Z;
                var distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);

                if (distanceSquared >= bestDistanceSquared)
                    continue;

                bestDistanceSquared = distanceSquared;
                segmentStart = start;
                segmentEnd = end;
                closestPoint = projected;
                found = true;
            }

            return found;
        }

        internal static bool IsWorldXyPlane(Vector3d normal) =>
            normal.CrossProduct(Vector3d.ZAxis).Length <= PointTolerance;

        internal static bool HasArcSegments(Polyline polyline)
        {
            var segmentCount = polyline.Closed
                ? polyline.NumberOfVertices
                : polyline.NumberOfVertices - 1;
            for (var i = 0; i < segmentCount; i++)
            {
                if (Math.Abs(polyline.GetBulgeAt(i)) > PointTolerance)
                    return true;
            }

            return false;
        }

        internal static List<Point2d> CreatePolylineApproximationPoints(Polyline polyline)
        {
            var points = new List<Point2d>();
            if (polyline.NumberOfVertices == 0)
                return points;

            var segmentCount = polyline.Closed
                ? polyline.NumberOfVertices
                : polyline.NumberOfVertices - 1;
            if (segmentCount <= 0)
            {
                AddVertexIfDistinct(points, polyline.GetPoint2dAt(0));
                return points;
            }

            for (var i = 0; i < segmentCount; i++)
            {
                var nextIndex = (i + 1) % polyline.NumberOfVertices;
                AddVertexIfDistinct(points, polyline.GetPoint2dAt(i));

                var bulge = polyline.GetBulgeAt(i);
                if (Math.Abs(bulge) <= PointTolerance)
                {
                    AddVertexIfDistinct(points, polyline.GetPoint2dAt(nextIndex));
                    continue;
                }

                AddApproximatedPolylineArcSegment(points, polyline, i, nextIndex, bulge);
            }

            if (polyline.Closed &&
                points.Count > 1 &&
                ArePointsEqual(points[0], points[^1]))
            {
                points.RemoveAt(points.Count - 1);
            }

            return points;
        }

        internal static List<Point2d> CreateArcApproximationPoints(Arc arc)
        {
            var points = new List<Point2d>();
            if (arc.Radius <= PointTolerance)
                return points;

            var sweep = NormalizePositiveSweep(arc.StartAngle, arc.EndAngle);
            var divisions = ResolveArcSegmentCount(sweep);
            for (var i = 0; i <= divisions; i++)
            {
                var angle = arc.StartAngle + (sweep * i / divisions);
                AddVertexIfDistinct(
                    points,
                    new Point2d(
                        arc.Center.X + (Math.Cos(angle) * arc.Radius),
                        arc.Center.Y + (Math.Sin(angle) * arc.Radius)));
            }

            return points;
        }

        private static void AddApproximatedPolylineArcSegment(
            List<Point2d> points,
            Polyline polyline,
            int segmentIndex,
            int nextIndex,
            double bulge)
        {
            var end = polyline.GetPoint2dAt(nextIndex);
            var divisions = ResolveArcSegmentCount(Math.Abs(4.0 * Math.Atan(bulge)));
            for (var step = 1; step <= divisions; step++)
            {
                try
                {
                    var point = polyline.GetPointAtParameter(segmentIndex + (step / (double)divisions));
                    AddVertexIfDistinct(points, new Point2d(point.X, point.Y));
                }
                catch (InvalidOperationException ex)
                {
                    C_toolsDiagnostics.LogNonFatal($"{CommandName} 近似圆弧多段线失败（无效操作）", ex);
                    AddVertexIfDistinct(points, end);
                    return;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal($"{CommandName} 近似圆弧多段线失败（CAD）", ex);
                    AddVertexIfDistinct(points, end);
                    return;
                }
            }
        }

        private static double NormalizePositiveSweep(double startAngle, double endAngle)
        {
            var sweep = endAngle - startAngle;
            while (sweep <= PointTolerance)
                sweep += Math.PI * 2.0;
            while (sweep > Math.PI * 2.0)
                sweep -= Math.PI * 2.0;
            return sweep;
        }

        private static int ResolveArcSegmentCount(double includedAngle)
        {
            if (double.IsNaN(includedAngle) || double.IsInfinity(includedAngle) || includedAngle <= PointTolerance)
                return 1;

            var count = (int)Math.Ceiling(includedAngle / ArcApproximationMaxAngle);
            if (count < 4)
                return 4;
            return count > ArcApproximationMaxSegments ? ArcApproximationMaxSegments : count;
        }

        internal static int FindOrCreateNodeIndex(
            List<Point2d> nodePoints,
            List<int> nodeDegrees,
            List<List<int>> nodeLinks,
            Point2d point)
        {
            for (var i = 0; i < nodePoints.Count; i++)
            {
                if (ArePointsEqual(nodePoints[i], point))
                    return i;
            }

            nodePoints.Add(point);
            nodeDegrees.Add(0);
            nodeLinks.Add(new List<int>());
            return nodePoints.Count - 1;
        }

        internal static void AppendPartVertices(List<Point2d> target, SourceChainPart part, bool reverse)
        {
            if (!reverse)
            {
                for (var i = 0; i < part.Vertices.Count; i++)
                    AddVertexIfDistinct(target, part.Vertices[i]);
                return;
            }

            for (var i = part.Vertices.Count - 1; i >= 0; i--)
                AddVertexIfDistinct(target, part.Vertices[i]);
        }

        internal static Polyline CreateGuidePolyline(
            IReadOnlyList<Point2d> points,
            double elevation,
            bool closed = false)
        {
            var polyline = new Polyline(points.Count);
            polyline.Normal = Vector3d.ZAxis;
            polyline.Elevation = elevation;

            for (var i = 0; i < points.Count; i++)
                polyline.AddVertexAt(i, points[i], 0, 0, 0);

            polyline.Closed = closed;
            return polyline;
        }

        internal static Polyline ClonePolyline(Polyline source)
        {
            if (source.Clone() is Polyline clone)
                return clone;

            var fallback = new Polyline(source.NumberOfVertices);
            fallback.Normal = source.Normal;
            fallback.Elevation = source.Elevation;

            for (var i = 0; i < source.NumberOfVertices; i++)
            {
                fallback.AddVertexAt(
                    i,
                    source.GetPoint2dAt(i),
                    source.GetBulgeAt(i),
                    source.GetStartWidthAt(i),
                    source.GetEndWidthAt(i));
            }

            fallback.Closed = source.Closed;
            return fallback;
        }

        private static double NormalizeCoordinate(double value) =>
            Math.Round(value, 4, MidpointRounding.AwayFromZero);
    }
}
