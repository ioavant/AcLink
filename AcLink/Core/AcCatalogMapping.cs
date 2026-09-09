using System.Runtime.Serialization;

namespace AcLink
{
    /// <summary>Which side of a system a catalog mapping builds units for.</summary>
    [DataContract]
    internal enum AcCatalogRole
    {
        [EnumMember] Indoor,
        [EnumMember] Outdoor
    }

    /// <summary>
    /// One user-configured catalog "profile": a label (e.g. "Wall mounted",
    /// "Cassette", "Outdoor VRF"), a role, the Revit family it builds types for
    /// (must already be loaded in the project), and the type-catalog CSV/TXT that
    /// describes its types. Several profiles may share a role — a project
    /// typically has more than one indoor unit style.
    ///
    /// Unlike a general parameter-mapping tool, AC Link cares about exactly one
    /// quantity (cooling capacity), so there is no per-quantity column/parameter
    /// pairing here — the capacity column and the family parameter it writes to
    /// are both auto-detected by Revit data type (see AcCatalogSearch/AcUnitPlacer).
    /// </summary>
    [DataContract]
    internal class AcCatalogMapping
    {
        [DataMember(Name = "label", Order = 0)]
        public string Label { get; set; }

        [DataMember(Name = "role", Order = 1)]
        public AcCatalogRole Role { get; set; }

        [DataMember(Name = "family", Order = 2)]
        public string FamilyName { get; set; }

        [DataMember(Name = "catalog", Order = 3)]
        public string CatalogPath { get; set; }

        public bool IsUsable => !string.IsNullOrWhiteSpace(FamilyName) && !string.IsNullOrWhiteSpace(CatalogPath);

        public override string ToString() =>
            string.IsNullOrWhiteSpace(Label) ? (FamilyName ?? "(mapping)") : Label;
    }
}
