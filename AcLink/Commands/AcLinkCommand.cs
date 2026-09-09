using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AcLink
{
    /// <summary>
    /// AC Link — single test-phase command.
    ///
    /// Behaviour depends on the current selection:
    ///   • Selected Mechanical Equipment, all already in ONE system
    ///       → open that system's element list (show / zoom units).
    ///   • Selected Mechanical Equipment, not yet linked (or mixed)
    ///       → create / assign dialog (auto-detect roles, write VXD_AClinkName).
    ///   • Nothing relevant selected
    ///       → main form listing all systems.  From there the user can open a
    ///         system, or click "Create System" to pick units in the model.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AcLinkCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc   = uiDoc?.Document;
            if (doc == null) return Result.Cancelled;

            try
            {
                // Lazy copy protection: units whose id-stamp no longer matches their
                // own ElementId are copies / arrays / pasted groups. A full outdoor+
                // indoor set copied together onto one level (e.g. a typical design
                // pasted onto another floor) is offered as a brand-new system instead
                // of being detached as a stray duplicate.
                var copyPlan = AcLinkRepository.PlanCopyCleanup(doc);
                bool promote = copyPlan.ToPromote.Count > 0 && OfferSystemCopyPromotion(copyPlan.ToPromote);
                AcLinkRepository.ApplyCopyCleanup(doc, copyPlan, promote);

                int detached = copyPlan.ToDetach.Count +
                    (promote ? 0 : copyPlan.ToPromote.Sum(g => g.Ids.Count));
                if (detached > 0)
                    TaskDialog.Show("AC Link",
                        $"{detached} copied unit(s) were detached from their system " +
                        "(duplicate of an existing unit). Re-link them if intended.");
                if (promote)
                    TaskDialog.Show("AC Link",
                        $"Created {copyPlan.ToPromote.Count} new system(s) from fully copied unit set(s).");

                // Migrate existing (key-less) systems to system keys — with confirmation.
                OfferLegacyKeyMigration(doc);

                // Cross-model audits: duplicate outdoors and name collisions/drift.
                WarnDuplicateOutdoors(doc);
                WarnNameIssues(doc);

                var selectedEquip = GetSelectedEquipment(uiDoc, doc);

                if (selectedEquip.Count > 0)
                {
                    var units = selectedEquip.Select(AcLinkRepository.ReadUnit).ToList();

                    // Identity = key when present, name for legacy units.
                    var idents = units.Where(u => u.IsLinked)
                        .Select(u => string.IsNullOrWhiteSpace(u.SysKey)
                            ? "N:" + (u.BaseName ?? "").ToUpperInvariant()
                            : "K:" + u.SysKey.ToUpperInvariant())
                        .Distinct()
                        .ToList();

                    bool allLinkedToOneSystem =
                        units.All(u => u.IsLinked) && idents.Count == 1;

                    if (allLinkedToOneSystem)
                        ShowSystem(uiApp, uiDoc, doc, units[0].SysKey, units[0].BaseName);
                    else
                        RunCreate(uiApp, uiDoc, doc, units);

                    return Result.Succeeded;
                }

                // Nothing selected → main systems form.
                var systems = AcLinkRepository.GetSystems(doc);
                var main = new MainDialog(systems, uiDoc);
                SetOwner(main, uiApp);
                main.ShowDialog();

                if (main.DialogResult == true)
                {
                    if (main.CreateRequested)
                    {
                        // Dialog is closed → Revit can enter pick mode.
                        var picked = PickEquipment(uiDoc);
                        if (picked != null && picked.Count > 0)
                            RunCreate(uiApp, uiDoc, doc,
                                      picked.Select(AcLinkRepository.ReadUnit).ToList());
                    }
                    else if (main.CreateIndoorRequested)
                    {
                        CreateIndoorFromSpaceCommand.Run(uiApp, ref message);
                    }
                    else if (main.CreateOutdoorRequested)
                    {
                        CreateOutdoorSystemCommand.Run(uiApp, ref message);
                    }
                    else if (main.MappingsRequested)
                    {
                        CatalogMappingCommand.Run(uiApp, ref message);
                    }
                    else if (!string.IsNullOrEmpty(main.SelectedBaseName))
                    {
                        ShowSystem(uiApp, uiDoc, doc, main.SelectedSysKey, main.SelectedBaseName);
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("AC Link — Error", ex.ToString());
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ── Create / assign ──────────────────────────────────────────────────

        internal static void RunCreate(UIApplication uiApp, UIDocument uiDoc, Document doc,
            List<AcUnit> units, bool prefillRoles = true)
        {
            if (prefillRoles) UnitClassifier.PrefillRoles(units);

            string suggested = units.FirstOrDefault(u => u.IsLinked)?.BaseName
                               ?? AcLinkRepository.SuggestNextBaseName(doc);
            string comments  = units.Select(u => u.Comments)
                                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";

            // Dialog loop: on a name collision the dialog re-opens with all the
            // user's edits (name, roles, comments) preserved.
            while (true)
            {
                var dlg = new CreateSystemDialog(units, suggested, comments);
                SetOwner(dlg, uiApp);
                dlg.ShowDialog();
                if (dlg.DialogResult != true) return;

                // Carry the user's edits into the next round in case we loop back.
                suggested = dlg.SystemBaseName;
                comments  = dlg.Comments;
                var roleById = dlg.Rows.ToDictionary(r => r.Id, r => r.IsOutdoor);
                foreach (var u in units)
                    if (roleById.TryGetValue(u.Id, out bool o)) u.IsOutdoor = o;

                var roles = dlg.Rows.Select(r => (r.Id, r.IsOutdoor)).ToList();
                if (!ResolveSystemKey(doc, dlg.SystemBaseName, dlg.SystemKeyInput, dlg.IsCrossModel, roles, out string sysKey))
                    continue;   // back to the dialog

                // Is the model currently in "By system" colour mode? (capture before assign)
                bool colored = ColorOverrideService.IsColored(uiDoc, AcLinkRepository.GetSystems(doc));

                AcLinkRepository.AssignSystem(doc, dlg.SystemBaseName, sysKey, roles, dlg.Comments);

                // Colour the newly created system too, if colour mode is active.
                if (colored)
                    ColorOverrideService.Apply(uiDoc, AcLinkRepository.GetSystems(doc));

                // Re-read and open the freshly created system.
                ShowSystem(uiApp, uiDoc, doc, sysKey, dlg.SystemBaseName);
                return;
            }
        }

        /// <summary>
        /// Determines the system KEY for the units being assigned.
        ///   • A pasted key wins — that is the explicit cross-model join.
        ///   • A fresh name → a new key.
        ///   • The name of one existing system → explicit Yes/No join (blocked if it
        ///     would produce a second outdoor unit).
        ///   • A name used by several systems → rejected, pick another name.
        /// Returns false to send the user back to the create dialog.
        /// </summary>
        internal static bool ResolveSystemKey(Document doc, string baseName, string keyInput,
            bool isCrossModel, IEnumerable<(ElementId id, bool isOutdoor)> roles, out string sysKey)
        {
            sysKey = null;
            var rolesList     = roles.ToList();
            var selectedIds   = new HashSet<ElementId>(rolesList.Select(r => r.id));
            var systems       = AcLinkRepository.GetSystems(doc);
            bool selHasOutdoor = rolesList.Any(r => r.isOutdoor);

            // ── Explicit key paste (join a system whose model is not linked here) ─
            string pasted = (keyInput ?? "").Trim().ToUpperInvariant();
            if (pasted.Length > 0)
            {
                var target = systems.FirstOrDefault(s =>
                    string.Equals(s.SysKey, pasted, StringComparison.OrdinalIgnoreCase));
                if (target != null && selHasOutdoor &&
                    target.AllUnits.Any(u => u.IsOutdoor && (u.IsRemote || !selectedIds.Contains(u.Id))))
                {
                    TaskDialog.Show("AC Link",
                        $"System with key {pasted} already has an outdoor unit.\n" +
                        "Uncheck \"Outdoor\" to join it with indoor units only.");
                    return false;
                }
                sysKey = pasted;
                return true;
            }

            // ── Systems already using this NAME with units beyond the selection ──
            var candidates = systems
                .Where(s => string.Equals(s.BaseName, baseName, StringComparison.OrdinalIgnoreCase))
                .Select(s => new
                {
                    Sys = s,
                    Foreign = s.AllUnits.Where(u => u.IsRemote || !selectedIds.Contains(u.Id)).ToList()
                })
                .Where(x => x.Foreign.Count > 0)
                .ToList();

            if (candidates.Count == 0)
            {
                sysKey = AcLinkParams.NewSystemKey(isCrossModel);
                return true;
            }

            if (candidates.Count > 1)
            {
                TaskDialog.Show("AC Link — name collision",
                    $"The name \"{baseName}\" is already used by {candidates.Count} DIFFERENT systems " +
                    $"(keys: {string.Join(", ", candidates.Select(c => c.Sys.KeyLabel))}).\n\n" +
                    "Choose another name.");
                return false;
            }

            var cand  = candidates[0];
            string where = string.Join(", ", cand.Foreign.Select(u => u.ModelLabel).Distinct());

            if (selHasOutdoor && cand.Foreign.Any(u => u.IsOutdoor))
            {
                TaskDialog.Show("AC Link — name collision",
                    $"System \"{baseName}\" (key {cand.Sys.KeyLabel}) already exists ({where}) " +
                    "and already has an outdoor unit.\n\n" +
                    "Choose another name, or uncheck \"Outdoor\" to join it with indoor units only.");
                return false;
            }

            var td = new TaskDialog("AC Link — name collision")
            {
                MainInstruction = $"System \"{baseName}\" already exists (key {cand.Sys.KeyLabel})",
                MainContent =
                    $"It already has {cand.Foreign.Count} unit(s) in: {where}.\n\n" +
                    "JOIN the selected units to that existing system?\n" +
                    "Choose No to go back and pick a different name.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (td.Show() != TaskDialogResult.Yes) return false;

            sysKey = string.IsNullOrEmpty(cand.Sys.SysKey)
                ? AcLinkParams.NewSystemKey(isCrossModel)      // joining a legacy system starts its key
                : cand.Sys.SysKey;                             // existing key carries its own cross flag
            return true;
        }

        /// <summary>
        /// Creates a system from units with already-known roles (e.g. a freshly
        /// placed outdoor unit + the indoor units it was sized for) without opening
        /// <see cref="CreateSystemDialog"/> — used by the "auto-create" catalog
        /// setting. Falls back to the interactive dialog on a real collision that
        /// needs a user decision, so the existing safety prompts are never skipped.
        /// </summary>
        internal static void AutoAssignSystem(UIApplication uiApp, UIDocument uiDoc, Document doc, List<AcUnit> units)
        {
            string suggested = AcLinkRepository.SuggestNextBaseName(doc);
            var roles = units.Select(u => (u.Id, u.IsOutdoor)).ToList();

            if (!ResolveSystemKey(doc, suggested, "", false, roles, out string sysKey))
            {
                RunCreate(uiApp, uiDoc, doc, units, prefillRoles: false);
                return;
            }

            bool colored = ColorOverrideService.IsColored(uiDoc, AcLinkRepository.GetSystems(doc));
            string comments = units.Select(u => u.Comments).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";
            AcLinkRepository.AssignSystem(doc, suggested, sysKey, roles, comments);
            if (colored) ColorOverrideService.Apply(uiDoc, AcLinkRepository.GetSystems(doc));

            ShowSystem(uiApp, uiDoc, doc, sysKey, suggested);
        }

        // ── Show one system ────────────────────────────────────────────────────

        internal static void ShowSystem(UIApplication uiApp, UIDocument uiDoc, Document doc,
                                       string sysKey, string baseName)
        {
            // Edit loop: the dialog signals an action and closes; we mutate the model
            // and re-open with fresh data.  No unsaved state ever lives in the grid.
            while (true)
            {
                var allSystems = AcLinkRepository.GetSystems(doc);

                // Identity: by key when we have one; by name for legacy systems.
                var sys = !string.IsNullOrWhiteSpace(sysKey)
                    ? allSystems.FirstOrDefault(s =>
                          string.Equals(s.SysKey, sysKey, StringComparison.OrdinalIgnoreCase))
                    : allSystems.FirstOrDefault(s =>
                          string.IsNullOrEmpty(s.SysKey) &&
                          string.Equals(s.BaseName, baseName, StringComparison.OrdinalIgnoreCase));
                sys = sys ?? allSystems.FirstOrDefault(s =>
                          string.Equals(s.BaseName, baseName, StringComparison.OrdinalIgnoreCase));

                if (sys == null)
                {
                    TaskDialog.Show("AC Link", $"System \"{baseName}\" was not found.");
                    return;
                }
                sysKey   = sys.SysKey;     // may have been assigned by migration meanwhile
                baseName = sys.BaseName;

                var dlg = new SystemElementsDialog(sys, uiDoc);
                SetOwner(dlg, uiApp);
                dlg.ShowDialog();

                // Persist the system comment if the user changed it (any exit path).
                string comment = dlg.Comments ?? "";
                if (comment != (sys.Comments ?? ""))
                    AcLinkRepository.SetSystemComments(doc, baseName, sysKey, comment);

                // Is the model in "By system" colour mode right now? (capture before mutation)
                bool colored = ColorOverrideService.IsColored(uiDoc, allSystems);

                switch (dlg.Action)
                {
                    case SystemDialogAction.AddUnits:
                        var picked = PickEquipment(uiDoc);
                        if (picked != null && picked.Count > 0)
                        {
                            AcLinkRepository.AddUnits(doc, baseName, sysKey,
                                picked.Select(p => p.Id), comment);
                            if (colored)
                                ColorOverrideService.Apply(uiDoc, AcLinkRepository.GetSystems(doc));
                        }
                        continue;

                    case SystemDialogAction.Remove:
                        if (dlg.SelectedIds.Count > 0)
                        {
                            AcLinkRepository.RemoveUnits(doc, dlg.SelectedIds);
                            if (colored)
                            {
                                ColorOverrideService.ClearElements(uiDoc, dlg.SelectedIds);
                                ColorOverrideService.Apply(uiDoc, AcLinkRepository.GetSystems(doc));
                            }
                        }
                        continue;

                    case SystemDialogAction.SetOutdoor:
                        if (dlg.SelectedIds.Count == 1)
                            AcLinkRepository.SetOutdoor(doc, baseName, sysKey, dlg.SelectedIds[0]);
                        continue;

                    case SystemDialogAction.ToggleCross:
                        bool makeCross = !sys.IsCrossModel;
                        if (!makeCross && sys.AllUnits.Any(u => u.IsRemote))
                        {
                            var td = new TaskDialog("AC Link")
                            {
                                MainInstruction = "Make this system in-model?",
                                MainContent =
                                    "This system currently has units in linked models.\n" +
                                    "They will DETACH (keep the old cross key) and this model's " +
                                    "half becomes a plain in-model system.\n\nContinue?",
                                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                                DefaultButton = TaskDialogResult.No
                            };
                            if (td.Show() != TaskDialogResult.Yes) continue;
                        }
                        sysKey = AcLinkRepository.SetCrossModel(doc, sys, makeCross);
                        continue;

                    case SystemDialogAction.SetKey:
                        if (string.IsNullOrWhiteSpace(dlg.KeyInput)) continue;

                        // Merging with another HOST system under that key? Confirm.
                        var clash = allSystems.FirstOrDefault(s =>
                            s != sys &&
                            string.Equals(s.SysKey, dlg.KeyInput, StringComparison.OrdinalIgnoreCase) &&
                            s.AllUnits.Any(u => !u.IsRemote));
                        if (clash != null)
                        {
                            var td2 = new TaskDialog("AC Link")
                            {
                                MainInstruction = $"Key {dlg.KeyInput} already belongs to system \"{clash.BaseName}\"",
                                MainContent = "Setting this key will MERGE the two systems in this model. Continue?",
                                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                                DefaultButton = TaskDialogResult.No
                            };
                            if (td2.Show() != TaskDialogResult.Yes) continue;
                        }

                        AcLinkRepository.SetSystemKey(doc, sys, dlg.KeyInput);
                        sysKey = dlg.KeyInput;
                        continue;

                    default:
                        return;   // Close
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Offers (with confirmation, never silently) to turn fully-duplicated
        /// systems into brand-new systems instead of detaching the copies as
        /// stray duplicates. See <see cref="AcLinkRepository.PlanCopyCleanup"/>.
        /// </summary>
        private static bool OfferSystemCopyPromotion(List<AcLinkRepository.CopiedSystemGroup> groups)
        {
            var lines = groups.Select(g =>
                $"•  {g.OldBaseName} (copy)" +
                (string.IsNullOrEmpty(g.LevelLabel) ? "" : $"  @ {g.LevelLabel}") +
                $"  ({g.Ids.Count} units)  →  new system {g.BaseName}");

            var td = new TaskDialog("AC Link — copied systems")
            {
                MainInstruction =
                    $"{groups.Count} complete system(s) look copied as a whole — create new system(s) for them?",
                MainContent =
                    "These pasted units carry the same system key as their source, still form a " +
                    "complete outdoor+indoor set, and landed together on one level — a typical " +
                    "sign of a design copied onto another floor rather than a stray duplicate:\n\n" +
                    string.Join("\n", lines) +
                    "\n\nYes creates a new system for each set below (fresh key, next free name).\n" +
                    "No detaches them instead, like any other copy.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.Yes
            };
            return td.Show() == TaskDialogResult.Yes;
        }

        /// <summary>
        /// Offers (with confirmation, never silently) to stamp system keys onto
        /// legacy host units that predate the key mechanism.  A same-named keyed
        /// system in a loaded link donates its key, joining the halves.
        /// </summary>
        private static void OfferLegacyKeyMigration(Document doc)
        {
            var plans = AcLinkRepository.PlanLegacyKeys(doc);
            if (plans.Count == 0) return;

            var lines = plans.Select(p => $"•  {p.BaseName}  →  {p.Key}");

            var td = new TaskDialog("AC Link — system keys")
            {
                MainInstruction = $"Assign system keys to {plans.Count} existing system(s)?",
                MainContent =
                    "AC Link now identifies systems by a unique key (the name is just a label).\n" +
                    "These systems in this model have no key yet:\n\n" +
                    string.Join("\n", lines) +
                    "\n\nUnits in linked models receive their key when that model is opened.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.Yes
            };
            if (td.Show() == TaskDialogResult.Yes)
                AcLinkRepository.ApplyLegacyKeys(doc, plans);
        }

        /// <summary>Warns about duplicate names (different keys) and name drift within a key.</summary>
        private static void WarnNameIssues(Document doc)
        {
            var systems = AcLinkRepository.GetSystems(doc);
            var lines = new List<string>();

            foreach (var g in systems.GroupBy(s => s.BaseName, StringComparer.OrdinalIgnoreCase)
                                     .Where(g => g.Count() > 1))
                lines.Add($"•  Name \"{g.Key}\" is used by {g.Count()} DIFFERENT systems " +
                          $"(keys: {string.Join(", ", g.Select(s => s.KeyLabel))}) — rename one.");

            foreach (var s in systems.Where(s => s.HasNameMismatch))
                lines.Add($"•  System {s.KeyLabel} has units with different names — " +
                          $"rename them all to \"{s.BaseName}\".");

            if (lines.Count > 0)
                TaskDialog.Show("AC Link — audit", string.Join("\n", lines));
        }

        private static void WarnDuplicateOutdoors(Document doc)
        {
            var dupes = AcLinkRepository.GetSystems(doc)
                .Where(s => s.AllUnits.Count(u => u.IsOutdoor) > 1)
                .ToList();
            if (dupes.Count == 0) return;

            var lines = dupes.Select(s =>
            {
                var outs = s.AllUnits.Where(u => u.IsOutdoor)
                            .Select(u => u.ModelLabel).Distinct();
                return $"•  {s.BaseName}  —  outdoor units in: {string.Join(" and ", outs)}";
            });

            TaskDialog.Show("AC Link — name collision",
                "These systems have MORE THAN ONE outdoor unit. Most likely the same\n" +
                "system name was used independently in different models — rename one:\n\n" +
                string.Join("\n", lines));
        }

        internal static List<FamilyInstance> GetSelectedEquipment(UIDocument uiDoc, Document doc) =>
            uiDoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .OfType<FamilyInstance>()
                .Where(AcLinkRepository.IsMechanicalEquipment)
                .ToList();

        /// <summary>Lets the user pick Mechanical Equipment units in the model.</summary>
        internal static List<FamilyInstance> PickEquipment(UIDocument uiDoc)
        {
            try
            {
                var refs = uiDoc.Selection.PickObjects(
                    ObjectType.Element,
                    new EquipmentSelectionFilter(),
                    "Select the AC units (indoor + outdoor). Click Finish to continue, ESC to cancel.");

                return refs.Select(r => uiDoc.Document.GetElement(r))
                           .OfType<FamilyInstance>()
                           .ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        internal static void SetOwner(System.Windows.Window w, UIApplication uiApp) =>
            new System.Windows.Interop.WindowInteropHelper(w).Owner = uiApp.MainWindowHandle;
    }

    /// <summary>Allows only Mechanical Equipment family instances to be picked.</summary>
    public class EquipmentSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => AcLinkRepository.IsMechanicalEquipment(elem);
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
