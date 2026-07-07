using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    private sealed class QuickSelectionGeometry
    {
        internal QuickSelectionGeometry(
            double elevation,
            IReadOnlyList<QuickSegment> openSegments,
            IReadOnlyList<QuickClosedLoop> closedLoops)
        {
            Elevation = elevation;
            OpenSegments = openSegments;
            ClosedLoops = closedLoops;

            var hasPoint = false;
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            for (var i = 0; i < openSegments.Count; i++)
            {
                AccumulatePoint(openSegments[i].StartPoint);
                AccumulatePoint(openSegments[i].EndPoint);
            }

            for (var i = 0; i < closedLoops.Count; i++)
            {
                for (var vertexIndex = 0; vertexIndex < closedLoops[i].Vertices.Count; vertexIndex++)
                    AccumulatePoint(closedLoops[i].Vertices[vertexIndex]);
            }

            if (!hasPoint)
            {
                CenterPoint = Point2d.Origin;
                BoundsDiagonal = 0.0;
                return;
            }

            CenterPoint = new Point2d((minX + maxX) * 0.5, (minY + maxY) * 0.5);
            var width = maxX - minX;
            var height = maxY - minY;
            BoundsDiagonal = Math.Sqrt((width * width) + (height * height));
            return;

            void AccumulatePoint(Point2d point)
            {
                if (!hasPoint)
                {
                    minX = maxX = point.X;
                    minY = maxY = point.Y;
                    hasPoint = true;
                    return;
                }

                if (point.X < minX) minX = point.X;
                if (point.Y < minY) minY = point.Y;
                if (point.X > maxX) maxX = point.X;
                if (point.Y > maxY) maxY = point.Y;
            }
        }

        internal double Elevation { get; }

        internal IReadOnlyList<QuickSegment> OpenSegments { get; }

        internal IReadOnlyList<QuickClosedLoop> ClosedLoops { get; }

        internal Point2d CenterPoint { get; }

        internal double BoundsDiagonal { get; }

        internal Point3d CenterPoint3d => new(CenterPoint.X, CenterPoint.Y, Elevation);
    }

    internal sealed class QuickSegment
    {
        internal QuickSegment(ObjectId sourceEntityId, double elevation, Point2d startPoint, Point2d endPoint)
        {
            SourceEntityId = sourceEntityId;
            Elevation = elevation;
            StartPoint = startPoint;
            EndPoint = endPoint;
            Length = startPoint.GetDistanceTo(endPoint);
            MidPoint = new Point2d((startPoint.X + endPoint.X) * 0.5, (startPoint.Y + endPoint.Y) * 0.5);

            var tangent = startPoint.GetVectorTo(endPoint);
            if (tangent.Length <= PointTolerance)
            {
                CanonicalTangent = Vector2d.XAxis;
                return;
            }

            tangent = tangent.GetNormal();
            if (tangent.X < -PointTolerance || (Math.Abs(tangent.X) <= PointTolerance && tangent.Y < 0))
                tangent = -tangent;

            CanonicalTangent = tangent;
        }

        internal ObjectId SourceEntityId { get; }

        internal double Elevation { get; }

        internal Point2d StartPoint { get; }

        internal Point2d EndPoint { get; }

        internal double Length { get; }

        internal Point2d MidPoint { get; }

        internal Vector2d CanonicalTangent { get; }
    }

    private sealed class QuickClosedLoop
    {
        internal QuickClosedLoop(ObjectId sourceEntityId, double elevation, IReadOnlyList<Point2d> vertices)
        {
            SourceEntityId = sourceEntityId;
            Elevation = elevation;
            Vertices = vertices;

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

            BoundsWidth = maxX - minX;
            BoundsHeight = maxY - minY;
            CenterPoint = new Point2d((minX + maxX) * 0.5, (minY + maxY) * 0.5);
        }

        internal ObjectId SourceEntityId { get; }

        internal double Elevation { get; }

        internal IReadOnlyList<Point2d> Vertices { get; }

        internal double BoundsWidth { get; }

        internal double BoundsHeight { get; }

        internal Point2d CenterPoint { get; }

        internal Point3d CenterPoint3d => new(CenterPoint.X, CenterPoint.Y, Elevation);
    }

    private sealed class QuickWallGuideCandidate
    {
        internal QuickWallGuideCandidate(QuickSegment innerSegment, QuickSegment outerSegment, double wallThickness)
        {
            InnerSegment = innerSegment;
            OuterSegment = outerSegment;
            WallThickness = wallThickness;

            var axis = innerSegment.CanonicalTangent;
            var normal = new Vector2d(-axis.Y, axis.X);
            if (GeometryHelpers.DotProduct(innerSegment.MidPoint, outerSegment.MidPoint, normal) < 0)
            {
                axis = -axis;
                normal = -normal;
            }

            FacadeAxis = axis;
            OutwardNormal = normal;
            InnerMinProjection = GeometryHelpers.GetPointProjection(innerSegment.StartPoint, axis);
            InnerMaxProjection = GeometryHelpers.GetPointProjection(innerSegment.EndPoint, axis);
            InnerStartPoint = innerSegment.StartPoint;
            InnerEndPoint = innerSegment.EndPoint;
            if (InnerMinProjection > InnerMaxProjection)
            {
                (InnerMinProjection, InnerMaxProjection) = (InnerMaxProjection, InnerMinProjection);
                (InnerStartPoint, InnerEndPoint) = (InnerEndPoint, InnerStartPoint);
            }

            OuterMinProjection = GeometryHelpers.GetPointProjection(outerSegment.StartPoint, axis);
            OuterMaxProjection = GeometryHelpers.GetPointProjection(outerSegment.EndPoint, axis);
            OuterStartPoint = outerSegment.StartPoint;
            OuterEndPoint = outerSegment.EndPoint;
            if (OuterMinProjection > OuterMaxProjection)
            {
                (OuterMinProjection, OuterMaxProjection) = (OuterMaxProjection, OuterMinProjection);
                (OuterStartPoint, OuterEndPoint) = (OuterEndPoint, OuterStartPoint);
            }

            FacadeOffset = GeometryHelpers.GetPointProjection(GeometryHelpers.AveragePoint(innerSegment.MidPoint, outerSegment.MidPoint), normal);
        }

        internal QuickSegment InnerSegment { get; }

        internal QuickSegment OuterSegment { get; }

        internal double WallThickness { get; }

        internal Vector2d FacadeAxis { get; }

        internal Vector2d OutwardNormal { get; }

        internal double FacadeOffset { get; }

        internal double InnerMinProjection { get; }

        internal double InnerMaxProjection { get; }

        internal double OuterMinProjection { get; }

        internal double OuterMaxProjection { get; }

        internal Point2d InnerStartPoint { get; }

        internal Point2d InnerEndPoint { get; }

        internal Point2d OuterStartPoint { get; }

        internal Point2d OuterEndPoint { get; }

        internal string FacadeKey(double estimatedWallThickness)
        {
            var angle = Math.Atan2(FacadeAxis.Y, FacadeAxis.X);
            if (angle < 0)
                angle += Math.PI * 2.0;

            var angleBucket = (int)Math.Round(angle / (Math.PI / 36.0));
            var offsetTolerance = Math.Max(estimatedWallThickness * 0.75, 30.0);
            var offsetBucket = (int)Math.Round(FacadeOffset / offsetTolerance);
            return angleBucket.ToString(CultureInfo.InvariantCulture) + "|" +
                   offsetBucket.ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed class QuickDoorCandidate
    {
        internal QuickDoorCandidate(
            int number,
            double elevation,
            ObjectId leftSourceEntityId,
            ObjectId rightSourceEntityId,
            Point2d leftInnerPoint,
            Point2d leftOuterPoint,
            Point2d rightInnerPoint,
            Point2d rightOuterPoint,
            Point2d openingCenter,
            Point2d labelPosition)
        {
            Number = number;
            Elevation = elevation;
            LeftSourceEntityId = leftSourceEntityId;
            RightSourceEntityId = rightSourceEntityId;
            LeftInnerPoint = leftInnerPoint;
            LeftOuterPoint = leftOuterPoint;
            RightInnerPoint = rightInnerPoint;
            RightOuterPoint = rightOuterPoint;
            OpeningCenter = openingCenter;
            LabelPosition = labelPosition;
        }

        internal int Number { get; }

        internal double Elevation { get; }

        internal ObjectId LeftSourceEntityId { get; }

        internal ObjectId RightSourceEntityId { get; }

        internal Point2d LeftInnerPoint { get; }

        internal Point2d LeftOuterPoint { get; }

        internal Point2d RightInnerPoint { get; }

        internal Point2d RightOuterPoint { get; }

        internal Point2d OpeningCenter { get; }

        internal Point2d LabelPosition { get; }

        internal Point3d OpeningCenter3d => new(OpeningCenter.X, OpeningCenter.Y, Elevation);

        internal Point3d LabelPosition3d => new(LabelPosition.X, LabelPosition.Y, Elevation);
    }
}
