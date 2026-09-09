using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;

namespace AcLink
{
    /// <summary>One Mechanical Equipment unit (indoor evaporator or outdoor condenser).</summary>
    public class AcUnit
    {
        public ElementId Id;

        /// <summary>Raw VXD_AClinkName value as stored (null/empty = not linked yet).</summary>
        public string RawName;

        /// <summary>System name without the outdoor suffix.</summary>
        public string BaseName;

        /// <summary>True when RawName ends with the outdoor suffix ("-C").</summary>
        public bool IsOutdoor;

        /// <summary>System key (VXD_AClinkSysKey); empty on legacy units until migrated.</summary>
        public string SysKey;

        public bool IsLinked => !string.IsNullOrWhiteSpace(RawName);

        public string FamilyName;
        public string TypeName;
        public string Description;
        public string Comments;

        /// <summary>Bounding-box height in millimetres (Z extent).</summary>
        public double HeightMm;

        /// <summary>Bounding-box bottom elevation in millimetres.</summary>
        public double ElevationMm;

        /// <summary>Cooling load in Btu/hr (0 if none found).</summary>
        public double CoolingLoadBtuH;

        /// <summary>Name of the project level the unit mostly sits on.</summary>
        public string LevelName;

        /// <summary>True when the unit lives in a linked model (read-only from here).</summary>
        public bool IsRemote;

        /// <summary>Link document title when IsRemote; empty for host units.</summary>
        public string SourceModelName;

        /// <summary>
        /// Host ElementId of the RevitLinkInstance the unit came from (IsRemote only).
        /// NOTE: <see cref="Id"/> of a remote unit belongs to the LINKED document —
        /// never pass it to the host document or to view overrides.
        /// </summary>
        public ElementId LinkInstanceId;

        public string RoleLabel => IsOutdoor ? "Outdoor (condenser)" : "Indoor (evaporator)";

        public string ModelLabel => IsRemote ? (SourceModelName ?? "Link") : "Host";

        /// <summary>Element id as text (for display / Select by ID in the link's model).</summary>
        public string IdText => Id?.ToString() ?? "";
    }

    /// <summary>
    /// A system = all units sharing one system KEY (the identity); the base name is
    /// a human label.  Legacy key-less units group by name until keys are assigned.
    /// </summary>
    public class AcSystem
    {
        public string BaseName;

        /// <summary>Identity key; empty for a legacy (not yet migrated) system.</summary>
        public string SysKey = "";

        /// <summary>True when units of this key carry different names (rename drift).</summary>
        public bool HasNameMismatch;

        public string KeyLabel => string.IsNullOrEmpty(SysKey) ? "(legacy)" : SysKey;

        /// <summary>True when the system spans models (key carries the "X-" prefix).</summary>
        public bool IsCrossModel => AcLinkParams.IsCrossKey(SysKey);
        public AcUnit Outdoor;                       // may be null if not yet assigned
        public List<AcUnit> Indoor = new List<AcUnit>();
        public string Comments;

        public IEnumerable<AcUnit> AllUnits
        {
            get
            {
                if (Outdoor != null) yield return Outdoor;
                foreach (var u in Indoor) yield return u;
            }
        }

        public int IndoorCount => Indoor.Count;

        public string OutdoorLabel => Outdoor != null
            ? $"{Outdoor.FamilyName} : {Outdoor.TypeName}"
            : "—";

        /// <summary>Sum of the indoor units' cooling loads, Btu/hr.</summary>
        public double IndoorCoolingTotalBtuH => Indoor.Sum(u => u.CoolingLoadBtuH);

        /// <summary>
        /// Condenser load factor = outdoor load / Σ(indoor loads).
        /// Null when there is no outdoor unit or the indoor total is zero.
        /// </summary>
        public double? OutdoorLoadFactor
        {
            get
            {
                double sum = IndoorCoolingTotalBtuH;
                if (Outdoor == null || sum <= 0) return null;
                return Outdoor.CoolingLoadBtuH / sum;
            }
        }

        /// <summary>
        /// Which models the system's units live in: "Host", "Apartments.rvt",
        /// or "Host + HVAC-Roof.rvt" when split across models.
        /// </summary>
        public string ModelSummary
        {
            get
            {
                var names = AllUnits
                    .Select(u => u.ModelLabel)
                    .Distinct()
                    .OrderBy(n => n == "Host" ? 0 : 1)
                    .ToList();
                return string.Join(" + ", names);
            }
        }

        /// <summary>
        /// Level(s) of the system. One value if everything is on the same level;
        /// otherwise "indoor level / outdoor level".  The indoor level is the most
        /// common one among the indoor units.
        /// </summary>
        public string LevelSummary
        {
            get
            {
                string indoorRep = Indoor
                    .Where(u => !string.IsNullOrEmpty(u.LevelName))
                    .GroupBy(u => u.LevelName)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault();
                string outdoorLvl = Outdoor?.LevelName;

                var parts = new List<string>();
                if (!string.IsNullOrEmpty(indoorRep))  parts.Add(indoorRep);
                if (!string.IsNullOrEmpty(outdoorLvl)) parts.Add(outdoorLvl);

                return string.Join(" / ", parts.Distinct());
            }
        }
    }
}
