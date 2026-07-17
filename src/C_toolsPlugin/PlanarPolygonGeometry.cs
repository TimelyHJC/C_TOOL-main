using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static class PlanarPolygonGeometry
{
    internal static bool TryComputeCentroid(
        IReadOnlyList<Point2d> vertices,
        double elevation,
        out Point3d centroid,
        out double area,
        double tolerance = 1e-6)
    {
        centroid = Point3d.Origin;
        area = 0.0;
        if (vertices.Count < 3)
            return false;

        var crossSum = 0.0;
        var weightedX = 0.0;
        var weightedY = 0.0;
        for (var i = 0; i < vertices.Count; i++)
        {
            var current = vertices[i];
            var next = vertices[(i + 1) % vertices.Count];
            var cross = (current.X * next.Y) - (next.X * current.Y);
            crossSum += cross;
            weightedX += (current.X + next.X) * cross;
            weightedY += (current.Y + next.Y) * cross;
        }

        area = Math.Abs(crossSum) * 0.5;
        if (area <= tolerance)
            return false;

        centroid = new Point3d(
            weightedX / (3.0 * crossSum),
            weightedY / (3.0 * crossSum),
            elevation);
        return true;
    }

    internal static bool TryComputeCentroid(
        IReadOnlyList<Point3d> vertices,
        out Point3d centroid,
        out double area,
        double tolerance = 1e-6)
    {
        centroid = Point3d.Origin;
        area = 0.0;
        if (vertices.Count < 3)
            return false;

        var points = new Point2d[vertices.Count];
        var elevation = 0.0;
        for (var i = 0; i < vertices.Count; i++)
        {
            points[i] = new Point2d(vertices[i].X, vertices[i].Y);
            elevation += vertices[i].Z;
        }

        elevation /= vertices.Count;
        return TryComputeCentroid(points, elevation, out centroid, out area, tolerance);
    }

    internal static Point3d ComputeBoundsCenter(IReadOnlyList<Point2d> vertices, double elevation)
    {
        if (vertices.Count == 0)
            return new Point3d(0.0, 0.0, elevation);

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        for (var i = 0; i < vertices.Count; i++)
        {
            var point = vertices[i];
            if (point.X < minX) minX = point.X;
            if (point.Y < minY) minY = point.Y;
            if (point.X > maxX) maxX = point.X;
            if (point.Y > maxY) maxY = point.Y;
        }

        return new Point3d((minX + maxX) * 0.5, (minY + maxY) * 0.5, elevation);
    }

    internal static Point3d ComputeBoundsCenter(IReadOnlyList<Point3d> vertices)
    {
        if (vertices.Count == 0)
            return Point3d.Origin;

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var minZ = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        var maxZ = double.MinValue;
        for (var i = 0; i < vertices.Count; i++)
        {
            var point = vertices[i];
            if (point.X < minX) minX = point.X;
            if (point.Y < minY) minY = point.Y;
            if (point.Z < minZ) minZ = point.Z;
            if (point.X > maxX) maxX = point.X;
            if (point.Y > maxY) maxY = point.Y;
            if (point.Z > maxZ) maxZ = point.Z;
        }

        return new Point3d(
            (minX + maxX) * 0.5,
            (minY + maxY) * 0.5,
            (minZ + maxZ) * 0.5);
    }

    internal static bool ContainsPoint(
        IReadOnlyList<Point3d> vertices,
        Point3d point,
        double tolerance = 1e-6)
    {
        if (vertices.Count < 3)
            return false;

        var points = new Point2d[vertices.Count];
        for (var i = 0; i < vertices.Count; i++)
            points[i] = new Point2d(vertices[i].X, vertices[i].Y);

        return ContainsPoint(points, new Point2d(point.X, point.Y), tolerance);
    }

    internal static bool ContainsPoint(
        IReadOnlyList<Point2d> vertices,
        Point2d point,
        double tolerance = 1e-6)
    {
        if (vertices.Count < 3)
            return false;

        var inside = false;
        for (int i = 0, previous = vertices.Count - 1; i < vertices.Count; previous = i++)
        {
            var a = vertices[previous];
            var b = vertices[i];
            if (IsPointOnSegment(a, b, point, tolerance))
                return true;

            if ((a.Y > point.Y) == (b.Y > point.Y))
                continue;

            var intersectionX = ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X;
            if (point.X < intersectionX)
                inside = !inside;
        }

        return inside;
    }

    private static bool IsPointOnSegment(Point2d a, Point2d b, Point2d point, double tolerance)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= tolerance)
            return point.GetDistanceTo(a) <= tolerance;

        var cross = ((point.X - a.X) * dy) - ((point.Y - a.Y) * dx);
        if (Math.Abs(cross) > tolerance * Math.Max(1.0, length))
            return false;

        var dot = ((point.X - a.X) * dx) + ((point.Y - a.Y) * dy);
        if (dot < -tolerance)
            return false;

        return dot <= (length * length) + tolerance;
    }
}
