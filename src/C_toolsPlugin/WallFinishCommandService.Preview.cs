using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Gi = Autodesk.AutoCAD.GraphicsInterface;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    internal readonly struct WallFinishDirectionSelection
    {
        internal static WallFinishDirectionSelection Empty => new(false, false, Point3d.Origin);

        internal WallFinishDirectionSelection(
            bool useRecognizedFigureDirection,
            bool useInteriorSide,
            Point3d samplePoint)
        {
            UseRecognizedFigureDirection = useRecognizedFigureDirection;
            UseInteriorSide = useInteriorSide;
            SamplePoint = samplePoint;
        }

        internal bool UseRecognizedFigureDirection { get; }

        internal bool UseInteriorSide { get; }

        internal Point3d SamplePoint { get; }
    }

    internal readonly struct WallFinishPreviewItem
    {
        internal WallFinishPreviewItem(
            Polyline guidePolyline,
            int? previewColorIndex,
            int recognizedInteriorSideSign,
            bool useForDirectionResolution)
        {
            GuidePolyline = guidePolyline;
            PreviewColorIndex = previewColorIndex;
            RecognizedInteriorSideSign = recognizedInteriorSideSign < 0 ? -1 : 1;
            UseForDirectionResolution = useForDirectionResolution;
        }

        internal Polyline GuidePolyline { get; }

        internal int? PreviewColorIndex { get; }

        internal int RecognizedInteriorSideSign { get; }

        internal bool UseForDirectionResolution { get; }
    }

    private sealed class WallFinishPreviewJig : DrawJig, IDisposable
    {
        private readonly List<PreviewPolylineItem> _items;
        private readonly double _offsetDistance;
        private readonly bool _useRecognizedFigureDirection;
        private readonly Point3d _basePoint;
        private Point3d _cursorPoint;
        private bool _hasCursorPoint;

        internal WallFinishPreviewJig(
            IReadOnlyList<WallFinishPreviewItem> items,
            double offsetDistance,
            bool useRecognizedFigureDirection,
            Point3d basePoint)
        {
            _items = new List<PreviewPolylineItem>(items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                _items.Add(new PreviewPolylineItem(
                    GeometryHelpers.ClonePolyline(items[i].GuidePolyline),
                    items[i].PreviewColorIndex,
                    items[i].RecognizedInteriorSideSign,
                    items[i].UseForDirectionResolution));
            }

            _offsetDistance = offsetDistance;
            _useRecognizedFigureDirection = useRecognizedFigureDirection;
            _basePoint = basePoint;
        }

        internal bool RequestedSettings { get; private set; }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(
                "\nC_TOOL：指定方向 [设置(S)]",
                SettingsKeyword)
            {
                Cursor = CursorType.RubberBand,
                UseBasePoint = true,
                BasePoint = _basePoint,
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.GovernedByOrthoMode
            };

            var result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.Keyword)
            {
                RequestedSettings = true;
                return SamplerStatus.NoChange;
            }

            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            var elevation = _items.Count > 0 ? _items[0].GuidePolyline.Elevation : 0.0;
            var nextPoint = new Point3d(result.Value.X, result.Value.Y, elevation);
            if (_hasCursorPoint && nextPoint.DistanceTo(_cursorPoint) <= PointTolerance)
                return SamplerStatus.NoChange;

            _cursorPoint = nextPoint;
            _hasCursorPoint = true;
            return SamplerStatus.OK;
        }

        internal bool TryGetResolvedDirection(out WallFinishDirectionSelection directionSelection, out string error)
        {
            directionSelection = WallFinishDirectionSelection.Empty;
            error = "";

            if (!_hasCursorPoint)
            {
                error = "请移动鼠标指定完成面方向。";
                return false;
            }

            var direction = ResolveDirectionSelection();
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                var sideSign = direction.UseRecognizedFigureDirection
                    ? (direction.UseInteriorSide ? item.RecognizedInteriorSideSign : -item.RecognizedInteriorSideSign)
                    : GeometryHelpers.ResolveSideSign(item.GuidePolyline, _cursorPoint, 1);
                if (!OutlineBuilder.TryBuildOutline(item.GuidePolyline, sideSign, _offsetDistance, out var previewOutline, out error) ||
                    previewOutline == null)
                {
                    return false;
                }

                previewOutline.Dispose();
            }

            directionSelection = direction;
            return true;
        }

        protected override bool WorldDraw(Gi.WorldDraw draw)
        {
            try
            {
                for (var i = 0; i < _items.Count; i++)
                {
                    using var guideClone = GeometryHelpers.ClonePolyline(_items[i].GuidePolyline);
                    guideClone.ColorIndex = 8;
                    draw.Geometry.Draw(guideClone);
                }

                if (!_hasCursorPoint)
                    return true;

                var direction = ResolveDirectionSelection();
                for (var i = 0; i < _items.Count; i++)
                {
                    var item = _items[i];
                    var sideSign = direction.UseRecognizedFigureDirection
                        ? (direction.UseInteriorSide ? item.RecognizedInteriorSideSign : -item.RecognizedInteriorSideSign)
                        : GeometryHelpers.ResolveSideSign(item.GuidePolyline, _cursorPoint, 1);
                    if (!OutlineBuilder.TryBuildOutline(item.GuidePolyline, sideSign, _offsetDistance, out var previewOutline, out _) ||
                        previewOutline == null)
                    {
                        continue;
                    }

                    using (previewOutline)
                    {
                        previewOutline.ColorIndex = item.PreviewColorIndex is >= 1 and <= 255
                            ? (short)item.PreviewColorIndex.Value
                            : (short)3;
                        draw.Geometry.Draw(previewOutline);
                    }
                }

                return true;
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal($"{CommandName} 预览绘制失败（无效操作）", ex);
                return false;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal($"{CommandName} 预览绘制失败（CAD）", ex);
                return false;
            }
        }

        public void Dispose()
        {
            for (var i = 0; i < _items.Count; i++)
                _items[i].Dispose();
        }

        private WallFinishDirectionSelection ResolveDirectionSelection()
        {
            if (!_useRecognizedFigureDirection)
                return new WallFinishDirectionSelection(false, false, _cursorPoint);

            if (!TryGetNearestItemIndex(_cursorPoint, out var nearestItemIndex))
                return new WallFinishDirectionSelection(false, false, _cursorPoint);

            var nearestItem = _items[nearestItemIndex];
            var cursorSideSign = GeometryHelpers.ResolveSideSign(
                nearestItem.GuidePolyline,
                _cursorPoint,
                nearestItem.RecognizedInteriorSideSign);
            var useInteriorSide = cursorSideSign == nearestItem.RecognizedInteriorSideSign;
            return new WallFinishDirectionSelection(true, useInteriorSide, _cursorPoint);
        }

        private bool TryGetNearestItemIndex(Point3d samplePoint, out int nearestItemIndex)
        {
            return TryGetNearestItemIndex(samplePoint, referencesOnly: true, out nearestItemIndex) ||
                   TryGetNearestItemIndex(samplePoint, referencesOnly: false, out nearestItemIndex);
        }

        private bool TryGetNearestItemIndex(
            Point3d samplePoint,
            bool referencesOnly,
            out int nearestItemIndex)
        {
            nearestItemIndex = -1;
            var bestDistanceSquared = double.MaxValue;

            for (var i = 0; i < _items.Count; i++)
            {
                if (referencesOnly && !_items[i].UseForDirectionResolution)
                    continue;

                if (!GeometryHelpers.TryGetClosestSegment(_items[i].GuidePolyline, samplePoint, out _, out _, out var closestPoint))
                    continue;

                var dx = samplePoint.X - closestPoint.X;
                var dy = samplePoint.Y - closestPoint.Y;
                var dz = samplePoint.Z - closestPoint.Z;
                var distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
                if (distanceSquared >= bestDistanceSquared)
                    continue;

                bestDistanceSquared = distanceSquared;
                nearestItemIndex = i;
            }

            return nearestItemIndex >= 0;
        }
    }

    private sealed class PreviewPolylineItem : IDisposable
    {
        internal PreviewPolylineItem(
            Polyline guidePolyline,
            int? previewColorIndex,
            int recognizedInteriorSideSign,
            bool useForDirectionResolution)
        {
            GuidePolyline = guidePolyline;
            PreviewColorIndex = previewColorIndex;
            RecognizedInteriorSideSign = recognizedInteriorSideSign < 0 ? -1 : 1;
            UseForDirectionResolution = useForDirectionResolution;
        }

        internal Polyline GuidePolyline { get; }

        internal int? PreviewColorIndex { get; }

        internal int RecognizedInteriorSideSign { get; }

        internal bool UseForDirectionResolution { get; }

        public void Dispose()
        {
            GuidePolyline.Dispose();
        }
    }
}
