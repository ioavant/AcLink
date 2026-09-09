using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace AcLink
{
    /// <summary>Looks up Mechanical Equipment families/types loaded in the project.</summary>
    internal static class FamilyScanner
    {
        /// <summary>Family names of every mechanical equipment type loaded in the project.</summary>
        public static List<string> EquipmentFamilies(Document doc)
        {
            try
            {
                return new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                    .Cast<FamilySymbol>()
                    .Where(s => s.Family != null && !string.IsNullOrEmpty(s.Family.Name))
                    .Select(s => s.Family.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>Every type of one family that is loaded in the project.</summary>
        public static List<FamilySymbol> SymbolsOf(Document doc, string familyName)
        {
            if (string.IsNullOrEmpty(familyName)) return new List<FamilySymbol>();
            try
            {
                return new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                    .Cast<FamilySymbol>()
                    .Where(s => s.Family != null &&
                                string.Equals(s.Family.Name, familyName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch { return new List<FamilySymbol>(); }
        }
    }
}
