using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    internal static partial class OutlineBuilder
    {
        internal static bool PromptOffsetSideWithPreview(
            Document doc,
            SourceCurveSelection selection,
            ref WallFinishSettingsDto settings,
            out WallFinishDirectionSelection directionSelection)
        {
            directionSelection = WallFinishDirectionSelection.Empty;
            var ed = doc.Editor;

            while (true)
            {
                var previewItems = SettingsManager.BuildPreviewItems(doc, selection, settings);
                using var jig = new WallFinishPreviewJig(
                    previewItems,
                    settings.OffsetDistance,
                    selection.UseRecognizedFigureDirection,
                    selection.PreviewBasePoint);
                var dragResult = ed.Drag(jig);
                if (jig.RequestedSettings)
                {
                    SettingsManager.ShowSettingsDialog(doc, ref settings);
                    continue;
                }

                if (dragResult.Status != PromptStatus.OK)
                    return false;

                if (!jig.TryGetResolvedDirection(out directionSelection, out var error))
                {
                    ed.WriteMessage("\nC_TOOL：" + error);
                    continue;
                }

                return true;
            }
        }

        internal static WallFinishCreateResult CreateWallFinishOutlines(
            Document doc,
            SourceCurveSelection selection,
            WallFinishSettingsDto settings,
            double offsetDistance,
            WallFinishDirectionSelection directionSelection)
        {
            var layerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var createdCount = 0;

            CadDatabaseScope.Write(
                doc,
                (db, tr) =>
                {
                    var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(db, tr);
                    for (var i = 0; i < selection.GuideSelections.Count; i++)
                    {
                        var guideSelection = selection.GuideSelections[i];
                        var sourceEntity = CadDatabaseScope.OpenAs<Entity>(tr, guideSelection.PropertySourceEntityId, OpenMode.ForRead);
                        var resolvedSettings = SettingsManager.ResolveSettings((sourceEntity.Layer ?? "").Trim(), settings);
                        var hatchInnerLoopIds = GetClosedLoopHatchInnerLoopIds(sourceEntity, guideSelection);
                        var sideSign = SettingsManager.ResolveSideSign(
                            guideSelection.GuidePolyline,
                            directionSelection,
                            guideSelection.RecognizedInteriorSideSign);
                        if (!TryBuildOutline(guideSelection.GuidePolyline, sideSign, offsetDistance, out var outline, out var error) ||
                            outline == null)
                        {
                            throw new InvalidOperationException(error);
                        }

                        using (outline)
                        {
                            var hatchColor = SettingsManager.ResolveHatchEntityColor(tr, db, resolvedSettings);
                            var targetLayerId = SettingsManager.EnsureLayer(
                                tr,
                                db,
                                resolvedSettings.TargetLayerName,
                                resolvedSettings.TargetLayerEntry,
                                resolvedSettings.TargetLayerColorIndex);
                            outline.SetDatabaseDefaults(db);
                            outline.SetPropertiesFrom(sourceEntity);
                            outline.LayerId = targetLayerId;
                            SettingsManager.SetEntityColorByLayer(outline);
                            outline.LineWeight = LineWeight.ByLayer;
                            outline.Closed = true;

                            currentSpace.AppendEntity(outline);
                            tr.AddNewlyCreatedDBObject(outline, true);
                            CreateWallFinishHatchEntity(tr, db, currentSpace, outline, hatchInnerLoopIds, resolvedSettings, hatchColor);
                            layerNames.Add(resolvedSettings.TargetLayerName);
                            createdCount++;
                        }
                    }
                },
                requireDocumentLock: true);

            return new WallFinishCreateResult(createdCount, SettingsManager.FormatLayerSummary(layerNames));
        }

        private static void CreateWallFinishHatchEntity(
            Transaction tr,
            Database db,
            BlockTableRecord currentSpace,
            Polyline outline,
            ObjectIdCollection? hatchInnerLoopIds,
            WallFinishResolvedSettings settings,
            Color? hatchColor)
        {
            var hatch = new Hatch();
            hatch.SetDatabaseDefaults(outline.Database);
            var hatchLayerId = SettingsManager.EnsureLayer(
                tr,
                db,
                settings.HatchLayerName,
                settings.HatchLayerEntry,
                layerColorIndex: null);
            hatch.LayerId = hatchLayerId;
            hatch.Normal = outline.Normal;
            hatch.Elevation = outline.Elevation;
            SettingsManager.ApplyEntityColor(hatch, hatchColor);

            currentSpace.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);

            SettingsManager.ApplyHatchStyle(hatch, settings.HatchStyle);
            hatch.Associative = false;

            var loopIds = new ObjectIdCollection();
            loopIds.Add(outline.ObjectId);
            hatch.AppendLoop(HatchLoopTypes.Outermost, loopIds);
            if (hatchInnerLoopIds is { Count: > 0 })
            {
                hatch.AppendLoop(HatchLoopTypes.Default, hatchInnerLoopIds);
            }

            hatch.EvaluateHatch(true);
            SettingsManager.MoveEntityToBottom(tr, currentSpace, hatch.ObjectId);
        }

        private static ObjectIdCollection? GetClosedLoopHatchInnerLoopIds(Entity sourceEntity, GuideChainSelection guideSelection)
        {
            if (!guideSelection.GuidePolyline.Closed)
                return null;

            if (guideSelection.HatchBoundaryEntityIds.Count > 0)
            {
                var innerLoopIds = new ObjectIdCollection();
                for (var i = 0; i < guideSelection.HatchBoundaryEntityIds.Count; i++)
                    innerLoopIds.Add(guideSelection.HatchBoundaryEntityIds[i]);

                return innerLoopIds.Count > 0 ? innerLoopIds : null;
            }

            if (sourceEntity is not Polyline sourcePolyline ||
                !sourcePolyline.Closed ||
                !GeometryHelpers.IsWorldXyPlane(sourcePolyline.Normal) ||
                Math.Abs(sourcePolyline.Elevation - guideSelection.GuidePolyline.Elevation) > PointTolerance ||
                GeometryHelpers.HasArcSegments(sourcePolyline))
            {
                return null;
            }

            var hatchBoundaryEntityIds = new ObjectIdCollection();
            hatchBoundaryEntityIds.Add(sourcePolyline.ObjectId);
            return hatchBoundaryEntityIds;
        }
    }
}
