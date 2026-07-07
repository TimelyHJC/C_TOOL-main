using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal static class DddMLeaderCreationService
{
    private static void SyncLeaderGeometry(MLeader ml, int leaderLineIndex, Point3d arrowPoint, Point3d contentPoint)
    {
        if (ml.ContentType == ContentType.MTextContent && ml.MText != null)
        {
            var mt = ml.MText;
            mt.Location = contentPoint;
            ml.MText = mt;
        }
        else if (ml.ContentType == ContentType.BlockContent)
        {
            ml.BlockPosition = contentPoint;
        }

        ml.SetFirstVertex(leaderLineIndex, arrowPoint);
        ml.SetLastVertex(leaderLineIndex, contentPoint);
    }

    private class MLeaderDrawJig : DrawJig
    {
        private Point3d _dragPoint;
        private readonly MLeader _ml;
        private readonly int _leaderLineIndex;
        private readonly Point3d _startPoint;

        public MLeaderDrawJig(MLeader ml, int leaderLineIndex, Point3d startPoint)
        {
            _ml = ml;
            _leaderLineIndex = leaderLineIndex;
            _startPoint = startPoint;
            _dragPoint = startPoint;
        }

        public Point3d DragPoint => _dragPoint;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var opts = new JigPromptPointOptions("\nC_TOOL锛氭寚瀹氬紩绾挎嫄鐐瑰強鏂囧瓧浣嶇疆");
            opts.BasePoint = _startPoint;
            opts.UseBasePoint = true;
            opts.Cursor = CursorType.RubberBand;

            var res = prompts.AcquirePoint(opts);
            if (res.Status == PromptStatus.Cancel || res.Status == PromptStatus.Error)
                return SamplerStatus.Cancel;

            if (res.Value.IsEqualTo(_dragPoint))
                return SamplerStatus.NoChange;

            _dragPoint = res.Value;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            try
            {
                SyncLeaderGeometry(_ml, _leaderLineIndex, _startPoint, _dragPoint);
                using (var clone = _ml.Clone() as MLeader)
                {
                    if (clone != null)
                    {
                        draw.Geometry.Draw(clone);
                    }
                }

                return true;
            }
            catch (System.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeaderStyleExists", ex);
                return false;
            }
        }
    }

    internal static void CreateMLeaderInteractive(Document doc, string text)
    {
        var raw = DddTextContentHelper.NormalizeLineEndings(text);
        if (!DddTextContentHelper.HasVisibleText(raw))
            return;

        var ed = doc.Editor;
        var db = doc.Database;

        using (doc.LockDocument())
        {
            var pr1 = ed.GetPoint("\nC_TOOL锛氭寚瀹氬紩绾胯捣鐐癸紙绠ご渚э級");
            if (pr1.Status != PromptStatus.OK)
                return;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ml = new MLeader();
                ml.SetDatabaseDefaults(db);
                MLeaderStyleHelper.ApplyCurrentStyleToMLeader(ml, doc);

                var mt = new MText();
                mt.SetDatabaseDefaults(db);
                MLeaderStyleHelper.ApplyMLeaderStyleToNewMText(ml, tr, mt);
                mt.Contents = raw;
                ml.MText = mt;

                var leaderIdx = ml.AddLeader();
                var li = ml.AddLeaderLine(leaderIdx);
                SyncLeaderGeometry(ml, li, pr1.Value, pr1.Value);

                MLeaderStyleHelper.ApplyMLeaderStylePropertiesToEntity(ml, tr);
                SyncLeaderGeometry(ml, li, pr1.Value, pr1.Value);

                var jig = new MLeaderDrawJig(ml, li, pr1.Value);
                var res = ed.Drag(jig);

                if (res.Status != PromptStatus.OK)
                {
                    ml.Dispose();
                    tr.Commit();
                    return;
                }

                SyncLeaderGeometry(ml, li, pr1.Value, jig.DragPoint);

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                btr.AppendEntity(ml);
                tr.AddNewlyCreatedDBObject(ml, true);

                MLeaderStyleHelper.ApplyMLeaderStylePropertiesToEntity(ml, tr);
                SyncLeaderGeometry(ml, li, pr1.Value, jig.DragPoint);

                tr.Commit();
            }

            DddLeaderInsertService.RecordLastInsertedText(raw);
        }
    }
}
