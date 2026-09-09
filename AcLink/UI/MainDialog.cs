using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using Binding = System.Windows.Data.Binding;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AcLink
{
    /// <summary>
    /// Entry form: lists all AC systems in the project.
    ///   • "Create System" → closes the form so the command can let the user pick
    ///     Mechanical Equipment units in the model and build a system from them.
    ///   • Selecting a row (or double-click) + "Open" → show that system's units.
    /// </summary>
    public class MainDialog : Window
    {
        private class SysRow
        {
            public string BaseName { get; set; }
            public string Key { get; set; }
            public string Outdoor { get; set; }
            public int IndoorCount { get; set; }
            public string FactorFmt { get; set; }
            public string Level { get; set; }
            public string Models { get; set; }
            public string Comments { get; set; }
            public Brush ColorBrush { get; set; }
            public AcSystem System { get; set; }
        }

        private readonly DataGrid _grid;
        private readonly UIDocument _uiDoc;
        private readonly IList<AcSystem> _systems;

        /// <summary>Base name of the system the user chose to open, or null.</summary>
        public string SelectedBaseName { get; private set; }

        /// <summary>System key of the chosen system ("" for a legacy system).</summary>
        public string SelectedSysKey { get; private set; }

        /// <summary>True when the user asked to create a new system by picking units.</summary>
        public bool CreateRequested { get; private set; }

        /// <summary>True when the user asked to size/place an indoor unit from a Space.</summary>
        public bool CreateIndoorRequested { get; private set; }

        /// <summary>True when the user asked to size/place an outdoor unit + create its system.</summary>
        public bool CreateOutdoorRequested { get; private set; }

        /// <summary>True when the user asked to manage catalog mappings.</summary>
        public bool MappingsRequested { get; private set; }

        public MainDialog(IList<AcSystem> systems, UIDocument uiDoc)
        {
            _uiDoc = uiDoc;
            _systems = systems;

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            Title                 = $"AC Link v{ver.Major}.{ver.Minor}.{ver.Build}";
            Width                 = 980;
            Height                = 430;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background            = new SolidColorBrush(Color.FromRgb(245, 245, 245));

            var root = new DockPanel { Margin = new Thickness(14) };

            var header = new TextBlock
            {
                Text = "Air-conditioning systems in this project",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var updateBanner = BuildUpdateBanner();
            if (updateBanner != null)
            {
                DockPanel.SetDock(updateBanner, Dock.Top);
                root.Children.Add(updateBanner);
            }

            // ── Color scheme selector ──────────────────────────────────────────
            var schemeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            schemeRow.Children.Add(new TextBlock
            {
                Text = "Color scheme:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            var schemeCombo = new ComboBox { Width = 150 };
            schemeCombo.Items.Add("None");
            schemeCombo.Items.Add("By system");
            // Reflect the model's real state: if the systems are already coloured,
            // show "By system" (survives the form being closed/reopened and the
            // add-in assembly being reloaded by AddIn Manager).
            schemeCombo.SelectedIndex = ColorOverrideService.IsColored(uiDoc, systems) ? 1 : 0;
            schemeCombo.SelectionChanged += OnSchemeChanged;       // attached after initial index → no early fire
            schemeRow.Children.Add(schemeCombo);
            DockPanel.SetDock(schemeRow, Dock.Top);
            root.Children.Add(schemeRow);

            // ── Buttons (bottom) ───────────────────────────────────────────────
            var btnRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };

            var leftButtons = new StackPanel { Orientation = Orientation.Horizontal };

            var createBtn = new Button
            {
                Content = "Create System…",
                Width = 130,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Pick indoor and outdoor units in the model and link them into a system"
            };
            createBtn.Click += (s, e) => { CreateRequested = true; DialogResult = true; };
            leftButtons.Children.Add(createBtn);

            var indoorBtn = new Button
            {
                Content = "Indoor from Space…",
                Width = 140,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Pick a Space and place an indoor unit sized to its cooling load"
            };
            indoorBtn.Click += (s, e) => { CreateIndoorRequested = true; DialogResult = true; };
            leftButtons.Children.Add(indoorBtn);

            var outdoorBtn = new Button
            {
                Content = "Outdoor + System…",
                Width = 140,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Place an outdoor unit sized for the selected indoor units and link them into a system"
            };
            outdoorBtn.Click += (s, e) => { CreateOutdoorRequested = true; DialogResult = true; };
            leftButtons.Children.Add(outdoorBtn);

            var mappingsBtn = new Button
            {
                Content = "Catalog Mappings…",
                Width = 130,
                Height = 28,
                ToolTip = "Configure indoor/outdoor family + catalog mappings used by unit creation"
            };
            mappingsBtn.Click += (s, e) => { MappingsRequested = true; DialogResult = true; };
            leftButtons.Children.Add(mappingsBtn);

            DockPanel.SetDock(leftButtons, Dock.Left);
            btnRow.Children.Add(leftButtons);

            var rightButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var showBtn = new Button { Content = "Show", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            showBtn.ToolTip = "Highlight all units of the selected system in the model";
            showBtn.Click += (s, e) => ShowSelected();
            var openBtn = new Button { Content = "Open", Width = 100, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            openBtn.Click += (s, e) => TriggerOpen();
            var closeBtn = new Button { Content = "Close", Width = 90, Height = 28, IsCancel = true };
            closeBtn.Click += (s, e) => DialogResult = false;
            rightButtons.Children.Add(showBtn);
            rightButtons.Children.Add(openBtn);
            rightButtons.Children.Add(closeBtn);
            btnRow.Children.Add(rightButtons);

            DockPanel.SetDock(btnRow, Dock.Bottom);
            root.Children.Add(btnRow);

            if (systems == null || systems.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "No systems found.\nClick “Create System”, or select Mechanical Equipment units and run the command.",
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                    FontStyle = FontStyles.Italic
                });
                Content = root;
                return;
            }

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly          = true,
                CanUserAddRows      = false,
                SelectionMode       = DataGridSelectionMode.Single,
                HeadersVisibility   = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                Background          = Brushes.White
            };
            _grid.Columns.Add(MakeColorSwatchColumn());
            _grid.Columns.Add(Col("System", nameof(SysRow.BaseName), 90));
            _grid.Columns.Add(Col("Key", nameof(SysRow.Key), 80));
            _grid.Columns.Add(Col("Outdoor Unit", nameof(SysRow.Outdoor), 200));
            _grid.Columns.Add(Col("Indoor", nameof(SysRow.IndoorCount), 60));
            _grid.Columns.Add(Col("Load factor", nameof(SysRow.FactorFmt), 80));
            _grid.Columns.Add(Col("Level", nameof(SysRow.Level), 120));
            _grid.Columns.Add(Col("Model", nameof(SysRow.Models), 110));
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header  = "Comments",
                Binding = new Binding(nameof(SysRow.Comments)),
                Width   = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _grid.MouseDoubleClick += (s, e) => TriggerOpen();

            var rows = new List<SysRow>();
            for (int i = 0; i < systems.Count; i++)
            {
                var sys = systems[i];
                var f = sys.OutdoorLoadFactor;
                var (r, g, b) = SystemColorPalette.Rgb(i);
                rows.Add(new SysRow
                {
                    BaseName    = sys.BaseName,
                    Key         = sys.KeyLabel,
                    Outdoor     = sys.OutdoorLabel,
                    IndoorCount = sys.IndoorCount,
                    FactorFmt   = f.HasValue ? $"{f.Value * 100:F0}%" : "—",
                    Level       = sys.LevelSummary,
                    Models      = sys.ModelSummary,
                    Comments    = sys.Comments,
                    ColorBrush  = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    System      = sys
                });
            }
            _grid.ItemsSource = rows;
            _grid.SelectedIndex = 0;

            root.Children.Add(_grid);
            Content = root;
        }

        private void TriggerOpen()
        {
            if (_grid?.SelectedItem is SysRow row)
            {
                SelectedBaseName = row.BaseName;
                SelectedSysKey   = row.System?.SysKey ?? "";
                DialogResult = true;
            }
        }

        /// <summary>Highlight + zoom to the host units of the selected system (dialog stays open).</summary>
        private void ShowSelected()
        {
            if (!(_grid?.SelectedItem is SysRow row) || row.System == null) return;

            // Remote units' ids belong to their linked documents — host units only here.
            var ids = row.System.AllUnits.Where(u => !u.IsRemote).Select(u => u.Id).ToList();
            if (ids.Count == 0)
            {
                MessageBox.Show(this,
                    "All units of this system are in linked models.\n" +
                    "Open the system to see their model names and element IDs.",
                    "AC Link", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                _uiDoc.ShowElements(ids);
                _uiDoc.Selection.SetElementIds(ids);
            }
            catch { /* Revit shows its own notice if it cannot display them */ }
        }

        /// <summary>
        /// A thin clickable banner shown when UpdateChecker found a newer release.
        /// Clicking opens the download URL in the default browser — there is no
        /// silent/automatic install, the user runs the downloaded MSI themselves.
        /// </summary>
        private static UIElement BuildUpdateBanner()
        {
            var info = UpdateChecker.AvailableUpdate;
            if (info == null) return null;

            var border = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(224, 242, 253)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0, 159, 220)),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(8, 5, 8, 5),
                Margin          = new Thickness(0, 0, 0, 8),
                Cursor          = System.Windows.Input.Cursors.Hand
            };
            border.Child = new TextBlock
            {
                Text = $"↑  AC Link {info.Version} is available — click to download",
                Foreground = new SolidColorBrush(Color.FromRgb(0, 90, 140)),
                FontWeight = FontWeights.SemiBold
            };
            border.MouseLeftButtonUp += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(info.DownloadUrl) { UseShellExecute = true }); }
                catch { /* ignore — user can still copy the URL from the version.json manually */ }
            };
            return border;
        }

        private void OnSchemeChanged(object sender, SelectionChangedEventArgs e)
        {
            bool color = (sender as ComboBox)?.SelectedIndex == 1;
            if (_systems == null || _systems.Count == 0) return;
            try
            {
                if (color) ColorOverrideService.Apply(_uiDoc, _systems);
                else       ColorOverrideService.Clear(_uiDoc, _systems);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "AC Link",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>A small coloured square showing the system's assigned colour.</summary>
        private static DataGridTemplateColumn MakeColorSwatchColumn()
        {
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(FrameworkElement.WidthProperty, 16.0);
            factory.SetValue(FrameworkElement.HeightProperty, 16.0);
            factory.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
            factory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            factory.SetValue(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(120, 120, 120)));
            factory.SetBinding(Border.BackgroundProperty, new Binding(nameof(SysRow.ColorBrush)));

            return new DataGridTemplateColumn
            {
                Header = "",
                Width = new DataGridLength(30),
                CellTemplate = new DataTemplate { VisualTree = factory }
            };
        }

        private static DataGridTextColumn Col(string header, string path, double w) =>
            new DataGridTextColumn
            {
                Header  = header,
                Binding = new Binding(path),
                Width   = new DataGridLength(w)
            };
    }
}
