using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

namespace AcLink
{
    /// <summary>
    /// Applies / clears per-system colour overrides (solid fill) on every unit of
    /// every system, across all open graphical views.  Used by the "By system"
    /// colour scheme on the main form.
    /// </summary>
    public static class ColorOverrideService
    {
        public static void Apply(UIDocument uiDoc, IList<AcSystem> systems)
        {
            var doc   = uiDoc.Document;
            var views = OpenGraphicalViews(uiDoc);
            if (systems == null || views.Count == 0) return;

            ElementId solid = SolidFillId(doc);

            using (var tx = new Transaction(doc, "AC Link – colour by system"))
            {
                tx.Start();
                for (int i = 0; i < systems.Count; i++)
                {
                    var ogs = BuildOverride(SystemColorPalette.Revit(i), solid);
                    // Remote units live in linked documents: their ids are alien to
                    // this document, and Revit has no per-element override for link
                    // members anyway — host units only.
                    foreach (var u in systems[i].AllUnits.Where(x => !x.IsRemote))
                        foreach (var v in views)
                            TrySet(v, u.Id, ogs);
                }
                tx.Commit();
            }
        }

        public static void Clear(UIDocument uiDoc, IList<AcSystem> systems)
        {
            var doc   = uiDoc.Document;
            var views = OpenGraphicalViews(uiDoc);
            if (systems == null || views.Count == 0) return;

            var empty = new OverrideGraphicSettings();
            using (var tx = new Transaction(doc, "AC Link – clear colours"))
            {
                tx.Start();
                foreach (var sys in systems)
                    foreach (var u in sys.AllUnits.Where(x => !x.IsRemote))
                        foreach (var v in views)
                            TrySet(v, u.Id, empty);
                tx.Commit();
            }
        }

        /// <summary>
        /// True if the systems currently appear coloured — i.e. a sample unit has a
        /// valid colour override in any open graphical view.  Used to restore the
        /// "By system" choice when the form reopens (survives assembly reloads,
        /// since the truth lives in the document, not in a static field).
        /// </summary>
        public static bool IsColored(UIDocument uiDoc, IList<AcSystem> systems)
        {
            if (systems == null || systems.Count == 0) return false;

            var views = OpenGraphicalViews(uiDoc);
            if (views.Count == 0) return false;

            // Check a sample unit of EACH system (a newly created one may be the only
            // uncoloured system, so checking just the first would miss the mode).
            foreach (var sys in systems)
            {
                var sample = sys.AllUnits.Where(u => !u.IsRemote)
                                         .Select(u => u.Id).FirstOrDefault();
                if (sample == null) continue;
                foreach (var v in views)
                {
                    var ogs = v.GetElementOverrides(sample);
                    if (ogs != null &&
                        (ogs.ProjectionLineColor.IsValid || ogs.SurfaceForegroundPatternColor.IsValid))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Clears overrides on specific elements (e.g. units removed from a system).</summary>
        public static void ClearElements(UIDocument uiDoc, IEnumerable<ElementId> ids)
        {
            var doc   = uiDoc.Document;
            var views = OpenGraphicalViews(uiDoc);
            var idList = ids?.ToList() ?? new List<ElementId>();
            if (views.Count == 0 || idList.Count == 0) return;

            var empty = new OverrideGraphicSettings();
            using (var tx = new Transaction(doc, "AC Link – clear colours"))
            {
                tx.Start();
                foreach (var id in idList)
                    foreach (var v in views)
                        TrySet(v, id, empty);
                tx.Commit();
            }
        }

        private static void TrySet(View v, ElementId id, OverrideGraphicSettings ogs)
        {
            try { v.SetElementOverrides(id, ogs); } catch { }
        }

        private static OverrideGraphicSettings BuildOverride(Color c, ElementId solid)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(c);
            ogs.SetSurfaceForegroundPatternColor(c);
            ogs.SetCutForegroundPatternColor(c);
            if (solid != ElementId.InvalidElementId)
            {
                ogs.SetSurfaceForegroundPatternId(solid);
                ogs.SetCutForegroundPatternId(solid);
            }
            return ogs;
        }

        private static List<View> OpenGraphicalViews(UIDocument uiDoc)
        {
            var doc    = uiDoc.Document;
            var result = new List<View>();
            var seen   = new HashSet<ElementId>();
            foreach (var uv in uiDoc.GetOpenUIViews())
            {
                if (!(doc.GetElement(uv.ViewId) is View v) || v.IsTemplate) continue;
                if (v is ViewSchedule || v is ViewSheet) continue;
                if (v.ViewType == ViewType.Legend) continue;
                if (seen.Add(v.Id)) result.Add(v);
            }
            return result;
        }

        private static ElementId SolidFillId(Document doc)
        {
            var fp = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern()?.IsSolidFill == true);
            return fp?.Id ?? ElementId.InvalidElementId;
        }
    }
}
