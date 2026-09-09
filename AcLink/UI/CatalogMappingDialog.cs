using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Color = System.Windows.Media.Color;
using Binding = System.Windows.Data.Binding;

namespace AcLink
{
    /// <summary>
    /// Manages the list of catalog mappings (indoor/outdoor unit families + their
    /// CSV catalogs) used by "Create Indoor Unit from Space" and "Create Outdoor
    /// Unit + System". A master list (top) plus a detail panel (bottom) for the
    /// selected row — simpler in code-only WPF than per-cell editors, and a nicer
    /// place for the family/catalog pickers.
    /// </summary>
    internal class CatalogMappingDialog : Window
    {
        private class MappingRow : INotifyPropertyChanged
        {
            private string _label;
            private AcCatalogRole _role;
            private string _familyName;
            private string _catalogPath;

            public string Label { get => _label; set { _label = value; OnChanged(nameof(Label)); OnChanged(nameof(Display)); } }
            public AcCatalogRole Role { get => _role; set { _role = value; OnChanged(nameof(Role)); OnChanged(nameof(RoleText)); } }
            public string FamilyName { get => _familyName; set { _familyName = value; OnChanged(nameof(FamilyName)); OnChanged(nameof(Display)); } }
            public string CatalogPath { get => _catalogPath; set { _catalogPath = value; OnChanged(nameof(CatalogPath)); } }

            public string RoleText => Role.ToString();
            public string Display => string.IsNullOrWhiteSpace(Label) ? (FamilyName ?? "(new mapping)") : Label;

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

            public AcCatalogMapping ToMapping() => new AcCatalogMapping
            {
                Label = Label, Role = Role, FamilyName = FamilyName, CatalogPath = CatalogPath
            };

            public static MappingRow From(AcCatalogMapping m) => new MappingRow
            {
                Label = m.Label, Role = m.Role, FamilyName = m.FamilyName, CatalogPath = m.CatalogPath
            };
        }

        private readonly Document _doc;
        private readonly ObservableCollection<MappingRow> _rows;
        private readonly DataGrid _grid;

        private readonly TextBox _labelBox;
        private readonly ComboBox _roleBox;
        private readonly ComboBox _familyBox;
        private readonly TextBox _catalogBox;
        private readonly TextBox _toleranceBox;
        private readonly CheckBox _autoCreateBox;
        private readonly StackPanel _detailPanel;

        private MappingRow _editing;

        public AcCatalogSettings Settings { get; private set; }

        public CatalogMappingDialog(Document doc, AcCatalogSettings settings, string notice)
        {
            _doc = doc;
            settings = settings ?? new AcCatalogSettings();

            Title                 = "AC Link — Catalog Mappings";
            Width                 = 760;
            SizeToContent         = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background            = new SolidColorBrush(Color.FromRgb(245, 245, 245));
            ResizeMode            = ResizeMode.CanResize;

            _rows = new ObservableCollection<MappingRow>(settings.Mappings.Select(MappingRow.From));

            var root = new StackPanel { Margin = new Thickness(16) };

            if (!string.IsNullOrWhiteSpace(notice))
            {
                root.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(255, 244, 214)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(230, 200, 120)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap }
                });
            }

            root.Children.Add(Label("Mappings — each pairs a Revit family (must be loaded) with a type-catalog CSV/TXT:"));
            _grid = BuildGrid();
            root.Children.Add(_grid);

            var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
            var addBtn = new Button { Content = "Add", Width = 80, Height = 24, Margin = new Thickness(0, 0, 6, 0) };
            addBtn.Click += OnAdd;
            var removeBtn = new Button { Content = "Remove", Width = 80, Height = 24 };
            removeBtn.Click += OnRemove;
            listButtons.Children.Add(addBtn);
            listButtons.Children.Add(removeBtn);
            root.Children.Add(listButtons);

            root.Children.Add(Label("Selected mapping:"));
            _detailPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12), IsEnabled = false };

            _detailPanel.Children.Add(Label("Label"));
            _labelBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            _labelBox.TextChanged += (s, e) => { if (_editing != null) _editing.Label = _labelBox.Text; };
            _detailPanel.Children.Add(_labelBox);

            _detailPanel.Children.Add(Label("Role"));
            _roleBox = new ComboBox { Margin = new Thickness(0, 0, 0, 8), ItemsSource = Enum.GetValues(typeof(AcCatalogRole)) };
            _roleBox.SelectionChanged += (s, e) => { if (_editing != null && _roleBox.SelectedItem is AcCatalogRole r) _editing.Role = r; };
            _detailPanel.Children.Add(_roleBox);

            _detailPanel.Children.Add(Label("Family (must already be loaded in this project)"));
            _familyBox = new ComboBox { Margin = new Thickness(0, 0, 0, 8), IsEditable = true, ItemsSource = FamilyScanner.EquipmentFamilies(doc) };
            _familyBox.LostFocus += (s, e) => { if (_editing != null) _editing.FamilyName = _familyBox.Text; };
            _detailPanel.Children.Add(_familyBox);

            _detailPanel.Children.Add(Label("Catalog file (type-catalog CSV/TXT)"));
            var catalogRow = new DockPanel();
            var browseBtn = new Button { Content = "…", Width = 32, Height = 24 };
            browseBtn.Click += OnBrowseCatalog;
            DockPanel.SetDock(browseBtn, Dock.Right);
            _catalogBox = new TextBox { Margin = new Thickness(0, 0, 6, 0) };
            _catalogBox.TextChanged += (s, e) => { if (_editing != null) _editing.CatalogPath = _catalogBox.Text; };
            catalogRow.Children.Add(browseBtn);
            catalogRow.Children.Add(_catalogBox);
            catalogRow.Margin = new Thickness(0, 0, 0, 8);
            _detailPanel.Children.Add(catalogRow);

            root.Children.Add(_detailPanel);

            root.Children.Add(Label("Tolerance % (how far a candidate's capacity may be from the target)"));
            _toleranceBox = new TextBox
            {
                Text = settings.TolerancePercent.ToString("0.#"),
                Width = 80,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(_toleranceBox);

            _autoCreateBox = new CheckBox
            {
                Content = "Create the AC system automatically after placing the outdoor unit (no confirmation dialog)",
                IsChecked = settings.AutoCreateSystem,
                Margin = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(_autoCreateBox);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "Save", Width = 100, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            ok.Click += OnOk;
            var cancel = new Button { Content = "Cancel", Width = 90, Height = 28, IsCancel = true };
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);
            root.Children.Add(btnRow);

            Content = root;

            _grid.SelectionChanged += OnSelectionChanged;

            // Nothing to select yet on a first-time open (empty settings) would leave
            // the whole detail panel — including the "…" browse button — disabled with
            // no visible reason why. Start with one row ready to edit instead.
            if (_rows.Count == 0) OnAdd(null, null);
            else _grid.SelectedIndex = 0;
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows      = false,
                CanUserDeleteRows   = false,
                SelectionMode       = DataGridSelectionMode.Single,
                HeadersVisibility   = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                MaxHeight           = 200,
                Margin              = new Thickness(0, 4, 0, 0),
                Background          = Brushes.White,
                IsReadOnly          = true,
                ItemsSource         = _rows
            };

            grid.Columns.Add(Col("Label", nameof(MappingRow.Display), 220));
            grid.Columns.Add(Col("Role", nameof(MappingRow.RoleText), 90));
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Family", Binding = new Binding(nameof(MappingRow.FamilyName)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            return grid;
        }

        private static DataGridTextColumn Col(string header, string path, double w) => new DataGridTextColumn
        {
            Header = header, Binding = new Binding(path), Width = new DataGridLength(w)
        };

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _editing = _grid.SelectedItem as MappingRow;
            _detailPanel.IsEnabled = _editing != null;
            if (_editing == null)
            {
                _labelBox.Text = _catalogBox.Text = string.Empty;
                _familyBox.Text = string.Empty;
                _roleBox.SelectedItem = null;
                return;
            }

            _labelBox.Text    = _editing.Label ?? "";
            _roleBox.SelectedItem = _editing.Role;
            _familyBox.Text   = _editing.FamilyName ?? "";
            _catalogBox.Text  = _editing.CatalogPath ?? "";
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            var row = new MappingRow { Label = "New mapping", Role = AcCatalogRole.Indoor };
            _rows.Add(row);
            _grid.SelectedItem = row;
            _grid.ScrollIntoView(row);
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            if (_grid.SelectedItem is MappingRow row) _rows.Remove(row);
        }

        private void OnBrowseCatalog(object sender, RoutedEventArgs e)
        {
            if (_editing == null) { OnAdd(null, null); }

            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select a type-catalog file",
                    Filter = "Type catalog files (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*"
                };
                if (dlg.ShowDialog(this) == true && _editing != null)
                {
                    _editing.CatalogPath = dlg.FileName;
                    _catalogBox.Text = dlg.FileName;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The file picker could not be opened: " + ex.Message,
                    "AC Link", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            (Content as FrameworkElement)?.Focus();

            var incomplete = _rows.Where(r => string.IsNullOrWhiteSpace(r.FamilyName) || string.IsNullOrWhiteSpace(r.CatalogPath)).ToList();
            if (incomplete.Count > 0)
            {
                MessageBox.Show(this,
                    "Every mapping needs a family and a catalog file. Fix or remove the incomplete row(s).",
                    "AC Link", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(_toleranceBox.Text, out double tolerance) || tolerance <= 0)
            {
                MessageBox.Show(this, "Enter a tolerance percentage greater than 0.", "AC Link",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var warnings = _rows.SelectMany(ValidateRow).ToList();
            if (warnings.Count > 0)
            {
                var r = MessageBox.Show(this,
                    "Some mappings look incomplete:\n\n" + string.Join("\n", warnings) +
                    "\n\nSave anyway?",
                    "AC Link", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }

            Settings = new AcCatalogSettings
            {
                TolerancePercent = tolerance,
                AutoCreateSystem = _autoCreateBox.IsChecked == true,
                Mappings = _rows.Select(r => r.ToMapping()).ToList()
            };
            DialogResult = true;
        }

        private IEnumerable<string> ValidateRow(MappingRow row)
        {
            var file = CatalogFile.For(row.CatalogPath);
            if (!file.IsUsable)
            {
                yield return $"•  \"{row.Display}\": {file.Problem}";
                yield break;
            }
            if (AcCatalogSearch.FindCapacityColumn(file) == null)
            {
                yield return $"•  \"{row.Display}\": no column declares a Cooling Load value (header token HVAC_COOLING_LOAD).";
                yield break;
            }
            var symbol = FamilyScanner.SymbolsOf(_doc, row.FamilyName).FirstOrDefault();
            var capCol = AcCatalogSearch.FindCapacityColumn(file);
            if (symbol != null && symbol.LookupParameter(capCol.Name) == null)
                yield return $"•  \"{row.Display}\": family \"{row.FamilyName}\" has no type parameter named \"{capCol.Name}\" — capacity won't be written on new types.";
        }

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 6, 0, 4)
        };

        /// <summary>Opens the dialog and saves on confirm. Returns null on cancel.</summary>
        public static AcCatalogSettings ShowAndSave(Autodesk.Revit.UI.UIApplication uiApp, Document doc, string notice)
        {
            var dlg = new CatalogMappingDialog(doc, AcCatalogSettings.Load(), notice);
            new System.Windows.Interop.WindowInteropHelper(dlg).Owner = uiApp.MainWindowHandle;
            dlg.ShowDialog();
            if (dlg.DialogResult != true) return null;

            if (!dlg.Settings.Save())
                MessageBox.Show(dlg, "Settings could not be saved to disk; they apply to this session only.",
                    "AC Link", MessageBoxButton.OK, MessageBoxImage.Warning);
            return dlg.Settings;
        }
    }
}
