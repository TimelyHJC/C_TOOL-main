using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static class RectangleMergeGeometry
{
    private const double PointTolerance = 1e-6;
    private const double AngleTolerance = 1e-9;

    internal static bool TryBuildMergedBoundaryLoops(
        IReadOnlyList<RectangleFootprint> rectangles,
        out List<List<Point3d>> loops,
        out string error)
    {
        loops = new List<List<Point3d>>();
        error = "";

        if (rectangles.Count == 0)
        {
            error = "未找到可合并的矩形。";
            return false;
        }

        if (!TryResolveBasis(rectangles[0], out var origin, out var axisU, out var axisV, out error))
            return false;

        var localRectangles = new List<LocalRectangle>(rectangles.Count);
        for (var index = 0; index < rectangles.Count; index++)
        {
            if (!TryProjectRectangle(rectangles[index], origin, axisU, axisV, out var localRectangle, out error))
                return false;

            localRectangles.Add(localRectangle);
        }

        if (!TryBuildLocalBoundaryLoops(localRectangles, out var localLoops, out error))
            return false;

        foreach (var localLoop in localLoops)
        {
            var worldLoop = new List<Point3d>(localLoop.Count);
            for (var index = 0; index < localLoop.Count; index++)
                worldLoop.Add(ToWorldPoint(origin, axisU, axisV, localLoop[index]));

            loops.Add(worldLoop);
        }

        return true;
    }

    internal static RectangleFootprint CreateFootprint(params Point3d[] corners)
    {
        return new RectangleFootprint(corners);
    }

    private static bool TryResolveBasis(
        RectangleFootprint rectangle,
        out Point3d origin,
        out Vector3d axisU,
        out Vector3d axisV,
        out string error)
    {
        origin = Point3d.Origin;
        axisU = Vector3d.XAxis;
        axisV = Vector3d.YAxis;
        error = "";

        var corners = rectangle.Corners;
        if (corners.Count != 4)
        {
            error = "仅支持四个顶点的矩形。";
            return false;
        }

        var edgeU = corners[1] - corners[0];
        var edgeV = corners[2] - corners[1];
        if (!IsUsableEdge(edgeU) || !IsUsableEdge(edgeV))
        {
            error = "矩形边长无效。";
            return false;
        }

        if (Math.Abs(corners[1].Z - corners[0].Z) > PointTolerance ||
            Math.Abs(corners[2].Z - corners[0].Z) > PointTolerance ||
            Math.Abs(corners[3].Z - corners[0].Z) > PointTolerance)
        {
            error = "仅支持位于同一平面的矩形。";
            return false;
        }

        if (!ArePerpendicular(edgeU, edgeV))
        {
            error = "仅支持四边相互垂直的矩形。";
            return false;
        }

        axisU = edgeU.GetNormal();
        axisV = new Vector3d(-axisU.Y, axisU.X, 0.0);
        if (edgeV.DotProduct(axisV) < 0.0)
            axisV = -axisV;

        origin = corners[0];
        return true;
    }

    private static bool TryProjectRectangle(
        RectangleFootprint rectangle,
        Point3d origin,
        Vector3d axisU,
        Vector3d axisV,
        out LocalRectangle localRectangle,
        out string error)
    {
        localRectangle = default;
        error = "";

        if (rectangle.Corners.Count != 4)
        {
            error = "仅支持四个顶点的矩形。";
            return false;
        }

        var localPoints = new List<Point2d>(4);
        for (var index = 0; index < rectangle.Corners.Count; index++)
        {
            var point = rectangle.Corners[index];
            if (Math.Abs(point.Z - origin.Z) > PointTolerance)
            {
                error = "仅支持位于同一平面的矩形。";
                return false;
            }

            var delta = point - origin;
            localPoints.Add(new Point2d(delta.DotProduct(axisU), delta.DotProduct(axisV)));
        }

        if (!TryCreateLocalRectangle(localPoints, out localRectangle, out error))
            return false;

        return true;
    }

    private static bool TryCreateLocalRectangle(
        IReadOnlyList<Point2d> points,
        out LocalRectangle rectangle,
        out string error)
    {
        rectangle = default;
        error = "";

        if (points.Count != 4)
        {
            error = "仅支持四个顶点的矩形。";
            return false;
        }

        var xs = CollectDistinctCoordinates(points.Select(point => point.X));
        var ys = CollectDistinctCoordinates(points.Select(point => point.Y));
        if (xs.Count != 2 || ys.Count != 2)
        {
            error = "仅支持同向矩形。";
            return false;
        }

        var left = xs[0];
        var right = xs[1];
        var bottom = ys[0];
        var top = ys[1];
        if (right <= left + PointTolerance || top <= bottom + PointTolerance)
        {
            error = "矩形尺寸无效。";
            return false;
        }

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (!IsNear(point.X, left) && !IsNear(point.X, right))
            {
                error = "仅支持同向矩形。";
                return false;
            }

            if (!IsNear(point.Y, bottom) && !IsNear(point.Y, top))
            {
                error = "仅支持同向矩形。";
                return false;
            }
        }

        rectangle = new LocalRectangle(left, right, bottom, top);
        return true;
    }

    private static bool TryBuildLocalBoundaryLoops(
        IReadOnlyList<LocalRectangle> rectangles,
        out List<List<Point2d>> loops,
        out string error)
    {
        loops = new List<List<Point2d>>();
        error = "";

        var xCoords = CollectDistinctCoordinates(rectangles.SelectMany(rectangle => new[] { rectangle.Left, rectangle.Right }));
        var yCoords = CollectDistinctCoordinates(rectangles.SelectMany(rectangle => new[] { rectangle.Bottom, rectangle.Top }));
        if (xCoords.Count < 2 || yCoords.Count < 2)
        {
            error = "矩形合并失败。";
            return false;
        }

        var xCellCount = xCoords.Count - 1;
        var yCellCount = yCoords.Count - 1;
        var occupied = new bool[xCellCount, yCellCount];

        for (var xIndex = 0; xIndex < xCellCount; xIndex++)
        {
            var centerX = (xCoords[xIndex] + xCoords[xIndex + 1]) * 0.5;
            for (var yIndex = 0; yIndex < yCellCount; yIndex++)
            {
                var centerY = (yCoords[yIndex] + yCoords[yIndex + 1]) * 0.5;
                occupied[xIndex, yIndex] = rectangles.Any(rectangle =>
                    centerX > rectangle.Left + PointTolerance &&
                    centerX < rectangle.Right - PointTolerance &&
                    centerY > rectangle.Bottom + PointTolerance &&
                    centerY < rectangle.Top - PointTolerance);
            }
        }

        var segments = BuildBoundarySegments(xCoords, yCoords, occupied);
        if (segments.Count == 0)
        {
            error = "未能生成闭合边界。";
            return false;
        }

        if (!TryTraceBoundaryLoops(segments, out loops, out error))
            return false;

        if (loops.Count == 0)
        {
            error = "未能生成闭合边界。";
            return false;
        }

        return true;
    }

    private static List<BoundarySegment> BuildBoundarySegments(
        IReadOnlyList<double> xCoords,
        IReadOnlyList<double> yCoords,
        bool[,] occupied)
    {
        var segments = new List<BoundarySegment>();
        var xCellCount = xCoords.Count - 1;
        var yCellCount = yCoords.Count - 1;

        for (var yIndex = 0; yIndex < yCoords.Count; yIndex++)
        {
            var runStart = -1;
            var runDirection = 0;
            for (var xIndex = 0; xIndex < xCellCount; xIndex++)
            {
                var aboveOccupied = yIndex < yCellCount && occupied[xIndex, yIndex];
                var belowOccupied = yIndex > 0 && occupied[xIndex, yIndex - 1];
                var boundary = aboveOccupied != belowOccupied;
                var direction = aboveOccupied ? 0 : 180;

                if (!boundary)
                {
                    if (runStart >= 0)
                        FlushHorizontalRun(segments, xCoords, yCoords, yIndex, runStart, xIndex, runDirection);

                    runStart = -1;
                    continue;
                }

                if (runStart < 0)
                {
                    runStart = xIndex;
                    runDirection = direction;
                    continue;
                }

                if (runDirection != direction)
                {
                    FlushHorizontalRun(segments, xCoords, yCoords, yIndex, runStart, xIndex, runDirection);
                    runStart = xIndex;
                    runDirection = direction;
                }
            }

            if (runStart >= 0)
                FlushHorizontalRun(segments, xCoords, yCoords, yIndex, runStart, xCellCount, runDirection);
        }

        for (var xIndex = 0; xIndex < xCoords.Count; xIndex++)
        {
            var runStart = -1;
            var runDirection = 0;
            for (var yIndex = 0; yIndex < yCellCount; yIndex++)
            {
                var rightOccupied = xIndex < xCellCount && occupied[xIndex, yIndex];
                var leftOccupied = xIndex > 0 && occupied[xIndex - 1, yIndex];
                var boundary = rightOccupied != leftOccupied;
                var direction = rightOccupied ? 270 : 90;

                if (!boundary)
                {
                    if (runStart >= 0)
                        FlushVerticalRun(segments, xCoords, yCoords, xIndex, runStart, yIndex, runDirection);

                    runStart = -1;
                    continue;
                }

                if (runStart < 0)
                {
                    runStart = yIndex;
                    runDirection = direction;
                    continue;
                }

                if (runDirection != direction)
                {
                    FlushVerticalRun(segments, xCoords, yCoords, xIndex, runStart, yIndex, runDirection);
                    runStart = yIndex;
                    runDirection = direction;
                }
            }

            if (runStart >= 0)
                FlushVerticalRun(segments, xCoords, yCoords, xIndex, runStart, yCellCount, runDirection);
        }

        return segments;
    }

    private static void FlushHorizontalRun(
        List<BoundarySegment> segments,
        IReadOnlyList<double> xCoords,
        IReadOnlyList<double> yCoords,
        int yIndex,
        int runStart,
        int runEndExclusive,
        int direction)
    {
        if (runEndExclusive <= runStart)
            return;

        var y = yCoords[yIndex];
        if (direction == 0)
        {
            segments.Add(new BoundarySegment(
                new Point2d(xCoords[runStart], y),
                new Point2d(xCoords[runEndExclusive], y)));
            return;
        }

        segments.Add(new BoundarySegment(
            new Point2d(xCoords[runEndExclusive], y),
            new Point2d(xCoords[runStart], y)));
    }

    private static void FlushVerticalRun(
        List<BoundarySegment> segments,
        IReadOnlyList<double> xCoords,
        IReadOnlyList<double> yCoords,
        int xIndex,
        int runStart,
        int runEndExclusive,
        int direction)
    {
        if (runEndExclusive <= runStart)
            return;

        var x = xCoords[xIndex];
        if (direction == 90)
        {
            segments.Add(new BoundarySegment(
                new Point2d(x, yCoords[runStart]),
                new Point2d(x, yCoords[runEndExclusive])));
            return;
        }

        segments.Add(new BoundarySegment(
            new Point2d(x, yCoords[runEndExclusive]),
            new Point2d(x, yCoords[runStart])));
    }

    private static bool TryTraceBoundaryLoops(
        IReadOnlyList<BoundarySegment> segments,
        out List<List<Point2d>> loops,
        out string error)
    {
        loops = new List<List<Point2d>>();
        error = "";

        var outgoing = new Dictionary<PointKey, List<int>>();
        for (var index = 0; index < segments.Count; index++)
        {
            var key = PointKey.From(segments[index].Start);
            if (!outgoing.TryGetValue(key, out var list))
            {
                list = new List<int>();
                outgoing.Add(key, list);
            }

            list.Add(index);
        }

        var used = new bool[segments.Count];
        for (var startIndex = 0; startIndex < segments.Count; startIndex++)
        {
            if (used[startIndex])
                continue;

            var loop = new List<Point2d>();
            var currentIndex = startIndex;
            var startKey = PointKey.From(segments[startIndex].Start);
            var guard = 0;

            while (true)
            {
                if (guard++ > segments.Count * 8)
                {
                    error = "闭合图形追踪失败。";
                    return false;
                }

                if (used[currentIndex])
                {
                    error = "闭合图形追踪失败。";
                    return false;
                }

                used[currentIndex] = true;
                loop.Add(segments[currentIndex].Start);
                var currentEnd = segments[currentIndex].End;
                if (PointKey.From(currentEnd).Equals(startKey))
                {
                    loop.Add(currentEnd);
                    break;
                }

                if (!TrySelectNextSegment(outgoing, segments, used, currentIndex, currentEnd, out currentIndex))
                {
                    error = "闭合图形追踪失败。";
                    return false;
                }
            }

            RemoveDuplicateClosingPoint(loop);
            if (loop.Count < 3)
            {
                error = "闭合图形顶点不足。";
                return false;
            }

            loops.Add(loop);
        }

        return true;
    }

    private static bool TrySelectNextSegment(
        IReadOnlyDictionary<PointKey, List<int>> outgoing,
        IReadOnlyList<BoundarySegment> segments,
        bool[] used,
        int currentIndex,
        Point2d currentEnd,
        out int nextIndex)
    {
        nextIndex = -1;
        var key = PointKey.From(currentEnd);
        if (!outgoing.TryGetValue(key, out var candidates) || candidates.Count == 0)
            return false;

        var reverseAngle = NormalizeAngle(segments[currentIndex].Angle + 180.0);
        var bestDelta = double.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidateIndex = candidates[i];
            if (used[candidateIndex])
                continue;

            var delta = NormalizePositiveAngle(segments[candidateIndex].Angle - reverseAngle);
            if (delta <= AngleTolerance)
                continue;

            if (delta < bestDelta)
            {
                bestDelta = delta;
                nextIndex = candidateIndex;
            }
        }

        return nextIndex >= 0;
    }

    private static void RemoveDuplicateClosingPoint(List<Point2d> points)
    {
        if (points.Count > 1 && ArePointsEqual(points[0], points[^1]))
            points.RemoveAt(points.Count - 1);
    }

    private static bool ArePointsEqual(Point2d a, Point2d b) =>
        a.GetDistanceTo(b) <= PointTolerance;

    private static bool IsNear(double a, double b) =>
        Math.Abs(a - b) <= PointTolerance;

    private static bool IsUsableEdge(Vector3d edge) =>
        edge.Length > PointTolerance && !double.IsNaN(edge.Length) && !double.IsInfinity(edge.Length);

    private static bool ArePerpendicular(Vector3d a, Vector3d b) =>
        Math.Abs(a.DotProduct(b)) <= Math.Max(a.Length, b.Length) * PointTolerance * 10.0;

    private static Point3d ToWorldPoint(Point3d origin, Vector3d axisU, Vector3d axisV, Point2d localPoint) =>
        origin + (axisU * localPoint.X) + (axisV * localPoint.Y);

    private static List<double> CollectDistinctCoordinates(IEnumerable<double> values)
    {
        var ordered = values
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
            .OrderBy(value => value)
            .ToList();

        var distinct = new List<double>();
        for (var index = 0; index < ordered.Count; index++)
        {
            if (distinct.Count == 0 || Math.Abs(ordered[index] - distinct[^1]) > PointTolerance)
                distinct.Add(ordered[index]);
        }

        return distinct;
    }

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % 360.0;
        if (normalized < 0.0)
            normalized += 360.0;
        return normalized;
    }

    private static double NormalizePositiveAngle(double angle)
    {
        var normalized = NormalizeAngle(angle);
        return normalized <= AngleTolerance ? 0.0 : normalized;
    }

    internal readonly struct RectangleFootprint
    {
        internal RectangleFootprint(Point3d[] corners)
        {
            Corners = corners ?? Array.Empty<Point3d>();
        }

        internal IReadOnlyList<Point3d> Corners { get; }
    }

    private readonly struct LocalRectangle
    {
        internal LocalRectangle(double left, double right, double bottom, double top)
        {
            Left = left;
            Right = right;
            Bottom = bottom;
            Top = top;
        }

        internal double Left { get; }
        internal double Right { get; }
        internal double Bottom { get; }
        internal double Top { get; }
    }

    private readonly struct BoundarySegment
    {
        internal BoundarySegment(Point2d start, Point2d end)
        {
            Start = start;
            End = end;
            Angle = ResolveAngle(start, end);
        }

        internal Point2d Start { get; }
        internal Point2d End { get; }
        internal double Angle { get; }

        private static double ResolveAngle(Point2d start, Point2d end)
        {
            if (Math.Abs(end.X - start.X) <= PointTolerance)
                return end.Y > start.Y ? 90.0 : 270.0;

            return end.X > start.X ? 0.0 : 180.0;
        }
    }

    private readonly struct PointKey : IEquatable<PointKey>
    {
        private const double KeyScale = 1_000_000.0;

        private PointKey(long x, long y)
        {
            X = x;
            Y = y;
        }

        private long X { get; }
        private long Y { get; }

        internal static PointKey From(Point2d point)
        {
            return new PointKey(
                (long)Math.Round(point.X * KeyScale),
                (long)Math.Round(point.Y * KeyScale));
        }

        public bool Equals(PointKey other) => X == other.X && Y == other.Y;

        public override bool Equals(object? obj) => obj is PointKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397) ^ Y.GetHashCode();
            }
        }
    }
}
