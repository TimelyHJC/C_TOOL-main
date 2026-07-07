namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    private static void DisposeGuideSelections(IReadOnlyList<GuideChainSelection> guideSelections)
    {
        for (var i = 0; i < guideSelections.Count; i++)
            guideSelections[i].Dispose();
    }
}
