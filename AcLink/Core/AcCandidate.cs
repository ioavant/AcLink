using Autodesk.Revit.DB;

namespace AcLink
{
    /// <summary>One catalog row ranked against a target cooling capacity.</summary>
    internal class AcCandidate
    {
        public string TypeName { get; set; }

        /// <summary>Non-null only if this exact type is already loaded in the project.</summary>
        public FamilySymbol Symbol { get; set; }

        public CatalogRow Row { get; set; }

        public bool IsLoaded => Symbol != null;
        public string LoadedText => Symbol != null ? "yes" : "on insert";

        public double CapacityBtuH { get; set; }
        public string CapacityText { get; set; }

        /// <summary>Relative deviation from the target (0 = exact match).</summary>
        public double Deviation { get; set; }

        public string DeviationText => (Deviation * 100.0).ToString("0.#") + "%";
    }
}
