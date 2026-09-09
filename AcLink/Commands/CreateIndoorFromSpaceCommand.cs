using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Linq;

namespace AcLink
{
    /// <summary>
    /// Pick a Space that already carries a cooling-load value, pick a matching
    /// indoor unit from a configured catalog, and place it.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateIndoorFromSpaceCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            Run(commandData.Application, ref message);

        /// <summary>
        /// The command body, reusable from <see cref="MainDialog"/>'s own button as
        /// well as from the ribbon — both just need a UIApplication.
        /// </summary>
        internal static Result Run(UIApplication uiApp, ref string message)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc   = uiDoc?.Document;
            if (doc == null) return Result.Cancelled;

            try
            {
                Reference spaceRef;
                try
                {
                    spaceRef = uiDoc.Selection.PickObject(ObjectType.Element, new SpaceSelectionFilter(),
                        "Select a space to size the indoor unit for. ESC to cancel.");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }

                var space = doc.GetElement(spaceRef);
                if (space == null) { message = "Selected element is not a Space."; return Result.Failed; }

                double targetBtuH = LoadFinder.CoolingLoadBtuH(space);

                var settings = AcCatalogSettings.Load();
                var indoorMappings = settings.Mappings.Where(m => m.Role == AcCatalogRole.Indoor && m.IsUsable).ToList();
                if (indoorMappings.Count == 0)
                {
                    settings = CatalogMappingDialog.ShowAndSave(uiApp, doc,
                        "No indoor unit catalog is configured yet. Add at least one mapping (Role = Indoor), then continue.");
                    if (settings == null) return Result.Cancelled;
                    indoorMappings = settings.Mappings.Where(m => m.Role == AcCatalogRole.Indoor && m.IsUsable).ToList();
                    if (indoorMappings.Count == 0) return Result.Cancelled;
                }

                var dlg = new PickCatalogUnitDialog(doc, indoorMappings, targetBtuH, settings.TolerancePercent);
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

                TaskDialog.Show("AC Link",
                    $"Placed \"{dlg.SelectedCandidate.TypeName}\" — {dlg.SelectedCandidate.CapacityText} " +
                    $"({dlg.SelectedCandidate.DeviationText} off the target).");
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

    /// <summary>Allows only MEP Space elements to be picked.</summary>
    public class SpaceSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) =>
            e?.Category != null && e.Category.Id == new ElementId(BuiltInCategory.OST_MEPSpaces);
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
