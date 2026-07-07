using System;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsShared;

/// <summary>将插件 JSON 中的引线外观应用到 <see cref="MLeader"/>（在已挂样式与 MText 之后调用）。</summary>
public static class MLeaderPluginAppearanceApplier
{
    public static void ApplyToMLeader(MLeader ml, Transaction tr, Database db, MLeaderToolSettingsDto s)
    {
        if (!string.IsNullOrWhiteSpace(s.TextAttachmentDirection)
            && Enum.TryParse<TextAttachmentDirection>(s.TextAttachmentDirection, ignoreCase: true, out var attachDirection))
        {
            try
            {
                ml.TextAttachmentDirection = attachDirection;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader TextAttachmentDirection", ex);
            }
        }

        if (s.EnableLanding.HasValue)
        {
            try
            {
                ml.EnableLanding = s.EnableLanding.Value;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader EnableLanding", ex);
            }
        }

        if (s.EnableDogleg.HasValue)
        {
            try
            {
                ml.EnableDogleg = s.EnableDogleg.Value;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader EnableDogleg", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(s.LeaderArrowColor)
            && MLeaderToolCadColor.TryParse(s.LeaderArrowColor, out var lineArrowColor))
        {
            try
            {
                ml.LeaderLineColor = lineArrowColor;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader LeaderLineColor", ex);
            }

            try
            {
                ml.BlockColor = lineArrowColor;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader BlockColor（与引线同色）", ex);
            }
        }

        if (s.ArrowSize > 1e-9)
        {
            try
            {
                ml.ArrowSize = s.ArrowSize;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader ArrowSize", ex);
            }
        }

        if (s.DoglegLength > 1e-9)
        {
            try
            {
                ml.DoglegLength = s.DoglegLength;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader DoglegLength", ex);
            }
        }

        if (s.LandingGap > 1e-9)
        {
            try
            {
                ml.LandingGap = s.LandingGap;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader LandingGap（基线长度）", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(s.ArrowBlockName))
        {
            var name = s.ArrowBlockName.Trim();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(name))
            {
                try
                {
                    ml.ArrowSymbolId = bt[name];
                }
                catch (Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal("MLeader ArrowSymbolId", ex);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(s.TextStyleName))
        {
            var ts = s.TextStyleName.Trim();
            var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (tst.Has(ts))
            {
                try
                {
                    ml.TextStyleId = tst[ts];
                }
                catch (Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal("MLeader TextStyleId", ex);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(s.TextColor)
            && MLeaderToolCadColor.TryParse(s.TextColor, out var tc))
        {
            try
            {
                ml.TextColor = tc;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader TextColor", ex);
            }
        }

        if (ml.MText == null)
            return;

        var mt = ml.MText;
        try
        {
            double th;
            if (MLeaderToolSettingsStore.TryGetTextHeightOverride(s, out th))
            {
                ml.TextHeight = th;
                mt.TextHeight = th;
            }
            else if (MLeaderStyleHelper.TryGetStyleTextHeightFromMLeader(ml, tr, out var styleH))
            {
                ml.TextHeight = styleH;
                mt.TextHeight = styleH;
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("MLeader TextHeight", ex);
        }
        if (!string.IsNullOrWhiteSpace(s.TextStyleName))
        {
            var ts = s.TextStyleName.Trim();
            var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (tst.Has(ts))
                mt.TextStyleId = tst[ts];
        }

        if (!string.IsNullOrWhiteSpace(s.TextColor)
            && MLeaderToolCadColor.TryParse(s.TextColor, out var tcMt))
            mt.Color = tcMt;

        ml.MText = mt;

        MLeaderTextAttachmentHelper.ApplySetTextAttachmentTypesFromPlugin(ml, s);
        if (s.MaxLeaderSegmentsPoints > 0)
            MLeaderTextAttachmentHelper.ApplyMaxLeaderSegmentsPointsToStyle(ml, tr, s.MaxLeaderSegmentsPoints);
    }
}
