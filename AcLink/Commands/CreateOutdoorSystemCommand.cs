using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

namespace AcLink
{
    /// <summary>
    /// Select the finished indoor units, sum their cooling load, pick a matching
    /// outdoor unit from a configured catalog, place it, and create the AC system
    /// from the selection + the new outdoor unit.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateOutdoorSystemCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            Run(commandData.Application, ref message);

        /// <summary>The command body, reusable from <see cref="MainDialog"/>'s own button.</summary>
        internal static Result Run(UIApplication uiApp, ref string message)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc   = uiDoc?.Document;
            if (doc == null) return Result.Cancelled;

            try
            {
                var indoorFis = AcLinkCommand.GetSelectedEquipment(uiDoc, doc);
                if (indoorFis.Count == 0)
                {
                    // Nothing pre-selected — fall into pick mode, same convention
                    // AcLinkCommand itself uses for "Create System"/"Add units".
                    indoorFis = AcLinkCommand.PickEquipment(uiDoc);
                    if (indoorFis == null || indoorFis.Count == 0) return Result.Cancelled;
                }
                var indoorUnits = indoorFis.Select(AcLinkRepository.ReadUnit).ToList();
                double targetBtuH = indoorUnits.Sum(u => u.CoolingLoadBtuH);

                var settings = AcCatalogSettings.Load();
                var outdoorMappings = settings.Mappings.Where(m => m.Role == AcCatalogRole.Outdoor && m.IsUsable).ToList();
                if (outdoorMappings.Count == 0)
                {
                    settings = CatalogMappingDialog.ShowAndSave(uiApp, doc,
                        "No outdoor unit catalog is configured yet. Add at least one mapping (Role = Outdoor), then continue.");
                    if (settings == null) return Result.Cancelled;
                    outdoorMappings = settings.Mappings.Where(m => m.Role == AcCatalogRole.Outdoor && m.IsUsable).ToList();
                    if (outdoorMappings.Count == 0) return Result.Cancelled;
                }

                var dlg = new PickCatalogUnitDialog(doc, outdoorMappings, targetBtuH, settings.TolerancePercent);
                AcLinkCommand.SetOwner(dlg, uiApp);
                dlg.ShowDialog();
                if (dlg.DialogResult != true) return Result.Cancelled;

                var placement = AcUnitPlacer.Place(uiDoc, dlg.SelectedCandidate, dlg.SelectedMapping);
                if (placement.Cancelled) return Result.Cancelled;
                if (!placement.Placed)
                {
                    if (!string.IsNullOrEmpty(placement.Message)) TaskDialog.Show("AC Link", placement.Message);
                    return Result.Failed;
                }

                var newOutdoorUnit = AcLinkRepository.ReadUnit(placement.Instance);
                newOutdoorUnit.IsOutdoor = true;
                foreach (var u in indoorUnits) u.IsOutdoor = false;
                var allUnits = new List<AcUnit>(indoorUnits) { newOutdoorUnit };

                if (settings.AutoCreateSystem)
                    AcLinkCommand.AutoAssignSystem(uiApp, uiDoc, doc, allUnits);
                else
                    AcLinkCommand.RunCreate(uiApp, uiDoc, doc, allUnits, prefillRoles: false);

                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("AC Link — Error", ex.ToString());
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
