using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AcLink
{
    /// <summary>
    /// Reads / writes AC Link data on Mechanical Equipment elements.
    /// The "system" model is derived purely from the VXD_AClinkName values —
    /// there is no separate stored registry.
    /// </summary>
    public static class AcLinkRepository
    {
        private const double FeetToMm = 304.8;

        // ── Collection ────────────────────────────────────────────────────────

        public static List<FamilyInstance> CollectEquipment(Document doc) =>
            new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

        public static bool IsMechanicalEquipment(Element e)
        {
            try { return e?.Category != null &&
                         e.Category.Id == new ElementId(BuiltInCategory.OST_MechanicalEquipment); }
            catch { return false; }
        }

        // ── Read one unit ──────────────────────────────────────────────────────

        public static AcUnit ReadUnit(Element e) => ReadUnit(e, null);

        /// <param name="tf">
        /// Link-instance total transform when <paramref name="e"/> comes from a
        /// linked document — converts its bounding box into host coordinates
        /// (models always share coordinates, so host levels apply afterwards).
        /// </param>
        public static AcUnit ReadUnit(Element e, Transform tf)
        {
            var fi = e as FamilyInstance;
            var u = new AcUnit
            {
                Id          = e.Id,
                FamilyName  = fi?.Symbol?.FamilyName ?? "",
                TypeName    = fi?.Symbol?.Name ?? e.Name ?? "",
                Description = fi?.Symbol?
                                .get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString() ?? "",
                Comments    = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "",
                RawName     = e.LookupParameter(AcLinkParams.AClinkNameText)?.AsString() ?? ""
            };

            var (baseName, isOutdoor) = ParseName(u.RawName);
            u.BaseName  = baseName;
            u.IsOutdoor = isOutdoor;
            u.SysKey    = e.LookupParameter(AcLinkParams.AClinkSysKeyText)?.AsString()?.Trim() ?? "";

            var bb = e.get_BoundingBox(null);
            if (bb != null)
            {
                double zMin = bb.Min.Z, zMax = bb.Max.Z;
                if (tf != null && !tf.IsIdentity)
                    GetTransformedZRange(bb, tf, out zMin, out zMax);

                u.HeightMm    = (zMax - zMin) * FeetToMm;
                u.ElevationMm = zMin * FeetToMm;
            }

            u.CoolingLoadBtuH = LoadFinder.CoolingLoadBtuH(e);
            return u;
        }

        // ── Name parsing / building ─────────────────────────────────────────────

        public static (string baseName, bool isOutdoor) ParseName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return ("", false);
            raw = raw.Trim();
            if (raw.EndsWith(AcLinkParams.OutdoorSuffix, StringComparison.OrdinalIgnoreCase))
                return (raw.Substring(0, raw.Length - AcLinkParams.OutdoorSuffix.Length), true);
            return (raw, false);
        }

        public static string BuildName(string baseName, bool isOutdoor) =>
            isOutdoor ? baseName + AcLinkParams.OutdoorSuffix : baseName;

        // ── Systems ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Systems of this model.  By design most systems live INSIDE one model, so
        /// the default path never touches links.  Only systems whose key carries the
        /// cross-model marker ("X-…") pull their remaining units from loaded links —
        /// and even then non-matching link elements are skipped after one cheap
        /// parameter read (no bbox / load parsing).
        /// </summary>
        public static List<AcSystem> GetSystems(Document doc)
        {
            var levels = GetLevelsMm(doc);
            var units  = new List<AcUnit>();

            // Host units.
            foreach (var fi in CollectEquipment(doc))
            {
                var u = ReadUnit(fi);
                if (u.IsLinked) units.Add(u);
            }

            // Link units — ONLY for cross-model systems present in this model.
            var crossKeys = new HashSet<string>(
                units.Where(u => AcLinkParams.IsCrossKey(u.SysKey)).Select(u => u.SysKey),
                StringComparer.OrdinalIgnoreCase);

            if (crossKeys.Count > 0)
            {
                foreach (var (inst, linkDoc) in GetUniqueLoadedLinks(doc))
                {
                    Transform tf;
                    try { tf = inst.GetTotalTransform(); } catch { tf = null; }

                    foreach (var fi in CollectEquipment(linkDoc))
                    {
                        // Cheap pre-filter: one parameter read decides membership.
                        string key = fi.LookupParameter(AcLinkParams.AClinkSysKeyText)
                                       ?.AsString()?.Trim();
                        if (string.IsNullOrEmpty(key) || !crossKeys.Contains(key)) continue;

                        var u = ReadUnit(fi, tf);
                        if (!u.IsLinked) continue;
                        u.IsRemote        = true;
                        u.SourceModelName = linkDoc.Title;
                        u.LinkInstanceId  = inst.Id;
                        units.Add(u);
                    }
                }
            }

            foreach (var u in units)
                u.LevelName = LevelForSpan(u.ElevationMm, u.ElevationMm + u.HeightMm, levels);

            // ── Group by the system KEY (the identity); name is only a label ──
            var keyed   = units.Where(u => !string.IsNullOrWhiteSpace(u.SysKey)).ToList();
            var keyless = units.Where(u =>  string.IsNullOrWhiteSpace(u.SysKey)).ToList();

            var systems = keyed
                .GroupBy(u => u.SysKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => BuildSystem(g.Key, g))
                .ToList();

            // Legacy key-less units attach to the keyed system with the same name
            // when that is unambiguous; otherwise they form their own legacy system.
            foreach (var g in keyless.GroupBy(u => u.BaseName, StringComparer.OrdinalIgnoreCase))
            {
                var matches = systems.Where(s =>
                    string.Equals(s.BaseName, g.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1) MergeInto(matches[0], g);
                else                    systems.Add(BuildSystem("", g));
            }

            return systems.OrderBy(s => s.BaseName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static AcSystem BuildSystem(string sysKey, IEnumerable<AcUnit> members)
        {
            var list = members.OrderBy(x => x.IsRemote ? 1 : 0).ToList();  // host units first
            var sys = new AcSystem
            {
                SysKey   = sysKey ?? "",
                BaseName = list[0].BaseName,
                HasNameMismatch = list.Select(u => u.BaseName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            };
            MergeInto(sys, list);
            return sys;
        }

        private static void MergeInto(AcSystem sys, IEnumerable<AcUnit> members)
        {
            foreach (var u in members.OrderBy(x => x.IsRemote ? 1 : 0))
            {
                if (u.IsOutdoor && sys.Outdoor == null) sys.Outdoor = u;
                else sys.Indoor.Add(u);   // extra "outdoor"s fall back to indoor list
            }
            if (string.IsNullOrWhiteSpace(sys.Comments))
                sys.Comments = members.Select(u => u.Comments)
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";
        }

        /// <summary>
        /// Does the unit belong to the given system?  By key when the system has
        /// one (same-named legacy units count as members — they merge by name);
        /// purely by name for legacy systems.
        /// </summary>
        public static bool BelongsTo(AcUnit u, string sysKey, string baseName)
        {
            bool sameName = string.Equals(u.BaseName, baseName, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(sysKey))
                return string.IsNullOrWhiteSpace(u.SysKey) && sameName;
            return string.Equals(u.SysKey, sysKey, StringComparison.OrdinalIgnoreCase)
                   || (string.IsNullOrWhiteSpace(u.SysKey) && sameName);
        }

        /// <summary>
        /// Loaded link instances, one per distinct linked document.  When the same
        /// link is placed several times, the first instance is used for transforms.
        /// Unloaded links are skipped (their systems simply look incomplete).
        /// </summary>
        private static IEnumerable<(RevitLinkInstance inst, Document doc)> GetUniqueLoadedLinks(Document doc)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>())
            {
                Document linkDoc;
                try { linkDoc = inst.GetLinkDocument(); } catch { continue; }
                if (linkDoc == null) continue;                    // link unloaded

                if (!seen.Add(linkDoc.Title ?? "")) continue;     // repeated placement
                yield return (inst, linkDoc);
            }
        }

        /// <summary>Min/max Z of a bounding box after applying a link transform (8 corners).</summary>
        private static void GetTransformedZRange(
            BoundingBoxXYZ bb, Transform tf, out double zMin, out double zMax)
        {
            zMin = double.MaxValue; zMax = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = new XYZ(
                    (i & 1) == 0 ? bb.Min.X : bb.Max.X,
                    (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                    (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                double z = tf.OfPoint(p).Z;
                if (z < zMin) zMin = z;
                if (z > zMax) zMax = z;
            }
        }

        // ── Levels ───────────────────────────────────────────────────────────

        /// <summary>All project levels as (name, elevation in mm), sorted ascending.</summary>
        public static List<(string name, double elevMm)> GetLevelsMm(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation)
                .Select(l => (l.Name, l.ProjectElevation * FeetToMm))
                .ToList();

        /// <summary>
        /// Returns the name of the level the element mostly sits on: the level whose
        /// band [elev_i, elev_{i+1}) overlaps the element's [zMin, zMax] span the most.
        /// A point (zero-height) lands in its containing band.
        /// </summary>
        public static string LevelForSpan(
            double zMinMm, double zMaxMm, List<(string name, double elevMm)> levels)
        {
            if (levels == null || levels.Count == 0) return "";

            int best = 0;
            double bestOverlap = double.NegativeInfinity;
            for (int i = 0; i < levels.Count; i++)
            {
                double bandLo = levels[i].elevMm;
                double bandHi = (i < levels.Count - 1) ? levels[i + 1].elevMm : double.PositiveInfinity;
                double overlap = System.Math.Min(zMaxMm, bandHi) - System.Math.Max(zMinMm, bandLo);
                if (overlap > bestOverlap) { bestOverlap = overlap; best = i; }
            }
            return levels[best].name;
        }

        public static string SuggestNextBaseName(Document doc)
        {
            int max = 0;
            foreach (var sys in GetSystems(doc))
            {
                var m = Regex.Match(sys.BaseName ?? "", @"(\d+)\s*$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > max) max = n;
            }
            return $"AC-{max + 1:D2}";
        }

        // ── Write ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Assigns the given base name + roles + comments to a set of units.
        /// Caller is responsible for being inside (or wrapping) a transaction —
        /// here we open our own so the call is self-contained.
        /// </summary>
        public static void AssignSystem(Document doc, string baseName, string sysKey,
            IEnumerable<(ElementId id, bool isOutdoor)> roles, string comments)
        {
            SharedParamHelper.EnsureParameters(doc);

            using (var tx = new Transaction(doc, "AC Link – assign system"))
            {
                tx.Start();
                foreach (var (id, isOutdoor) in roles)
                {
                    var e = doc.GetElement(id);
                    if (e == null) continue;

                    SetMembership(e, BuildName(baseName, isOutdoor), sysKey);
                    SetComments(e, comments);
                }
                tx.Commit();
            }
        }

        /// <summary>Adds units to an existing system as indoor (evaporator) units.</summary>
        public static void AddUnits(Document doc, string baseName, string sysKey,
            IEnumerable<ElementId> ids, string comments)
        {
            SharedParamHelper.EnsureParameters(doc);

            using (var tx = new Transaction(doc, "AC Link – add units"))
            {
                tx.Start();
                foreach (var id in ids)
                {
                    var e = doc.GetElement(id);
                    if (e == null) continue;

                    SetMembership(e, BuildName(baseName, false), sysKey);

                    if (!string.IsNullOrEmpty(comments))
                        SetComments(e, comments);
                }
                tx.Commit();
            }
        }

        /// <summary>Writes the same Comments value to every host unit of a system.</summary>
        public static void SetSystemComments(Document doc, string baseName, string sysKey, string comments)
        {
            var ids = CollectEquipment(doc).Select(ReadUnit)
                .Where(u => u.IsLinked && BelongsTo(u, sysKey, baseName))
                .Select(u => u.Id)
                .ToList();
            if (ids.Count == 0) return;

            using (var tx = new Transaction(doc, "AC Link – set system comments"))
            {
                tx.Start();
                foreach (var id in ids)
                    SetComments(doc.GetElement(id), comments ?? "");
                tx.Commit();
            }
        }

        /// <summary>Removes units from their system by clearing VXD_AClinkName.</summary>
        public static void RemoveUnits(Document doc, IEnumerable<ElementId> ids)
        {
            using (var tx = new Transaction(doc, "AC Link – remove units"))
            {
                tx.Start();
                foreach (var id in ids)
                {
                    var e = doc.GetElement(id);
                    if (e == null) continue;
                    SetMembership(e, "", null);
                }
                tx.Commit();
            }
        }

        /// <summary>
        /// Makes the given unit the outdoor (condenser) of its system and demotes
        /// any current outdoor unit(s) of that system to indoor.
        /// </summary>
        public static void SetOutdoor(Document doc, string baseName, string sysKey, ElementId newOutdoorId)
        {
            SharedParamHelper.EnsureParameters(doc);

            var currentOutdoors = CollectEquipment(doc)
                .Select(ReadUnit)
                .Where(u => u.IsLinked && u.IsOutdoor && BelongsTo(u, sysKey, baseName))
                .Select(u => u.Id)
                .ToList();

            // null = leave each element's existing key untouched (legacy systems).
            string keyToWrite = string.IsNullOrEmpty(sysKey) ? null : sysKey;

            using (var tx = new Transaction(doc, "AC Link – set outdoor unit"))
            {
                tx.Start();
                foreach (var id in currentOutdoors)
                {
                    if (id == newOutdoorId) continue;
                    var e = doc.GetElement(id);
                    if (e != null) SetMembership(e, BuildName(baseName, false), keyToWrite);
                }
                var ne = doc.GetElement(newOutdoorId);
                if (ne != null) SetMembership(ne, BuildName(baseName, true), keyToWrite);
                tx.Commit();
            }
        }

        // ── Copy protection ──────────────────────────────────────────────────

        /// <summary>One set of copied units that together look like a whole system
        /// (same source key, same landing level, a full outdoor+indoor shape) —
        /// candidate to become a brand-new system instead of being detached.</summary>
        public class CopiedSystemGroup
        {
            public string OldKey;
            public string NewKey;

            /// <summary>The name it was copied from — for display only.</summary>
            public string OldBaseName;

            /// <summary>Fresh, sequential, unique name the promoted system gets written with.</summary>
            public string BaseName;
            public string LevelLabel;
            public List<ElementId> Ids = new List<ElementId>();
        }

        /// <summary>Result of <see cref="PlanCopyCleanup"/> — read-only, apply with
        /// <see cref="ApplyCopyCleanup"/> after the caller decides on ToPromote.</summary>
        public class CopyCleanupPlan
        {
            public List<ElementId> ToStamp  = new List<ElementId>();
            public List<ElementId> ToDetach = new List<ElementId>();
            public List<CopiedSystemGroup> ToPromote = new List<CopiedSystemGroup>();
        }

        /// <summary>
        /// Lazy guard run at command start: finds units whose stored stamp no
        /// longer matches their own ElementId (i.e. copies / arrays / pasted
        /// groups). Legacy linked-but-unstamped units (e.g. from an earlier build)
        /// go to ToStamp so future copies of them are caught.
        ///
        /// Copies are ordinarily stray duplicates (ToDetach). But a set of copies
        /// that share the same source system key, landed together on one level,
        /// and still form a complete system shape (exactly one outdoor + at least
        /// one indoor) look like a whole design pasted onto another floor — those
        /// are surfaced as ToPromote for the caller to offer as new systems.
        /// Read-only; apply with <see cref="ApplyCopyCleanup"/>.
        /// </summary>
        public static CopyCleanupPlan PlanCopyCleanup(Document doc)
        {
            var plan = new CopyCleanupPlan();
            var candidates = new List<(Element e, AcUnit u)>();

            foreach (var fi in CollectEquipment(doc))
            {
                string name = fi.LookupParameter(AcLinkParams.AClinkNameText)?.AsString();
                if (string.IsNullOrWhiteSpace(name)) continue;   // not linked

                string stamp = fi.LookupParameter(AcLinkParams.AClinkUidText)?.AsString();
                string self  = fi.UniqueId;

                if (string.IsNullOrWhiteSpace(stamp)) { plan.ToStamp.Add(fi.Id); continue; } // legacy → adopt
                if (stamp == self) continue;                                                 // genuine unit

                candidates.Add((fi, ReadUnit(fi)));   // copy → classify below
            }

            if (candidates.Count == 0) return plan;

            var levels = GetLevelsMm(doc);
            foreach (var c in candidates)
                c.u.LevelName = LevelForSpan(c.u.ElevationMm, c.u.ElevationMm + c.u.HeightMm, levels);

            var promoted = new HashSet<ElementId>();
            var qualifying = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.u.SysKey))
                .GroupBy(c => c.u.SysKey.ToUpperInvariant() + "|" + (c.u.LevelName ?? ""))
                .Select(g => g.ToList())
                .Where(list => list.Count(c => c.u.IsOutdoor) == 1 && list.Count >= 2)  // full outdoor+indoor shape
                .ToList();

            // Each promoted system gets the next free "AC-NN" name in sequence — never
            // the name it was copied from, which would just create a second system
            // sharing a name with the first (allowed by key, but confusing to read).
            var newNames = NextBaseNames(doc, qualifying.Count);

            for (int i = 0; i < qualifying.Count; i++)
            {
                var list = qualifying[i];
                string oldKey   = list[0].u.SysKey;
                bool   wasCross = AcLinkParams.IsCrossKey(oldKey);
                var group = new CopiedSystemGroup
                {
                    OldKey      = oldKey,
                    NewKey      = AcLinkParams.NewSystemKey(wasCross),
                    OldBaseName = list[0].u.BaseName,
                    BaseName    = newNames[i],
                    LevelLabel  = list[0].u.LevelName,
                };
                group.Ids.AddRange(list.Select(c => c.e.Id));
                plan.ToPromote.Add(group);
                foreach (var c in list) promoted.Add(c.e.Id);
            }

            foreach (var c in candidates)
                if (!promoted.Contains(c.e.Id)) plan.ToDetach.Add(c.e.Id);

            return plan;
        }

        /// <summary>
        /// Applies a plan from <see cref="PlanCopyCleanup"/>. Stray copies (ToDetach)
        /// are always unlinked; ToPromote groups are turned into new systems (fresh
        /// key, fresh sequential name, roles kept as copied) only when
        /// <paramref name="promoteFullCopies"/> is true — otherwise they are detached
        /// like any other copy. Opens its own transaction only if there is something
        /// to change.
        /// </summary>
        public static void ApplyCopyCleanup(Document doc, CopyCleanupPlan plan, bool promoteFullCopies)
        {
            var toDetach = new List<ElementId>(plan.ToDetach);
            if (!promoteFullCopies)
                foreach (var g in plan.ToPromote) toDetach.AddRange(g.Ids);

            bool promoting = promoteFullCopies && plan.ToPromote.Count > 0;
            if (toDetach.Count == 0 && plan.ToStamp.Count == 0 && !promoting) return;

            SharedParamHelper.EnsureParameters(doc);   // ensure all params bound

            using (var tx = new Transaction(doc, "AC Link – clean copied units"))
            {
                tx.Start();
                foreach (var id in plan.ToStamp)
                {
                    var e = doc.GetElement(id);
                    if (e != null) SetUid(e, e.UniqueId);
                }
                foreach (var id in toDetach)
                {
                    var e = doc.GetElement(id);
                    if (e != null) SetMembership(e, "", null);
                }
                if (promoting)
                    foreach (var g in plan.ToPromote)
                        foreach (var id in g.Ids)
                        {
                            var e = doc.GetElement(id);
                            if (e == null) continue;
                            string oldName = e.LookupParameter(AcLinkParams.AClinkNameText)?.AsString() ?? "";
                            var (_, isOutdoor) = ParseName(oldName);
                            SetName(e, BuildName(g.BaseName, isOutdoor));   // rename to the fresh sequential name
                            SetUid(e, e.UniqueId);                          // re-stamp to self — no longer a copy
                            SetKey(e, g.NewKey);
                        }
                tx.Commit();
            }
        }

        /// <summary>Next <paramref name="count"/> free "AC-NN" names in sequence, after
        /// the highest numeric suffix currently in use — same convention as
        /// <see cref="SuggestNextBaseName"/>, generalized to hand out several at once
        /// (e.g. several systems promoted from copies in the same run) without
        /// repeating a number.</summary>
        private static List<string> NextBaseNames(Document doc, int count)
        {
            int max = 0;
            foreach (var sys in GetSystems(doc))
            {
                var m = Regex.Match(sys.BaseName ?? "", @"(\d+)\s*$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > max) max = n;
            }
            var names = new List<string>();
            for (int i = 1; i <= count; i++) names.Add($"AC-{max + i:D2}");
            return names;
        }

        // ── Legacy key migration ─────────────────────────────────────────────

        /// <summary>One name-group of host units that still lack a system key.</summary>
        public class LegacyKeyPlan
        {
            public string BaseName;
            public string Key;
            public List<ElementId> Ids = new List<ElementId>();
        }

        /// <summary>
        /// Plans keys for host units that are linked into a system but have no key
        /// yet.  Legacy systems are in-model by definition, so each name-group just
        /// gets a fresh (non-cross) key — no link scanning involved.
        /// Read-only — apply with <see cref="ApplyLegacyKeys"/> after confirmation.
        /// </summary>
        public static List<LegacyKeyPlan> PlanLegacyKeys(Document doc)
        {
            var keyless = CollectEquipment(doc)
                .Select(ReadUnit)
                .Where(u => u.IsLinked && string.IsNullOrWhiteSpace(u.SysKey))
                .ToList();
            if (keyless.Count == 0) return new List<LegacyKeyPlan>();

            var plans = new List<LegacyKeyPlan>();
            foreach (var g in keyless.GroupBy(u => u.BaseName, StringComparer.OrdinalIgnoreCase))
            {
                var plan = new LegacyKeyPlan { BaseName = g.Key, Key = AcLinkParams.NewSystemKey() };
                plan.Ids.AddRange(g.Select(u => u.Id));
                plans.Add(plan);
            }
            return plans.OrderBy(p => p.BaseName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Promotes a system to cross-model (adds the "X-" key prefix) or demotes it
        /// back to in-model (strips the prefix).  Rewrites keys on HOST units only —
        /// on demote, a linked half keeps the old cross key and thus detaches (the
        /// command warns about that beforehand).  Returns the new key.
        /// </summary>
        public static string SetCrossModel(Document doc, AcSystem sys, bool cross)
        {
            string oldKey = sys.SysKey ?? "";
            string newKey;
            if (cross)
                newKey = string.IsNullOrEmpty(oldKey)
                    ? AcLinkParams.NewSystemKey(true)
                    : (AcLinkParams.IsCrossKey(oldKey) ? oldKey
                        : AcLinkParams.CrossKeyPrefix + oldKey);
            else
                newKey = string.IsNullOrEmpty(oldKey)
                    ? AcLinkParams.NewSystemKey(false)
                    : (AcLinkParams.IsCrossKey(oldKey)
                        ? oldKey.Substring(AcLinkParams.CrossKeyPrefix.Length) : oldKey);

            if (newKey.Equals(oldKey, StringComparison.OrdinalIgnoreCase)) return oldKey;

            SharedParamHelper.EnsureParameters(doc);
            using (var tx = new Transaction(doc, cross
                ? "AC Link – make system cross-model" : "AC Link – make system in-model"))
            {
                tx.Start();
                foreach (var u in sys.AllUnits.Where(x => !x.IsRemote))
                {
                    var e = doc.GetElement(u.Id);
                    if (e != null) SetKey(e, newKey);
                }
                tx.Commit();
            }
            return newKey;
        }

        /// <summary>
        /// Rewrites the system key on all HOST units of a system — the "Set key…"
        /// mechanism: paste a key copied from another model to join that system.
        /// A pasted "X-…" key implicitly makes the system cross-model.
        /// </summary>
        public static void SetSystemKey(Document doc, AcSystem sys, string newKey)
        {
            if (string.IsNullOrWhiteSpace(newKey)) return;
            SharedParamHelper.EnsureParameters(doc);

            using (var tx = new Transaction(doc, "AC Link – set system key"))
            {
                tx.Start();
                foreach (var u in sys.AllUnits.Where(x => !x.IsRemote))
                {
                    var e = doc.GetElement(u.Id);
                    if (e != null) SetKey(e, newKey.Trim());
                }
                tx.Commit();
            }
        }

        public static void ApplyLegacyKeys(Document doc, List<LegacyKeyPlan> plans)
        {
            if (plans == null || plans.Count == 0) return;
            SharedParamHelper.EnsureParameters(doc);

            using (var tx = new Transaction(doc, "AC Link – assign system keys"))
            {
                tx.Start();
                foreach (var plan in plans)
                    foreach (var id in plan.Ids)
                    {
                        var e = doc.GetElement(id);
                        if (e != null) SetKey(e, plan.Key);
                    }
                tx.Commit();
            }
        }

        // ── Low-level writers ────────────────────────────────────────────────

        /// <summary>
        /// Sets the system name, stamps (or clears) the guard id, and writes the
        /// system key.  Empty name = full unlink (all three cleared).
        /// <paramref name="sysKey"/> null = leave the element's key untouched.
        /// </summary>
        private static void SetMembership(Element e, string name, string sysKey)
        {
            SetName(e, name);
            SetUid(e, string.IsNullOrEmpty(name) ? "" : e.UniqueId);

            if (string.IsNullOrEmpty(name)) SetKey(e, "");
            else if (sysKey != null)        SetKey(e, sysKey);
        }

        private static void SetName(Element e, string value)
        {
            var p = e.get_Parameter(AcLinkParams.AClinkName);
            if (p != null && !p.IsReadOnly) p.Set(value ?? "");
        }

        private static void SetUid(Element e, string value)
        {
            var p = e.get_Parameter(AcLinkParams.AClinkUid);
            if (p != null && !p.IsReadOnly) p.Set(value ?? "");
        }

        private static void SetKey(Element e, string value)
        {
            var p = e.get_Parameter(AcLinkParams.AClinkSysKey);
            if (p != null && !p.IsReadOnly) p.Set(value ?? "");
        }

        /// <summary>
        /// Writes the built-in Comments value, but SKIPS elements that live inside
        /// a group: Comments is a built-in parameter that cannot "vary by group",
        /// so Revit rejects editing it on a group member.  Our VXD_* parameters are
        /// set to vary by group, so those are still written on grouped elements.
        /// </summary>
        private static void SetComments(Element e, string comments)
        {
            if (e == null || comments == null) return;
            if (e.GroupId != ElementId.InvalidElementId) return;   // member of a group → skip
            var c = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (c != null && !c.IsReadOnly) c.Set(comments);
        }
    }
}
