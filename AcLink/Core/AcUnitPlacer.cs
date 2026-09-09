using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace AcLink
{
    internal class PlacementResult
    {
        public bool Placed { get; set; }
        public bool Cancelled { get; set; }

        /// <summary>Something to tell the user. Null when there is nothing to say.</summary>
        public string Message { get; set; }

        /// <summary>The placed element, when Placed is true.</summary>
        public FamilyInstance Instance { get; set; }

        public static PlacementResult Cancel() => new PlacementResult { Cancelled = true };
        public static PlacementResult Fail(string message) => new PlacementResult { Message = message };
    }

    /// <summary>
    /// Places a catalog-picked unit: click a point, build/reuse the family type for
    /// that catalog row, create the instance.
    ///
    /// Unlike a fan whose duty is normally an instance parameter filled in per
    /// install, every AC Link catalog row becomes its own distinct family TYPE
    /// (built once via Duplicate + ApplyCatalogRow) with capacity already baked in
    /// as a type parameter — so there is no separate instance-parameter write step.
    /// </summary>
    internal static class AcUnitPlacer
    {
        public static PlacementResult Place(UIDocument uidoc, AcCandidate candidate, AcCatalogMapping mapping)
        {
            if (uidoc == null || candidate == null) return PlacementResult.Fail("Nothing to place.");

            Document doc = uidoc.Document;

            XYZ point;
            try
            {
                point = uidoc.Selection.PickPoint("Click the insertion point for " + candidate.TypeName);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return PlacementResult.Cancel();
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                return PlacementResult.Fail(
                    "This view does not accept a point. Switch to a plan, section or 3D view and try again.");
            }

            Level level = ResolveLevel(doc, point);
            if (level == null)
                return PlacementResult.Fail("The project has no levels, so there is nothing to host the unit on.");

            using (var transaction = new Transaction(doc, "AC Link – place unit"))
            {
                transaction.Start();
                try
                {
                    FamilySymbol symbol = Resolve(doc, candidate, mapping, out string problem);
                    if (symbol == null)
                    {
                        transaction.RollBack();
                        return PlacementResult.Fail(problem);
                    }

                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }

                    // PickPoint's Z is unreliable in a plan view (often not the level's
                    // own elevation), which is what was dropping the unit far below —
                    // host it at the resolved level's own elevation instead, at the
                    // picked X/Y.
                    XYZ target = new XYZ(point.X, point.Y, level.Elevation);
                    FamilyInstance instance = doc.Create.NewFamilyInstance(
                        target, symbol, level, StructuralType.NonStructural);

                    transaction.Commit();
                    return new PlacementResult { Placed = true, Instance = instance };
                }
                catch (Exception exception)
                {
                    transaction.RollBack();
                    return PlacementResult.Fail("The unit could not be placed: " + exception.Message);
                }
            }
        }

        /// <summary>
        /// The type to place, created from its catalog row when the project does not
        /// have it yet. Must run inside the caller's open transaction.
        /// </summary>
        private static FamilySymbol Resolve(Document doc, AcCandidate candidate,
            AcCatalogMapping mapping, out string problem)
        {
            problem = null;
            if (candidate.Symbol != null) return candidate.Symbol;

            List<FamilySymbol> existing = FamilyScanner.SymbolsOf(doc, mapping.FamilyName);

            // Between searching and inserting, somebody may have loaded it.
            FamilySymbol already = existing.FirstOrDefault(
                s => string.Equals(s.Name, candidate.TypeName, StringComparison.OrdinalIgnoreCase));
            if (already != null) return already;

            if (existing.Count == 0)
            {
                problem = $"The family \"{mapping.FamilyName}\" has no types in this project, so there is " +
                          $"nothing to build \"{candidate.TypeName}\" from.\n\nLoad the family into the project first.";
                return null;
            }

            FamilySymbol created;
            try
            {
                created = existing[0].Duplicate(candidate.TypeName) as FamilySymbol;
            }
            catch (Exception exception)
            {
                problem = $"The type \"{candidate.TypeName}\" could not be created in this project: {exception.Message}";
                return null;
            }

            if (created == null)
            {
                problem = $"The type \"{candidate.TypeName}\" could not be created in this project.";
                return null;
            }

            ApplyCatalogRow(created, candidate, mapping);
            return created;
        }

        /// <summary>
        /// Writes the whole catalog line onto a freshly made type by name — the
        /// same thing Revit itself does when it applies a type catalog. Columns
        /// that don't match a type parameter, or match a read-only one, are
        /// skipped; one bad column must not lose the whole type.
        /// </summary>
        private static void ApplyCatalogRow(FamilySymbol symbol, AcCandidate candidate, AcCatalogMapping mapping)
        {
            var file = CatalogFile.For(mapping.CatalogPath);
            if (candidate.Row == null || !file.IsUsable) return;

            foreach (var column in file.Columns)
            {
                Parameter parameter;
                try { parameter = symbol.LookupParameter(column.Name); }
                catch { parameter = null; }

                if (parameter == null || parameter.IsReadOnly) continue;
                Apply(parameter, candidate.Row, column.Name);
            }
        }

        private static bool Apply(Parameter parameter, CatalogRow row, string column)
        {
            try
            {
                double? number = row.Number(column);
                if (parameter.StorageType == StorageType.Double && number.HasValue)
                    return parameter.Set(number.Value);
                if (parameter.StorageType == StorageType.Integer && number.HasValue)
                    return parameter.Set((int)Math.Round(number.Value));
                if (parameter.StorageType == StorageType.String)
                {
                    string text = row.Text(column);
                    return text.Length > 0 && parameter.Set(text);
                }
            }
            catch { /* one column must not lose the type */ }
            return false;
        }

        /// <summary>
        /// The level to host the instance on: the view's own level in a plan, and
        /// otherwise the nearest level at or below the picked point.
        /// </summary>
        private static Level ResolveLevel(Document doc, XYZ point)
        {
            try
            {
                Level viewLevel = doc.ActiveView?.GenLevel;
                if (viewLevel != null) return viewLevel;
            }
            catch { /* some view types throw rather than return null */ }

            List<Level> levels;
            try
            {
                levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .ToList();
            }
            catch { return null; }

            if (levels.Count == 0) return null;

            Level below = levels.LastOrDefault(l => l.Elevation <= point.Z + 1e-6);
            return below ?? levels[0];
        }
    }
}
