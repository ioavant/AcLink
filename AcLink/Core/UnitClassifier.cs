using System.Collections.Generic;
using System.Linq;

namespace AcLink
{
    /// <summary>
    /// Heuristic indoor/outdoor classification, used ONLY to pre-fill roles when
    /// the user first creates a system.  After assignment the role lives in the
    /// stored name suffix, so this is never needed to re-read a system.
    ///
    /// Signals:
    ///   • keywords in family / type / description (strong)
    ///   • bounding-box height — taller ⇒ more likely the condenser (weak)
    /// Rule: at most ONE outdoor unit per selection (1 condenser + N evaporators).
    /// </summary>
    public static class UnitClassifier
    {
        private static readonly string[] OutdoorKeywords =
        {
            "outdoor", "condens", "odu", "external",
            "наружн", "конденс", "уличн"
        };

        private static readonly string[] IndoorKeywords =
        {
            "indoor", "evapor", "cassette", "ducted", "fancoil", "fan coil",
            "idu", "fcu", "wall mount", "wall-mount", "ceiling",
            "внутрен", "испар", "кассет", "настен", "канальн", "потолоч"
        };

        public static double OutdoorScore(AcUnit u)
        {
            string t = ((u.FamilyName ?? "") + " " +
                        (u.TypeName ?? "") + " " +
                        (u.Description ?? "")).ToLowerInvariant();

            double score = 0;
            foreach (var k in OutdoorKeywords) if (t.Contains(k)) score += 10;
            foreach (var k in IndoorKeywords)  if (t.Contains(k)) score -= 10;

            // +1 point per metre of height — weak tie-breaker.
            score += u.HeightMm / 1000.0;
            return score;
        }

        /// <summary>
        /// Sets <see cref="AcUnit.IsOutdoor"/> on each unit.
        ///   • Units already linked keep their stored role.
        ///   • If a linked outdoor unit already exists, all unlinked units → indoor.
        ///   • Otherwise the single highest-scoring unit becomes the outdoor one.
        /// </summary>
        public static void PrefillRoles(IList<AcUnit> units)
        {
            if (units == null || units.Count == 0) return;

            bool hasLinkedOutdoor = units.Any(u => u.IsLinked && u.IsOutdoor);

            // Keep stored roles for already-linked units.
            var candidates = units.Where(u => !u.IsLinked).ToList();
            if (candidates.Count == 0) return;

            if (hasLinkedOutdoor)
            {
                foreach (var u in candidates) u.IsOutdoor = false;
                return;
            }

            // Pick the best candidate as outdoor; everyone else indoor.
            var best = candidates.OrderByDescending(OutdoorScore).First();
            foreach (var u in candidates) u.IsOutdoor = (u == best);
        }
    }
}
