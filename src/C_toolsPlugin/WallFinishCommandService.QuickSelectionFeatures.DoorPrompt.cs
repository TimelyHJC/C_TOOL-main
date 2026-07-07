using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    private static partial class QuickSelectionFeatureBuilder
    {
        internal static bool TryPromptStorefrontDoorNumbers(
            Document doc,
            IReadOnlyList<QuickDoorCandidate> doorCandidates,
            double estimatedWallThickness,
            out HashSet<int> storefrontNumbers,
            out string error,
            out bool canceled)
        {
            storefrontNumbers = new HashSet<int>();
            error = "";
            canceled = false;

            if (doorCandidates.Count == 0)
                return true;

            var labelIds = Array.Empty<ObjectId>();
            try
            {
                labelIds = CreateTemporaryDoorLabels(doc, doorCandidates, estimatedWallThickness);
                if (labelIds.Length > 0)
                    doc.Editor.Regen();

                while (true)
                {
                    var options = new PromptStringOptions("\nC_TOOL：选择门头编号，空格分隔，直接回车跳过")
                    {
                        AllowSpaces = true
                    };

                    var result = doc.Editor.GetString(options);
                    if (result.Status == PromptStatus.Cancel)
                    {
                        canceled = true;
                        return false;
                    }

                    if (result.Status == PromptStatus.None)
                        return true;

                    if (result.Status != PromptStatus.OK)
                        return true;

                    if (TryParseStorefrontDoorNumbers(
                            result.StringResult,
                            doorCandidates.Count,
                            out storefrontNumbers,
                            out error))
                    {
                        return true;
                    }

                    doc.Editor.WriteMessage("\nC_TOOL：" + error);
                }
            }
            finally
            {
                EraseTemporaryEntities(doc, labelIds);
                if (labelIds.Length > 0)
                    doc.Editor.Regen();
            }
        }

        private static bool TryParseStorefrontDoorNumbers(
            string? rawInput,
            int maxNumber,
            out HashSet<int> storefrontNumbers,
            out string error)
        {
            storefrontNumbers = new HashSet<int>();
            error = "";

            var normalized = (rawInput ?? "")
                .Replace('，', ' ')
                .Replace(',', ' ')
                .Replace('、', ' ')
                .Replace(';', ' ')
                .Replace('；', ' ');
            var tokens = normalized
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return true;

            for (var i = 0; i < tokens.Length; i++)
            {
                if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    error = "门头编号仅支持数字，多个编号请用空格分隔。";
                    return false;
                }

                if (number < 1 || number > maxNumber)
                {
                    error = $"门头编号需在 1 到 {maxNumber} 之间。";
                    return false;
                }

                storefrontNumbers.Add(number);
            }

            return true;
        }

        private static ObjectId[] CreateTemporaryDoorLabels(
            Document doc,
            IReadOnlyList<QuickDoorCandidate> doorCandidates,
            double estimatedWallThickness)
        {
            var labelIds = new List<ObjectId>(doorCandidates.Count);
            CadDatabaseScope.Write(
                doc,
                (db, tr) =>
                {
                    var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(db, tr);
                    var textHeight = Math.Max(estimatedWallThickness * 0.8, 150.0);

                    for (var i = 0; i < doorCandidates.Count; i++)
                    {
                        var candidate = doorCandidates[i];
                        var position = candidate.LabelPosition3d;
                        var text = new DBText();
                        text.SetDatabaseDefaults(db);
                        text.TextString = candidate.Number.ToString(CultureInfo.InvariantCulture);
                        text.Height = textHeight;
                        text.Justify = AttachmentPoint.MiddleCenter;
                        text.AlignmentPoint = position;
                        text.Position = position;
                        text.Color = Color.FromColorIndex(ColorMethod.ByAci, QuickDoorLabelColorIndex);
                        if (!db.Textstyle.IsNull)
                            text.TextStyleId = db.Textstyle;

                        currentSpace.AppendEntity(text);
                        tr.AddNewlyCreatedDBObject(text, true);
                        text.AdjustAlignment(db);
                        labelIds.Add(text.ObjectId);
                    }
                },
                requireDocumentLock: true);

            return labelIds.ToArray();
        }

        private static void EraseTemporaryEntities(Document doc, IReadOnlyList<ObjectId> objectIds)
        {
            if (objectIds.Count == 0)
                return;

            try
            {
                CadDatabaseScope.Write(
                    doc,
                    (_, tr) =>
                    {
                        for (var i = 0; i < objectIds.Count; i++)
                        {
                            var objectId = objectIds[i];
                            if (objectId.IsNull || objectId.IsErased)
                                continue;

                            if (!CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForWrite, out var entity) ||
                                entity == null)
                            {
                                continue;
                            }

                            entity.Erase();
                        }
                    },
                    requireDocumentLock: true);
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_WCC 删除门头编号失败（无效操作）", ex);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_WCC 删除门头编号失败（CAD）", ex);
            }
        }
    }
}
