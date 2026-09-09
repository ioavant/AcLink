using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AcLink
{
    /// <summary>Opens the catalog-mapping management dialog (indoor/outdoor family + CSV pairs).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class CatalogMappingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            Run(commandData.Application, ref message);

        /// <summary>The command body, reusable from <see cref="MainDialog"/>'s own button.</summary>
        internal static Result Run(UIApplication uiApp, ref string message)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            if (uiDoc == null) { message = "No active document."; return Result.Failed; }

            var settings = CatalogMappingDialog.ShowAndSave(uiApp, uiDoc.Document, null);
            return settings == null ? Result.Cancelled : Result.Succeeded;
        }
    }
}
