using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace AcLink
{
    /// <summary>Ranks catalog rows against a target cooling capacity.</summary>
    internal static class AcCatalogSearch
    {
        private static readonly string[] NameHints = { "cool", "capac", "load" };

        /// <summary>
        /// The column whose header token resolves to SpecTypeId.CoolingLoad. Prefers
        /// a name containing "cool"/"capac"/"load" when several such columns exist —
        /// mirrors LoadFinder's own tie-break for reading capacity off an element.
        /// </summary>
        public static CatalogColumn FindCapacityColumn(CatalogFile file)
        {
            if (file == null || !file.IsUsable) return null;

            var matches = file.Columns.Where(c => c.Spec != null && c.Spec.Equals(SpecTypeId.CoolingLoad)).ToList();
            if (matches.Count == 0) return null;
            if (matches.Count == 1) return matches[0];

            foreach (string hint in NameHints)
            {
                var named = matches.FirstOrDefault(c => c.Name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);
                if (named != null) return named;
            }
            return matches[0];
        }

        /// <summary>
        /// Ranks every row of the mapping's catalog by relative deviation from
        /// <paramref name="targetBtuH"/>, dropping rows outside tolerance, and
        /// flags whether each candidate type is already loaded in the project.
        /// </summary>
        public static List<AcCandidate> Run(Document doc, AcCatalogMapping mapping,
            double targetBtuH, double tolerancePercent, out string note)
        {
            note = null;
            var results = new List<AcCandidate>();

            if (mapping == null || !mapping.IsUsable)
            {
                note = "No catalog mapping selected.";
                return results;
            }

            var file = CatalogFile.For(mapping.CatalogPath);
            if (!file.IsUsable)
            {
                note = file.Problem;
                return results;
            }

            var capCol = FindCapacityColumn(file);
            if (capCol == null)
            {
                note = "No column in this catalog declares a Cooling Load value " +
                       "(header token \"HVAC_COOLING_LOAD\"). Check the CSV header.";
                return results;
            }

            double targetInternal = 0;
            try { targetInternal = UnitUtils.ConvertToInternalUnits(targetBtuH, UnitTypeId.BritishThermalUnitsPerHour); }
            catch { /* keep 0 — every row will then show as "far" but still list */ }

            double tolerance = Math.Max(0, tolerancePercent) / 100.0;
            var loaded = FamilyScanner.SymbolsOf(doc, mapping.FamilyName)
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var units = doc?.GetUnits();

            foreach (var row in file.Rows)
            {
                double? cap = row.Number(capCol.Name);
                if (!cap.HasValue) continue;

                double deviation = targetInternal > 1e-9
                    ? Math.Abs(cap.Value - targetInternal) / targetInternal
                    : 0;
                if (deviation > tolerance) continue;

                loaded.TryGetValue(row.TypeName, out FamilySymbol symbol);
                double capacityBtuH = UnitUtils.ConvertFromInternalUnits(cap.Value, UnitTypeId.BritishThermalUnitsPerHour);

                results.Add(new AcCandidate
                {
                    TypeName     = row.TypeName,
                    Symbol       = symbol,
                    Row          = row,
                    CapacityBtuH = capacityBtuH,
                    CapacityText = RevitUnits.Format(units, SpecTypeId.CoolingLoad, cap.Value),
                    Deviation    = deviation
                });
            }

            results = results.OrderBy(c => c.Deviation).ToList();
            if (results.Count == 0)
                note = $"No row of \"{mapping}\" is within {tolerancePercent:0.#}% of the target. " +
                       "Widen the tolerance or pick a different catalog.";

            return results;
        }
    }
}
