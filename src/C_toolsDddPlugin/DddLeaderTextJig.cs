using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

/// <summary>
/// DrawJig preview for leader text placement.
/// </summary>
internal sealed class DddLeaderTextJig : DrawJig
{
    private readonly MLeader _previewMLeader;
    private readonly Point3d _arrowPoint;
    private readonly int _leaderIndex;
    private readonly int _leaderLineIndex;
    private readonly TextAttachmentType _styleLeftAttach;
    private readonly TextAttachmentType _styleRightAttach;
    private int _landingSideSign;
    private Point3d _landingPoint;

    internal DddLeaderTextJig(MLeader previewMLeader, Point3d arrowPoint, int leaderIndex, int leaderLineIndex)
    {
        _previewMLeader = previewMLeader;
        _arrowPoint = arrowPoint;
        _leaderIndex = leaderIndex;
        _leaderLineIndex = leaderLineIndex;
        _landingSideSign = 1;
        _landingPoint = arrowPoint;
        DddLeaderInsertService.TryCaptureStyleTextAttachments(previewMLeader, out _styleLeftAttach, out _styleRightAttach);
    }

    internal Point3d LandingPoint => _landingPoint;
    internal int LandingSideSign => _landingSideSign;

    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        var options = new JigPromptPointOptions("\nC_TOOL：指定引线拐点（箭头已固定，移动预览完整引线，单击确认）: ")
        {
            BasePoint = _arrowPoint,
            UseBasePoint = true,
            Cursor = CursorType.RubberBand,
            UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.GovernedByOrthoMode
        };

        var ppr = prompts.AcquirePoint(options);
        if (ppr.Status != PromptStatus.OK)
            return SamplerStatus.Cancel;

        if (ppr.Value.IsEqualTo(_landingPoint))
            return SamplerStatus.NoChange;

        _landingPoint = ppr.Value;
        return SamplerStatus.OK;
    }

    protected override bool WorldDraw(WorldDraw draw)
    {
        try
        {
            DddLeaderInsertService.RestoreLeftRightTextAttachmentOverrides(_previewMLeader, _styleLeftAttach, _styleRightAttach);

            var mt = _previewMLeader.MText;
            if (mt != null)
            {
                try
                {
                    if (_previewMLeader.TextHeight > 1e-9)
                        mt.TextHeight = _previewMLeader.TextHeight;
                }
                catch (System.Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal("DddLeaderTextJig.TextHeight", ex);
                }

                _previewMLeader.MText = mt;
            }

            _landingSideSign = DddLeaderInsertService.ResolveLandingSideSign(
                _previewMLeader,
                _arrowPoint,
                _landingPoint,
                _landingSideSign);

            DddLeaderInsertService.ApplyLandingGeometry(
                _previewMLeader,
                _leaderIndex,
                _leaderLineIndex,
                _arrowPoint,
                _landingPoint,
                _landingSideSign);

            using (var clone = _previewMLeader.Clone() as MLeader)
            {
                if (clone == null)
                    return false;

                draw.Geometry.Draw(clone);
            }

            return true;
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("DddLeaderTextJig.WorldDraw", ex);
            return false;
        }
    }
}
