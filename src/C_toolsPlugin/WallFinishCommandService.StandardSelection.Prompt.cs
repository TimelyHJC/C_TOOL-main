using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    internal static partial class StandardSelectionBuilder
    {
        internal static SourcePromptStatus PromptSelectionIds(
            Document doc,
            ref WallFinishSettingsDto settings,
            out ObjectId[] entityIds)
        {
            var ed = doc.Editor;
            entityIds = Array.Empty<ObjectId>();

            while (true)
            {
                var currentSettings = settings;
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = SettingsManager.GetSelectionPromptMessage()
                };
                options.SetKeywords("[设置(S)]", SettingsKeyword);
                options.KeywordInput += (_, e) =>
                {
                    var keyword = SettingsManager.NormalizeKeyword(e.Input);
                    if (string.Equals(keyword, SettingsKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        SettingsManager.ShowSettingsDialog(doc, ref currentSettings);
                        options.MessageForAdding = SettingsManager.GetSelectionPromptMessage();
                        e.SetErrorMessage(SettingsManager.GetSelectionPromptMessage());
                    }
                };

                var result = ed.GetSelection(options);
                settings = currentSettings;
                if (result.Status == PromptStatus.OK && result.Value != null)
                {
                    entityIds = NormalizeSelectionIds(result.Value.GetObjectIds());
                    return entityIds.Length > 0
                        ? SourcePromptStatus.Success
                        : SourcePromptStatus.EndCommand;
                }

                if (result.Status == PromptStatus.Keyword)
                    continue;

                return result.Status == PromptStatus.Cancel
                    ? SourcePromptStatus.Cancel
                    : SourcePromptStatus.EndCommand;
            }
        }

        internal static ObjectId[] NormalizeSelectionIds(ObjectId[] rawIds)
        {
            if (rawIds.Length == 0)
                return Array.Empty<ObjectId>();

            var seen = new HashSet<ObjectId>();
            var ids = new List<ObjectId>(rawIds.Length);
            for (var i = 0; i < rawIds.Length; i++)
            {
                var objectId = rawIds[i];
                if (objectId.IsNull || !seen.Add(objectId))
                    continue;

                ids.Add(objectId);
            }

            return ids.Count == 0 ? Array.Empty<ObjectId>() : ids.ToArray();
        }
    }
}
