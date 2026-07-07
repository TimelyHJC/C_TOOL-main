using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsDddPlugin;

/// <summary>
/// 标注拖拽服务的枚举、结构体和数据类。
/// </summary>
internal enum SupportedDimensionKind
{
    Rotated,
    Aligned
}

internal enum SupportedLeaderKind
{
    MLeader,
    Leader
}

internal enum ShiftSeedKind
{
    Unknown,
    Dimension,
    Text,
    Leader,
    MLeader
}

internal enum DimensionGroupingMode
{
    Single,
    ContinuousChain,
    SelectedDimensions
}

internal enum ShiftDirection
{
    Vertical,
    Horizontal
}

internal readonly struct ShiftCommandContext
{
    internal ShiftCommandContext(ShiftDirection direction, string commandName, string axisLabel)
    {
        Direction = direction;
        CommandName = commandName;
        AxisLabel = axisLabel;
    }

    internal ShiftDirection Direction { get; }

    internal string CommandName { get; }

    internal string AxisLabel { get; }

    internal bool IsHorizontal => Direction == ShiftDirection.Horizontal;
}

internal readonly struct LeaderLineInfo
{
    internal LeaderLineInfo(int leaderIndex, int leaderLineIndex, Point3d arrowPoint, Point3d landingPoint)
    {
        LeaderIndex = leaderIndex;
        LeaderLineIndex = leaderLineIndex;
        ArrowPoint = arrowPoint;
        LandingPoint = landingPoint;
    }

    internal int LeaderIndex { get; }

    internal int LeaderLineIndex { get; }

    internal Point3d ArrowPoint { get; }

    internal Point3d LandingPoint { get; }
}

internal readonly struct DimensionInfo
{
    internal DimensionInfo(
        ObjectId dimensionId,
        SupportedDimensionKind kind,
        Point3d firstPoint,
        Point3d secondPoint,
        Point3d dimLinePoint,
        Vector3d axis,
        Vector3d referenceNormal,
        bool usesDefaultTextPosition,
        Point3d textPosition,
        Dimension previewTemplate)
    {
        DimensionId = dimensionId;
        Kind = kind;
        FirstPoint = firstPoint;
        SecondPoint = secondPoint;
        DimLinePoint = dimLinePoint;
        Axis = axis;
        ReferenceNormal = referenceNormal;
        UsesDefaultTextPosition = usesDefaultTextPosition;
        TextPosition = textPosition;
        PreviewTemplate = previewTemplate;
    }

    internal ObjectId DimensionId { get; }

    internal SupportedDimensionKind Kind { get; }

    internal Point3d FirstPoint { get; }

    internal Point3d SecondPoint { get; }

    internal Point3d DimLinePoint { get; }

    internal Vector3d Axis { get; }

    internal Vector3d ReferenceNormal { get; }

    internal bool UsesDefaultTextPosition { get; }

    internal Point3d TextPosition { get; }

    internal Dimension PreviewTemplate { get; }
}

internal sealed class DimensionShiftItem : IDisposable
{
    internal DimensionShiftItem(
        ObjectId dimensionId,
        SupportedDimensionKind kind,
        Point3d originalDimLinePoint,
        bool usesDefaultTextPosition,
        Point3d originalTextPosition,
        Dimension previewTemplate)
    {
        DimensionId = dimensionId;
        Kind = kind;
        OriginalDimLinePoint = originalDimLinePoint;
        UsesDefaultTextPosition = usesDefaultTextPosition;
        OriginalTextPosition = originalTextPosition;
        PreviewTemplate = previewTemplate;
    }

    internal ObjectId DimensionId { get; }

    internal SupportedDimensionKind Kind { get; }

    internal Point3d OriginalDimLinePoint { get; }

    internal bool UsesDefaultTextPosition { get; }

    internal Point3d OriginalTextPosition { get; }

    internal Dimension PreviewTemplate { get; }

    public void Dispose() => PreviewTemplate.Dispose();
}

internal sealed class LeaderShiftItem : IDisposable
{
    internal LeaderShiftItem(
        SupportedLeaderKind kind,
        ObjectId leaderId,
        Point3d basePoint,
        Vector3d moveNormal,
        Point3d originalTextLocation,
        IReadOnlyList<LeaderLineInfo> lines,
        Point3d[] originalLeaderVertices,
        ObjectId annotationId,
        Entity previewTemplate,
        Entity? annotationPreviewTemplate)
    {
        Kind = kind;
        LeaderId = leaderId;
        BasePoint = basePoint;
        MoveNormal = moveNormal;
        OriginalTextLocation = originalTextLocation;
        Lines = lines;
        OriginalLeaderVertices = originalLeaderVertices;
        AnnotationId = annotationId;
        PreviewTemplate = previewTemplate;
        AnnotationPreviewTemplate = annotationPreviewTemplate;
    }

    internal SupportedLeaderKind Kind { get; }

    internal ObjectId LeaderId { get; }

    internal Point3d BasePoint { get; }

    internal Vector3d MoveNormal { get; }

    internal Point3d OriginalTextLocation { get; }

    internal IReadOnlyList<LeaderLineInfo> Lines { get; }

    internal Point3d[] OriginalLeaderVertices { get; }

    internal ObjectId AnnotationId { get; }

    internal Entity PreviewTemplate { get; }

    internal Entity? AnnotationPreviewTemplate { get; }

    internal IEnumerable<ObjectId> GetPreviewEntityIds()
    {
        yield return LeaderId;
        if (!AnnotationId.IsNull && !AnnotationId.IsErased)
            yield return AnnotationId;
    }

    public void Dispose()
    {
        PreviewTemplate.Dispose();
        AnnotationPreviewTemplate?.Dispose();
    }
}

internal sealed class TextShiftItem : IDisposable
{
    internal TextShiftItem(ObjectId textId, Point3d basePoint, Vector3d moveNormal, Entity previewTemplate)
    {
        TextId = textId;
        BasePoint = basePoint;
        MoveNormal = moveNormal;
        PreviewTemplate = previewTemplate;
    }

    internal ObjectId TextId { get; }

    internal Point3d BasePoint { get; }

    internal Vector3d MoveNormal { get; }

    internal Entity PreviewTemplate { get; }

    internal IEnumerable<ObjectId> GetPreviewEntityIds()
    {
        yield return TextId;
    }

    public void Dispose() => PreviewTemplate.Dispose();
}
