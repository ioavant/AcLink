using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace AcLink
{
    /// <summary>
    /// AC Link ribbon application — a Vixeldorf product. Shares the "Vixeldorf"
    /// ribbon tab and "About"/Help Center button with other Vixeldorf add-ins
    /// (e.g. Link Section Box) using the same AppDomain-flag dedup convention,
    /// so only one Help Center button appears regardless of load order.
    /// </summary>
    public class App : IExternalApplication
    {
        private const string TabName = "Vixeldorf";
        private const string AboutPanelName = "About";
        private const string HelpUrl = "https://www.vixeldorf.com";

        public Result OnStartup(UIControlledApplication application)
        {
            // Fire-and-forget: never blocks startup, never throws into Revit.
            UpdateChecker.CheckInBackground();

            string tabName = TabName;
            CreateTabIfNotExists(application, tabName);

            RibbonPanel panel = GetOrCreatePanel(application, tabName, "AC Link");
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // Shared F1 (contextual) help target for every button.
            ContextualHelp help = new ContextualHelp(ContextualHelpType.Url, HelpUrl);

            var buttonData = new PushButtonData(
                "AcLinkBtn", "AC\nLink", assemblyPath, "AcLink.AcLinkCommand")
            {
                ToolTip    = "Link indoor and outdoor AC units into a system",
                LargeImage = LoadImage("aclink_32.png")
            };
            buttonData.SetContextualHelp(help);
            panel.AddItem(buttonData);

            panel.AddSeparator();

            var createIndoorBtn = new PushButtonData(
                "AcLinkCreateIndoorBtn", "Indoor Unit\nfrom Space", assemblyPath, "AcLink.CreateIndoorFromSpaceCommand")
            {
                ToolTip    = "Pick a Space and place an indoor unit sized to its cooling load",
                LargeImage = LoadImage("aclink_32.png")
            };
            createIndoorBtn.SetContextualHelp(help);
            panel.AddItem(createIndoorBtn);

            var createOutdoorBtn = new PushButtonData(
                "AcLinkCreateOutdoorBtn", "Outdoor Unit\n+ System", assemblyPath, "AcLink.CreateOutdoorSystemCommand")
            {
                ToolTip    = "Place an outdoor unit sized for the selected indoor units and link them into a system",
                LargeImage = LoadImage("aclink_32.png")
            };
            createOutdoorBtn.SetContextualHelp(help);
            panel.AddItem(createOutdoorBtn);

            var mappingBtn = new PushButtonData(
                "AcLinkCatalogMappingBtn", "Catalog\nMappings", assemblyPath, "AcLink.CatalogMappingCommand")
            {
                ToolTip    = "Configure indoor/outdoor family + catalog mappings used by unit creation",
                LargeImage = LoadImage("aclink_32.png")
            };
            mappingBtn.SetContextualHelp(help);
            panel.AddItem(mappingBtn);

            const string bimGuideKey = "Vixeldorf.BimGuide.Added";
            RibbonPanel aboutPanel = GetOrCreatePanel(application, tabName, AboutPanelName);
            if (AppDomain.CurrentDomain.GetData(bimGuideKey) == null)
            {
                AppDomain.CurrentDomain.SetData(bimGuideKey, true);
                var webButtonData = new PushButtonData(
                    "Vixeldorf_HelpCenter", "Help\nCenter",
                    assemblyPath, "AcLink.OpenWebPageCommand");
                webButtonData.SetContextualHelp(help);
                PushButton webButton = aboutPanel.AddItem(webButtonData) as PushButton;
                if (webButton != null)
                {
                    webButton.ToolTip = "Open the Vixeldorf Help Center web page.";
                    webButton.LargeImage = LoadImage("web_icon.png");
                }
            }

            // The "About" panel must stay at the very end of the tab even when other
            // Vixeldorf add-ins add their own panels later. There is no supported API to
            // reorder ribbon panels; ApplicationInitialized can fire before the ribbon is
            // fully built (and never fires when the add-in loads after Revit has started),
            // so we reorder on the first Idling tick — raised once, when the ribbon exists.
            _uiApp = application;
            application.Idling += OnFirstIdling;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        private UIControlledApplication _uiApp;

        private void OnFirstIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            _uiApp.Idling -= OnFirstIdling;   // one-shot: only the first idle
            MoveAboutPanelToEnd();
        }

        // Uses the (unofficial but long-stable) Autodesk.Windows ribbon model to move
        // the shared "About" panel to the end of the tab. Wrapped so it can never
        // break startup.
        private static void MoveAboutPanelToEnd()
        {
            try
            {
                Autodesk.Windows.RibbonControl ribbon = Autodesk.Windows.ComponentManager.Ribbon;
                if (ribbon == null) return;

                foreach (Autodesk.Windows.RibbonTab tab in ribbon.Tabs)
                {
                    if (tab.Title != TabName && tab.Id != TabName) continue;

                    Autodesk.Windows.RibbonPanel about = null;
                    foreach (Autodesk.Windows.RibbonPanel p in tab.Panels)
                        if (p.Source != null && p.Source.Title != null &&
                            p.Source.Title.Trim().Equals(AboutPanelName, StringComparison.OrdinalIgnoreCase)) { about = p; break; }

                    if (about != null && tab.Panels[tab.Panels.Count - 1] != about)
                    {
                        tab.Panels.Remove(about);
                        tab.Panels.Add(about);
                    }
                    break;
                }
            }
            catch { /* Never let a ribbon-cosmetics tweak interfere with loading. */ }
        }

        private void CreateTabIfNotExists(UIControlledApplication app, string tabName)
        {
            try { app.CreateRibbonTab(tabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab exists — OK */ }
        }

        private RibbonPanel GetOrCreatePanel(UIControlledApplication app, string tabName, string panelName)
        {
            foreach (RibbonPanel p in app.GetRibbonPanels(tabName))
                if (p.Name == panelName) return p;
            return app.CreateRibbonPanel(tabName, panelName);
        }

        private BitmapImage LoadImage(string resourceName)
        {
            string fullName = $"AcLink.Resources.{resourceName}";
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(fullName);
            if (stream == null) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption  = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
    }

    // ── Vixeldorf Help Center command — shared convention across Vixeldorf add-ins ─

    [Transaction(TransactionMode.Manual)]
    public class OpenWebPageCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.vixeldorf.com") { UseShellExecute = true });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
