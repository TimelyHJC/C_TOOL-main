using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsDddPlugin;

[TestClass]
public sealed class DddDimensionTextAvoidancePlannerTests
{
    [TestMethod]
    public void Plan_DoesNotMoveTextWhenBoundsDoNotOverlap()
    {
        var result = Plan(2.0, Item(0.0, 2.0), Item(2.01, 4.01));

        CollectionAssert.AreEqual(new[] { 0.0, 0.0 }, result.AxisOffsets);
        CollectionAssert.AreEqual(new[] { 0, 0 }, result.LaneOffsets);
        Assert.IsFalse(result.HasConflicts);
    }

    [TestMethod]
    public void Plan_UsesNearestLeftOrRightMoveBeforeChangingLane()
    {
        var result = Plan(2.0, Item(0.0, 3.0), Item(2.0, 5.0));

        Assert.AreEqual(1, CountMoved(result));
        Assert.AreEqual(0, result.LaneOffsets[1]);
        Assert.IsTrue(Math.Abs(result.AxisOffsets[1]) > 0.0);
        Assert.IsTrue(Math.Abs(result.AxisOffsets[1]) <= 2.0);
    }

    [TestMethod]
    public void Plan_ChangesLaneWhenRequiredAxisMoveExceedsThreshold()
    {
        var result = Plan(1.0, Item(0.0, 4.0), Item(0.0, 4.0));

        Assert.AreEqual(0.0, result.AxisOffsets[1], 1e-6);
        Assert.AreEqual(1, Math.Abs(result.LaneOffsets[1]));
    }

    [TestMethod]
    public void Plan_CentersChangedLaneFromDimensionLineInsteadOfCurrentTextCenter()
    {
        var shiftedBounds = new DddDimensionTextAvoidancePlanner.ProjectedTextBounds(
            axisStart: 0.0,
            axisEnd: 4.0,
            normalStart: 5.0,
            normalEnd: 7.0);
        var first = new DddDimensionTextAvoidancePlanner.PlanItem(
            shiftedBounds,
            preferAnchor: true,
            circleCenterAxis: 2.0,
            lineNormal: 0.0,
            maxRadius: 20.0);
        var second = new DddDimensionTextAvoidancePlanner.PlanItem(
            shiftedBounds,
            preferAnchor: false,
            circleCenterAxis: 2.0,
            lineNormal: 0.0,
            maxRadius: 20.0);

        var result = Plan(1.0, first, second);

        var targetNormalCenter = shiftedBounds.NormalCenter + result.NormalOffsets[1];
        Assert.AreEqual(
            result.LaneOffsets[1] * 3.0,
            targetNormalCenter,
            1e-6);
    }

    [TestMethod]
    public void Plan_AfterChangingLaneUsesBoundedAxisMoveWithinThatLane()
    {
        var result = Plan(
            2.0,
            Item(-10.0, 10.0, preferAnchor: true),
            Item(-1.0, 1.0),
            Item(-1.0, 1.0),
            Item(0.5, 4.0));

        Assert.AreEqual(1, Math.Abs(result.LaneOffsets[3]));
        Assert.IsTrue(Math.Abs(result.AxisOffsets[3]) > 0.0);
        Assert.IsTrue(Math.Abs(result.AxisOffsets[3]) <= 2.0);
    }

    [TestMethod]
    public void Plan_AlternatesNearbyLanesForChainConflictWhenHorizontalThresholdIsSmall()
    {
        var result = Plan(
            0.25,
            Item(0.0, 3.0),
            Item(2.0, 5.0),
            Item(4.0, 7.0),
            Item(6.0, 9.0));

        Assert.AreEqual(2, CountMoved(result));
        Assert.AreEqual(1, MaxAbsLane(result.LaneOffsets));
    }

    [TestMethod]
    public void Plan_NeverExceedsConfiguredAxisOrLaneLimits()
    {
        var result = Plan(
            1.25,
            Item(0.0, 5.0),
            Item(0.0, 5.0),
            Item(0.0, 5.0),
            Item(0.0, 5.0),
            Item(0.0, 5.0),
            Item(0.0, 5.0));

        foreach (var offset in result.AxisOffsets)
            Assert.IsTrue(Math.Abs(offset) <= 1.25 + 1e-6);
        Assert.IsTrue(MaxAbsLane(result.LaneOffsets) <= 2);
    }

    [TestMethod]
    public void Plan_RejectsCandidatesWhoseTextBoundsLeaveAllowedCircle()
    {
        var first = Item(0.0, 4.0, preferAnchor: true);
        var constrainedBounds = new DddDimensionTextAvoidancePlanner.ProjectedTextBounds(
            axisStart: 0.0,
            axisEnd: 4.0,
            normalStart: -1.0,
            normalEnd: 1.0);
        var originalRadius = Math.Sqrt(constrainedBounds.MaxCornerDistanceSquared(2.0, 0.0));
        var constrained = new DddDimensionTextAvoidancePlanner.PlanItem(
            constrainedBounds,
            preferAnchor: false,
            circleCenterAxis: 2.0,
            lineNormal: 0.0,
            maxRadius: originalRadius + 0.1);

        var result = Plan(2.0, first, constrained);
        var movedBounds = constrainedBounds.Translate(result.AxisOffsets[1], result.NormalOffsets[1]);

        Assert.IsTrue(
            movedBounds.MaxCornerDistanceSquared(2.0, 0.0) <=
            ((originalRadius + 0.1) * (originalRadius + 0.1)) + 1e-6);
    }

    [TestMethod]
    public void Plan_PreservesPreferredAnchorDuringConflict()
    {
        var result = Plan(
            2.0,
            Item(0.0, 4.0, preferAnchor: false),
            Item(1.0, 5.0, preferAnchor: true));

        Assert.IsTrue(IsMoved(result, 0));
        Assert.IsFalse(IsMoved(result, 1));
    }

    private static DddDimensionTextAvoidancePlanner.PlanResult Plan(
        double maxAxisShift,
        params DddDimensionTextAvoidancePlanner.PlanItem[] items) =>
        DddDimensionTextAvoidancePlanner.Plan(
            items,
            laneSpacing: 3.0,
            maxLaneIndex: 2,
            maxAxisShift,
            placementGap: 0.2);

    private static DddDimensionTextAvoidancePlanner.PlanItem Item(
        double axisStart,
        double axisEnd,
        bool preferAnchor = false) =>
        new(
            new DddDimensionTextAvoidancePlanner.ProjectedTextBounds(
                axisStart,
                axisEnd,
                normalStart: -1.0,
                normalEnd: 1.0),
            preferAnchor,
            circleCenterAxis: (axisStart + axisEnd) * 0.5,
            lineNormal: 0.0,
            maxRadius: 100.0);

    private static int CountMoved(DddDimensionTextAvoidancePlanner.PlanResult result)
    {
        var count = 0;
        for (var index = 0; index < result.LaneOffsets.Length; index++)
        {
            if (IsMoved(result, index))
                count++;
        }

        return count;
    }

    private static bool IsMoved(DddDimensionTextAvoidancePlanner.PlanResult result, int index) =>
        Math.Abs(result.AxisOffsets[index]) > 1e-6 ||
        Math.Abs(result.NormalOffsets[index]) > 1e-6;

    private static int MaxAbsLane(int[] offsets)
    {
        var max = 0;
        foreach (var offset in offsets)
            max = Math.Max(max, Math.Abs(offset));
        return max;
    }
}
