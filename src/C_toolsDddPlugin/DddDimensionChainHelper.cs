using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsDddPlugin;

internal readonly struct DddDimensionChainSegment
{
    internal DddDimensionChainSegment(Point3d first, Point3d second, ObjectId dimensionId)
    {
        First = first;
        Second = second;
        DimensionId = dimensionId;
    }

    internal Point3d First { get; }

    internal Point3d Second { get; }

    internal ObjectId DimensionId { get; }
}

internal static class DddDimensionChainHelper
{
    internal static bool PointExists(Point3d point, IList<Point3d> points, double tolerance)
    {
        foreach (var existing in points)
        {
            if (existing.DistanceTo(point) < tolerance)
                return true;
        }

        return false;
    }

    internal static bool SharesAnyEndpoint(
        Point3d firstPoint,
        Point3d secondPoint,
        IEnumerable<DddDimensionChainSegment> segments,
        double tolerance)
    {
        foreach (var segment in segments)
        {
            if (PointsCoincide(firstPoint, segment.First, tolerance) ||
                PointsCoincide(firstPoint, segment.Second, tolerance) ||
                PointsCoincide(secondPoint, segment.First, tolerance) ||
                PointsCoincide(secondPoint, segment.Second, tolerance))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ContainsSegment(
        IEnumerable<DddDimensionChainSegment> segments,
        Point3d firstPoint,
        Point3d secondPoint,
        double tolerance)
    {
        foreach (var segment in segments)
        {
            if (PointsCoincide(segment.First, firstPoint, tolerance) &&
                PointsCoincide(segment.Second, secondPoint, tolerance))
            {
                return true;
            }
        }

        return false;
    }

    internal static void AddDimensionIdsInOrder(
        List<DddDimensionChainSegment> segments,
        List<ObjectId> dimensionIds,
        Comparison<DddDimensionChainSegment> comparison)
    {
        segments.Sort(comparison);
        foreach (var segment in segments)
            dimensionIds.Add(segment.DimensionId);
    }

    internal static Point3d ResolveSelectedContinuationCursor(
        IList<Point3d> chainPoints,
        double selectedStartMetric,
        double selectedEndMetric,
        double pickedMetric)
    {
        var selectedMidMetric = (selectedStartMetric + selectedEndMetric) * 0.5;
        return pickedMetric < selectedMidMetric
            ? chainPoints[0]
            : chainPoints[^1];
    }

    internal static bool TryResolveSplitSegment(
        IList<Point3d> chainPoints,
        IList<ObjectId> dimensionIds,
        Func<Point3d, Point3d, (bool IsInside, Point3d SplitPoint)> splitEvaluator,
        out ObjectId splitDimensionId,
        out int splitIndex,
        out Point3d splitFirstPoint,
        out Point3d splitSecondPoint,
        out Point3d splitPoint)
    {
        splitDimensionId = ObjectId.Null;
        splitIndex = -1;
        splitFirstPoint = default;
        splitSecondPoint = default;
        splitPoint = default;

        var segmentCount = Math.Min(dimensionIds.Count, Math.Max(0, chainPoints.Count - 1));
        for (var index = 0; index < segmentCount; index++)
        {
            var firstPoint = chainPoints[index];
            var secondPoint = chainPoints[index + 1];
            var evaluation = splitEvaluator(firstPoint, secondPoint);
            if (!evaluation.IsInside)
                continue;

            splitDimensionId = dimensionIds[index];
            splitIndex = index;
            splitFirstPoint = firstPoint;
            splitSecondPoint = secondPoint;
            splitPoint = evaluation.SplitPoint;
            return true;
        }

        return false;
    }

    internal static bool TryReplaceSplitSegment(
        List<Point3d> chainPoints,
        List<ObjectId> dimensionIds,
        int splitIndex,
        Point3d splitPoint,
        IReadOnlyList<ObjectId> splitDimensionIds)
    {
        if (splitIndex < 0 ||
            splitIndex >= dimensionIds.Count ||
            splitIndex + 1 >= chainPoints.Count ||
            splitDimensionIds.Count != 2)
        {
            return false;
        }

        chainPoints.Insert(splitIndex + 1, splitPoint);
        dimensionIds.RemoveAt(splitIndex);
        dimensionIds.InsertRange(splitIndex, splitDimensionIds);
        return dimensionIds.Count == chainPoints.Count - 1;
    }

    private static bool PointsCoincide(Point3d left, Point3d right, double tolerance) =>
        left.DistanceTo(right) < tolerance;
}
