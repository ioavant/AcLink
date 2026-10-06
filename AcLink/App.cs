using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace AcLink
{
    /// <summary>
    /// AC Link ribbon application. Everything brand-specific (tab, panel, About
    /// button, help URL, update policy) comes from the generated <c>Brand</c>
    /// class — see ..\branding.props and ..\..\_branding.
    /// </summary>
    public class App : IExternalApplication
    {
        private UIControlledApplication _uiApp;

        public Result OnStartup(UIControlledApplication application)
        {
            // Fire-and-forget: never blocks startup, never throws into Revit.
            if (Brand.UpdateCheckEnabled) UpdateChecker.CheckInBackground();

            string tab = Brand.RibbonTab;
            try { application.CreateRibbonTab(tab); } catch { /* tab exists — OK */ }

            RibbonPanel panel = GetOrCreatePanel(application, tab, Brand.RibbonPanel);
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // Shared F1 (contextual) help target for every button.
            ContextualHelp help = new ContextualHelp(ContextualHelpType.Url, Brand.HelpUrl);

            // Three small (16x16) buttons stacked in one column.
            var buttonData = new PushButtonData(
                "AcLinkBtn", "AC Link", assemblyPath, "AcLink.AcLinkCommand")
            {
                ToolTip = "Link indoor and outdoor AC units into a system",
                Image   = LoadImage("aclink_16.png")
            };
            buttonData.SetContextualHelp(help);

            var createIndoorBtn = new PushButtonData(
                "AcLinkCreateIndoorBtn", "Indoor from Space", assemblyPath, "AcLink.CreateIndoorFromSpaceCommand")
            {
                ToolTip = "Pick a Space and place an indoor unit sized to its cooling load",
                Image   = LoadImage("aclink_indoor_16.png")
            };
            createIndoorBtn.SetContextualHelp(help);

            var createOutdoorBtn = new PushButtonData(
                "AcLinkCreateOutdoorBtn", "Outdoor + System", assemblyPath, "AcLink.CreateOutdoorSystemCommand")
            {
                ToolTip = "Place an outdoor unit sized for the selected indoor units and link them into a system",
                Image   = LoadImage("aclink_outdoor_16.png")
            };
            createOutdoorBtn.SetContextualHelp(help);

            panel.AddStackedItems(buttonData, createIndoorBtn, createOutdoorBtn);

            // Catalog Mappings has no ribbon button of its own — it's reached from
            // the "Catalog Mappings…" button on the main AC Link window (MainDialog),
            // same as the other two catalog commands.

            // ── About / Help button ───────────────────────────────────────────
            // Every add-in of this brand shares one "About" panel holding a single help
            // button, so only the first one loaded gets to add it. Two independent
            // checks, because neither alone covers every case:
            //   • an AppDomain data slot — all add-ins in a Revit process share one
            //     AppDomain, and this catches siblings regardless of load order (the
            //     Vixeldorf convention);
            //   • a scan of the live ribbon via AdWindows — RibbonPanel.GetItems() only
            //     sees items the CURRENT add-in added, so this is the only way to notice
            //     a button placed by an older add-in of the same brand (the TES
            //     convention).
            RibbonPanel aboutPanel = GetOrCreatePanel(application, tab, Brand.AboutPanel);
            bool alreadyAdded = AppDomain.CurrentDomain.GetData(Brand.AboutFlagKey) != null
                                || AboutButtonExistsOnRibbon(tab, Brand.AboutPanel);
            if (!alreadyAdded)
            {
                AppDomain.CurrentDomain.SetData(Brand.AboutFlagKey, true);
                var webButtonData = new PushButtonData(
                    Brand.AboutButtonId, Brand.AboutButtonText,
                    assemblyPath, "AcLink.OpenWebPageCommand");
                webButtonData.SetContextualHelp(help);
                if (aboutPanel.AddItem(webButtonData) is PushButton webButton)
                {
                    webButton.ToolTip = Brand.AboutButtonTooltip;
                    webButton.LargeImage = LoadImage("web_icon.png");
                }
            }

            // The "About" panel must stay at the very end of the tab even when other
            // add-ins add their own panels later. There is no supported API to reorder
            // ribbon panels; ApplicationInitialized can fire before the ribbon is fully
            // built (and never fires when the add-in loads after Revit has started), so
            // we reorder on the first Idling tick — raised once, when the ribbon exists.
            if (Brand.PinAboutPanelToEnd)
            {
                _uiApp = application;
                application.Idling += OnFirstIdling;
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        private void OnFirstIdling(object sender, IdlingEventArgs e)
        {
            _uiApp.Idling -= OnFirstIdling;   // one-shot: only the first idle
            MoveAboutPanelToEnd();
        }

        private static bool AboutButtonExistsOnRibbon(string tabTitle, string panelTitle)
        {
            try
            {
                var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
                if (ribbon == null) return false;

                foreach (Autodesk.Windows.RibbonTab ribbonTab in ribbon.Tabs)
                {
                    if (!string.Equals(ribbonTab.Title, tabTitle, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (Autodesk.Windows.RibbonPanel ribbonPanel in ribbonTab.Panels)
                    {
                        if (ribbonPanel.Source == null ||
                            !string.Equals(ribbonPanel.Source.Title, panelTitle, StringComparison.OrdinalIgnoreCase))
                            continue;

                        foreach (Autodesk.Windows.RibbonItem item in ribbonPanel.Source.Items)
                            if (ItemTextMatchesAboutButton(item)) return true;
                    }
                }
            }
            catch { /* AdWindows unavailable — fall through and add the button */ }
            return false;
        }

        private static bool ItemTextMatchesAboutButton(Autodesk.Windows.RibbonItem item)
        {
            if (item == null) return false;
            string text = item.Text ?? string.Empty;
            if (text.IndexOf(Brand.AboutMatchText, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (item is Autodesk.Windows.RibbonRowPanel rowPanel)
                foreach (Autodesk.Windows.RibbonItem child in rowPanel.Items)
                    if (ItemTextMatchesAboutButton(child)) return true;

            return false;
        }

        // Uses the (unofficial but long-stable) Autodesk.Windows ribbon model to move
        // the shared "About" panel to the end of the tab. Wrapped so it can never
        // break startup.
        private static void MoveAboutPanelToEnd()
        {
            try
            {
                var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
                if (ribbon == null) return;

                foreach (Autodesk.Windows.RibbonTab tab in ribbon.Tabs)
                {
                    if (tab.Title != Brand.RibbonTab && tab.Id != Brand.RibbonTab) continue;

                    Autodesk.Windows.RibbonPanel about = null;
                    foreach (Autodesk.Windows.RibbonPanel p in tab.Panels)
                        if (p.Source != null && p.Source.Title != null &&
                            p.Source.Title.Trim().Equals(Brand.AboutPanel, StringComparison.OrdinalIgnoreCase))
                        { about = p; break; }

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

        private static RibbonPanel GetOrCreatePanel(UIControlledApplication app, string tab, string name)
        {
            foreach (RibbonPanel p in app.GetRibbonPanels(tab))
                if (p.Name == name) return p;
            return app.CreateRibbonPanel(tab, name);
        }

        private static BitmapImage LoadImage(string resourceName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var stream = asm.GetManifestResourceStream($"AcLink.Resources.{resourceName}");
                if (stream == null)
                    foreach (string n in asm.GetManifestResourceNames())
                        if (n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
                        { stream = asm.GetManifestResourceStream(n); break; }

                if (stream == null) return null;

                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption  = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }
    }

    // ── Vixeldorf / TES Help Center command — shared convention across add-ins ─

    [Transaction(TransactionMode.Manual)]
    public class OpenWebPageCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Brand.HelpUrl) { UseShellExecute = true });
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
