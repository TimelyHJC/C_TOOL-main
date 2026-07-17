using System;
using System.Collections.Generic;

namespace C_toolsDddPlugin;

internal static class DddDimensionTextAvoidancePlanner
{
    private const double Tolerance = 1e-6;

    internal static PlanResult Plan(
        IReadOnlyList<PlanItem> items,
        double laneSpacing,
        int maxLaneIndex,
        double maxAxisShift,
        double placementGap)
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));
        if (laneSpacing <= Tolerance)
            throw new ArgumentOutOfRangeException(nameof(laneSpacing));
        if (maxLaneIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLaneIndex));
        if (maxAxisShift < 0.0)
            throw new ArgumentOutOfRangeException(nameof(maxAxisShift));

        var axisOffsets = new double[items.Count];
        var normalOffsets = new double[items.Count];
        var laneOffsets = new int[items.Count];
        var conflictFlags = BuildConflictFlags(items);
        if (!HasAnyConflict(conflictFlags))
            return new PlanResult(axisOffsets, normalOffsets, laneOffsets, conflictFlags);

        var placed = new List<ProjectedTextBounds>(items.Count);
        var fixedAtAnchor = new bool[items.Count];
        ReserveNonConflictingAnchors(items, conflictFlags, fixedAtAnchor, placed);
        ReserveConflictingAnchors(items, conflictFlags, fixedAtAnchor, placed);

        foreach (var index in BuildRemainingPlacementOrder(items, fixedAtAnchor))
        {
            var choice = ChoosePlacement(
                items[index],
                placed,
                laneSpacing,
                maxLaneIndex,
                maxAxisShift,
                Math.Max(placementGap, 0.0));
            axisOffsets[index] = choice.AxisOffset;
            normalOffsets[index] = choice.NormalOffset;
            laneOffsets[index] = choice.LaneOffset;
            placed.Add(items[index].Bounds.Translate(choice.AxisOffset, choice.NormalOffset));
        }

        return new PlanResult(axisOffsets, normalOffsets, laneOffsets, conflictFlags);
    }

    private static PlacementChoice ChoosePlacement(
        PlanItem anchorItem,
        List<ProjectedTextBounds> placed,
        double laneSpacing,
        int maxLaneIndex,
        double maxAxisShift,
        double placementGap)
    {
        if (TryFindAxisPlacement(
                anchorItem,
                normalOffset: 0.0,
                placed,
                maxAxisShift,
                placementGap,
                out var baseAxisOffset))
        {
            return new PlacementChoice(baseAxisOffset, normalOffset: 0.0, laneOffset: 0);
        }

        for (var laneIndex = 1; laneIndex <= maxLaneIndex; laneIndex++)
        {
            PlacementChoice? bestAtLevel = null;
            ConsiderLane(anchorItem, placed, laneSpacing, laneIndex, maxAxisShift, placementGap, ref bestAtLevel);
            ConsiderLane(anchorItem, placed, laneSpacing, -laneIndex, maxAxisShift, placementGap, ref bestAtLevel);
            if (bestAtLevel.HasValue)
                return bestAtLevel.Value;
        }

        return FindBestBoundedFallback(
            anchorItem,
            placed,
            laneSpacing,
            maxLaneIndex,
            maxAxisShift,
            placementGap);
    }

    private static void ConsiderLane(
        PlanItem anchorItem,
        List<ProjectedTextBounds> placed,
        double laneSpacing,
        int laneOffset,
        double maxAxisShift,
        double placementGap,
        ref PlacementChoice? bestChoice)
    {
        var normalOffset = anchorItem.LineNormal +
                           (laneOffset * laneSpacing) -
                           anchorItem.Bounds.NormalCenter;
        if (!TryFindAxisPlacement(
                anchorItem,
                normalOffset,
                placed,
                maxAxisShift,
                placementGap,
                out var axisOffset))
        {
            return;
        }

        var candidate = new PlacementChoice(axisOffset, normalOffset, laneOffset);
        if (!bestChoice.HasValue || candidate.IsCloserThan(bestChoice.Value))
            bestChoice = candidate;
    }

    private static bool TryFindAxisPlacement(
        PlanItem anchorItem,
        double normalOffset,
        List<ProjectedTextBounds> placed,
        double maxAxisShift,
        double placementGap,
        out double axisOffset)
    {
        axisOffset = 0.0;
        var laneBounds = anchorItem.Bounds.Translate(0.0, normalOffset);
        var candidates = BuildAxisCandidates(laneBounds, placed, maxAxisShift, placementGap);
        foreach (var candidateOffset in candidates)
        {
            var candidateBounds = laneBounds.Translate(candidateOffset, 0.0);
            if (!anchorItem.ContainsWithinCircle(candidateBounds))
                continue;
            if (Collides(candidateBounds, placed, placementGap))
                continue;

            axisOffset = candidateOffset;
            return true;
        }

        return false;
    }

    private static List<double> BuildAxisCandidates(
        ProjectedTextBounds bounds,
        List<ProjectedTextBounds> placed,
        double maxAxisShift,
        double placementGap)
    {
        var candidates = new List<double> { 0.0 };
        foreach (var obstacle in placed)
        {
            if (!bounds.NormalOverlaps(obstacle, placementGap))
                continue;

            AddUniqueShift(
                candidates,
                obstacle.AxisStart - placementGap - bounds.AxisEnd,
                maxAxisShift);
            AddUniqueShift(
                candidates,
                obstacle.AxisEnd + placementGap - bounds.AxisStart,
                maxAxisShift);
        }

        candidates.Sort(CompareAxisOffsets);
        return candidates;
    }

    private static void AddUniqueShift(List<double> candidates, double shift, double maxAxisShift)
    {
        if (Math.Abs(shift) > maxAxisShift + Tolerance)
            return;

        foreach (var existing in candidates)
        {
            if (Math.Abs(existing - shift) <= Tolerance)
                return;
        }

        candidates.Add(shift);
    }

    private static int CompareAxisOffsets(double left, double right)
    {
        var distanceCompare = Math.Abs(left).CompareTo(Math.Abs(right));
        if (distanceCompare != 0)
            return distanceCompare;

        // Equal moves are deterministic: left before right.
        return left.CompareTo(right);
    }

    private static PlacementChoice FindBestBoundedFallback(
        PlanItem anchorItem,
        List<ProjectedTextBounds> placed,
        double laneSpacing,
        int maxLaneIndex,
        double maxAxisShift,
        double placementGap)
    {
        CandidateScore? bestScore = null;
        for (var absLaneIndex = 0; absLaneIndex <= maxLaneIndex; absLaneIndex++)
        {
            if (absLaneIndex == 0)
            {
                ScoreLaneFallback(anchorItem, placed, 0, laneSpacing, maxAxisShift, placementGap, ref bestScore);
                continue;
            }

            ScoreLaneFallback(anchorItem, placed, absLaneIndex, laneSpacing, maxAxisShift, placementGap, ref bestScore);
            ScoreLaneFallback(anchorItem, placed, -absLaneIndex, laneSpacing, maxAxisShift, placementGap, ref bestScore);
        }

        return bestScore?.Choice ?? new PlacementChoice(0.0, 0.0, 0);
    }

    private static void ScoreLaneFallback(
        PlanItem anchorItem,
        List<ProjectedTextBounds> placed,
        int laneOffset,
        double laneSpacing,
        double maxAxisShift,
        double placementGap,
        ref CandidateScore? bestScore)
    {
        var normalOffset = laneOffset == 0
            ? 0.0
            : anchorItem.LineNormal + (laneOffset * laneSpacing) - anchorItem.Bounds.NormalCenter;
        var laneBounds = anchorItem.Bounds.Translate(0.0, normalOffset);
        foreach (var axisOffset in BuildAxisCandidates(laneBounds, placed, maxAxisShift, placementGap))
        {
            var candidateBounds = laneBounds.Translate(axisOffset, 0.0);
            if (!anchorItem.ContainsWithinCircle(candidateBounds))
                continue;
            var score = ScoreCandidate(
                new PlacementChoice(axisOffset, normalOffset, laneOffset),
                candidateBounds,
                placed,
                placementGap);
            if (!bestScore.HasValue || score.IsBetterThan(bestScore.Value))
                bestScore = score;
        }
    }

    private static CandidateScore ScoreCandidate(
        PlacementChoice choice,
        ProjectedTextBounds candidateBounds,
        List<ProjectedTextBounds> placed,
        double placementGap)
    {
        var collisionCount = 0;
        var overlapArea = 0.0;
        foreach (var obstacle in placed)
        {
            if (!candidateBounds.Overlaps(obstacle, placementGap))
                continue;

            collisionCount++;
            overlapArea += candidateBounds.OverlapArea(obstacle, placementGap);
        }

        return new CandidateScore(choice, collisionCount, overlapArea);
    }

    private static bool[] BuildConflictFlags(IReadOnlyList<PlanItem> items)
    {
        var conflicts = new bool[items.Count];
        for (var leftIndex = 0; leftIndex < items.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < items.Count; rightIndex++)
            {
                if (!items[leftIndex].Bounds.Overlaps(items[rightIndex].Bounds, 0.0))
                    continue;

                conflicts[leftIndex] = true;
                conflicts[rightIndex] = true;
            }
        }

        return conflicts;
    }

    private static bool HasAnyConflict(bool[] conflictFlags)
    {
        foreach (var conflict in conflictFlags)
        {
            if (conflict)
                return true;
        }

        return false;
    }

    private static void ReserveNonConflictingAnchors(
        IReadOnlyList<PlanItem> items,
        bool[] conflictFlags,
        bool[] fixedAtAnchor,
        List<ProjectedTextBounds> placed)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (conflictFlags[index])
                continue;

            fixedAtAnchor[index] = true;
            placed.Add(items[index].Bounds);
        }
    }

    private static void ReserveConflictingAnchors(
        IReadOnlyList<PlanItem> items,
        bool[] conflictFlags,
        bool[] fixedAtAnchor,
        List<ProjectedTextBounds> placed)
    {
        var candidates = new List<int>();
        for (var index = 0; index < items.Count; index++)
        {
            if (conflictFlags[index])
                candidates.Add(index);
        }

        candidates.Sort((leftIndex, rightIndex) =>
        {
            var left = items[leftIndex];
            var right = items[rightIndex];
            if (left.PreferAnchor != right.PreferAnchor)
                return left.PreferAnchor ? -1 : 1;

            var endCompare = left.Bounds.AxisEnd.CompareTo(right.Bounds.AxisEnd);
            if (endCompare != 0)
                return endCompare;
            return left.Bounds.AxisStart.CompareTo(right.Bounds.AxisStart);
        });

        foreach (var index in candidates)
        {
            if (Collides(items[index].Bounds, placed, 0.0))
                continue;

            fixedAtAnchor[index] = true;
            placed.Add(items[index].Bounds);
        }
    }

    private static List<int> BuildRemainingPlacementOrder(IReadOnlyList<PlanItem> items, bool[] fixedAtAnchor)
    {
        var order = new List<int>();
        for (var index = 0; index < items.Count; index++)
        {
            if (!fixedAtAnchor[index])
                order.Add(index);
        }

        order.Sort((leftIndex, rightIndex) =>
        {
            var centerCompare = items[leftIndex].Bounds.AxisCenter.CompareTo(items[rightIndex].Bounds.AxisCenter);
            if (centerCompare != 0)
                return centerCompare;
            return leftIndex.CompareTo(rightIndex);
        });
        return order;
    }

    private static bool Collides(
        ProjectedTextBounds candidate,
        List<ProjectedTextBounds> placed,
        double placementGap)
    {
        foreach (var obstacle in placed)
        {
            if (candidate.Overlaps(obstacle, placementGap))
                return true;
        }

        return false;
    }

    internal readonly struct PlanItem
    {
        internal PlanItem(
            ProjectedTextBounds bounds,
            bool preferAnchor,
            double circleCenterAxis,
            double lineNormal,
            double maxRadius)
        {
            Bounds = bounds;
            PreferAnchor = preferAnchor;
            CircleCenterAxis = circleCenterAxis;
            LineNormal = lineNormal;
            MaxRadius = Math.Max(maxRadius, 0.0);
        }

        internal ProjectedTextBounds Bounds { get; }

        internal bool PreferAnchor { get; }

        internal double CircleCenterAxis { get; }

        internal double LineNormal { get; }

        internal double MaxRadius { get; }

        internal bool ContainsWithinCircle(ProjectedTextBounds candidateBounds) =>
            candidateBounds.MaxCornerDistanceSquared(CircleCenterAxis, LineNormal) <=
            (MaxRadius * MaxRadius) + Tolerance;
    }

    internal readonly struct ProjectedTextBounds
    {
        internal ProjectedTextBounds(double axisStart, double axisEnd, double normalStart, double normalEnd)
        {
            AxisStart = Math.Min(axisStart, axisEnd);
            AxisEnd = Math.Max(axisStart, axisEnd);
            NormalStart = Math.Min(normalStart, normalEnd);
            NormalEnd = Math.Max(normalStart, normalEnd);
        }

        internal double AxisStart { get; }

        internal double AxisEnd { get; }

        internal double NormalStart { get; }

        internal double NormalEnd { get; }

        internal double AxisCenter => (AxisStart + AxisEnd) * 0.5;

        internal double NormalCenter => (NormalStart + NormalEnd) * 0.5;

        internal double NormalSpan => Math.Max(NormalEnd - NormalStart, Tolerance);

        internal ProjectedTextBounds Translate(double axisDistance, double normalDistance) =>
            new(
                AxisStart + axisDistance,
                AxisEnd + axisDistance,
                NormalStart + normalDistance,
                NormalEnd + normalDistance);

        internal bool NormalOverlaps(ProjectedTextBounds other, double gap)
        {
            gap = Math.Max(gap, 0.0);
            return NormalStart < other.NormalEnd + gap - Tolerance &&
                   NormalEnd > other.NormalStart - gap + Tolerance;
        }

        internal bool Overlaps(ProjectedTextBounds other, double gap)
        {
            gap = Math.Max(gap, 0.0);
            return AxisStart < other.AxisEnd + gap - Tolerance &&
                   AxisEnd > other.AxisStart - gap + Tolerance &&
                   NormalOverlaps(other, gap);
        }

        internal double OverlapArea(ProjectedTextBounds other, double gap)
        {
            gap = Math.Max(gap, 0.0);
            var axisOverlap = Math.Min(AxisEnd, other.AxisEnd + gap) - Math.Max(AxisStart, other.AxisStart - gap);
            var normalOverlap = Math.Min(NormalEnd, other.NormalEnd + gap) - Math.Max(NormalStart, other.NormalStart - gap);
            if (axisOverlap <= 0.0 || normalOverlap <= 0.0)
                return 0.0;
            return axisOverlap * normalOverlap;
        }

        internal double MaxCornerDistanceSquared(double centerAxis, double centerNormal)
        {
            var axisDistance = Math.Max(
                Math.Abs(AxisStart - centerAxis),
                Math.Abs(AxisEnd - centerAxis));
            var normalDistance = Math.Max(
                Math.Abs(NormalStart - centerNormal),
                Math.Abs(NormalEnd - centerNormal));
            return (axisDistance * axisDistance) + (normalDistance * normalDistance);
        }
    }

    internal readonly struct PlacementChoice
    {
        internal PlacementChoice(double axisOffset, double normalOffset, int laneOffset)
        {
            AxisOffset = axisOffset;
            NormalOffset = normalOffset;
            LaneOffset = laneOffset;
        }

        internal double AxisOffset { get; }

        internal double NormalOffset { get; }

        internal int LaneOffset { get; }

        internal bool IsCloserThan(PlacementChoice other)
        {
            var distance = (AxisOffset * AxisOffset) + (NormalOffset * NormalOffset);
            var otherDistance = (other.AxisOffset * other.AxisOffset) +
                                (other.NormalOffset * other.NormalOffset);
            if (Math.Abs(distance - otherDistance) > Tolerance)
                return distance < otherDistance;
            if (Math.Abs(AxisOffset) != Math.Abs(other.AxisOffset))
                return Math.Abs(AxisOffset) < Math.Abs(other.AxisOffset);
            return LaneOffset > other.LaneOffset;
        }
    }

    internal sealed class PlanResult
    {
        internal PlanResult(
            double[] axisOffsets,
            double[] normalOffsets,
            int[] laneOffsets,
            bool[] conflictFlags)
        {
            AxisOffsets = axisOffsets;
            NormalOffsets = normalOffsets;
            LaneOffsets = laneOffsets;
            ConflictFlags = conflictFlags;
        }

        internal double[] AxisOffsets { get; }

        internal double[] NormalOffsets { get; }

        internal int[] LaneOffsets { get; }

        internal bool[] ConflictFlags { get; }

        internal bool HasConflicts => HasAnyConflict(ConflictFlags);
    }

    private readonly struct CandidateScore
    {
        internal CandidateScore(
            PlacementChoice choice,
            int collisionCount,
            double overlapArea)
        {
            Choice = choice;
            CollisionCount = collisionCount;
            OverlapArea = overlapArea;
            DistanceSquared = (choice.AxisOffset * choice.AxisOffset) +
                              (choice.NormalOffset * choice.NormalOffset);
        }

        internal PlacementChoice Choice { get; }

        internal int CollisionCount { get; }

        internal double OverlapArea { get; }

        internal double DistanceSquared { get; }

        internal bool IsBetterThan(CandidateScore other)
        {
            if (CollisionCount != other.CollisionCount)
                return CollisionCount < other.CollisionCount;
            if (Math.Abs(OverlapArea - other.OverlapArea) > Tolerance)
                return OverlapArea < other.OverlapArea;
            if (Math.Abs(DistanceSquared - other.DistanceSquared) > Tolerance)
                return DistanceSquared < other.DistanceSquared;
            if (Math.Abs(Choice.AxisOffset) != Math.Abs(other.Choice.AxisOffset))
                return Math.Abs(Choice.AxisOffset) < Math.Abs(other.Choice.AxisOffset);
            return Choice.LaneOffset > other.Choice.LaneOffset;
        }
    }
}
