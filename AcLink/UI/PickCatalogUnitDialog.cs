using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Binding = System.Windows.Data.Binding;

namespace AcLink
{
    /// <summary>
    /// Search/pick dialog shared by both catalog-driven commands: choose a mapping
    /// (family + CSV), see the target capacity, search, pick a ranked candidate row.
    /// </summary>
    internal class PickCatalogUnitDialog : Window
    {
        private readonly Document _doc;
        private readonly List<AcCatalogMapping> _mappings;
        private readonly Units _units;

        private readonly ComboBox _mappingBox;
        private readonly TextBox _targetBox;
        private readonly TextBox _toleranceBox;
        private readonly TextBlock _statusText;
        private readonly DataGrid _grid;
        private readonly Button _insertBtn;

        private List<AcCandidate> _results = new List<AcCandidate>();

        public AcCandidate SelectedCandidate { get; private set; }
        public AcCatalogMapping SelectedMapping { get; private set; }

        public PickCatalogUnitDialog(Document doc, IList<AcCatalogMapping> mappings,
            double initialTargetBtuH, double tolerancePercent)
        {
            _doc = doc;
            _mappings = mappings.ToList();
            _units = doc.GetUnits();

            Title                 = "AC Link — Pick Unit from Catalog";
            Width                 = 720;
            SizeToContent         = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background            = new SolidColorBrush(Color.FromRgb(245, 245, 245));
            ResizeMode            = ResizeMode.CanResize;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(Label("Catalog"));
            _mappingBox = new ComboBox { ItemsSource = _mappings, Margin = new Thickness(0, 0, 0, 10) };
            _mappingBox.SelectionChanged += (s, e) => RunSearch();
            root.Children.Add(_mappingBox);

            var targetRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

            var targetPanel = new StackPanel();
            targetPanel.Children.Add(Label($"Target cooling capacity ({RevitUnits.Symbol(_units, SpecTypeId.CoolingLoad)})"));
            double targetInternal = 0;
            try { targetInternal = UnitUtils.ConvertToInternalUnits(initialTargetBtuH, UnitTypeId.BritishThermalUnitsPerHour); } catch { }
            _targetBox = new TextBox { Text = RevitUnits.Format(_units, SpecTypeId.CoolingLoad, targetInternal) };
            targetPanel.Children.Add(_targetBox);
            Grid.SetColumn(targetPanel, 0);

            var tolerancePanel = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            tolerancePanel.Children.Add(Label("Tolerance %"));
            _toleranceBox = new TextBox { Text = tolerancePercent.ToString("0.#") };
            tolerancePanel.Children.Add(_toleranceBox);
            Grid.SetColumn(tolerancePanel, 1);

            targetRow.Children.Add(targetPanel);
            targetRow.Children.Add(tolerancePanel);
            root.Children.Add(targetRow);

            var searchBtn = new Button { Content = "Search", Width = 100, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
            searchBtn.Click += (s, e) => RunSearch();
            root.Children.Add(searchBtn);

            _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Foreground = Brushes.DimGray };
            root.Children.Add(_statusText);

            _grid = BuildGrid();
            _grid.SelectionChanged += (s, e) => _insertBtn.IsEnabled = _grid.SelectedItem != null;
            _grid.MouseDoubleClick += (s, e) => { if (_grid.SelectedItem != null) Accept(); };
            root.Children.Add(_grid);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _insertBtn = new Button { Content = "Insert", Width = 100, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, IsDefault = true };
            _insertBtn.Click += (s, e) => Accept();
            var cancel = new Button { Content = "Cancel", Width = 90, Height = 28, IsCancel = true };
            btnRow.Children.Add(_insertBtn);
            btnRow.Children.Add(cancel);
            root.Children.Add(btnRow);

            Content = root;

            if (_mappings.Count > 0) _mappingBox.SelectedIndex = 0;
            else RunSearch();
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows      = false,
                CanUserDeleteRows   = false,
                IsReadOnly          = true,
                SelectionMode       = DataGridSelectionMode.Single,
                HeadersVisibility   = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                MaxHeight           = 280,
                Background          = Brushes.White
            };
            grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(AcCandidate.TypeName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Capacity", Binding = new Binding(nameof(AcCandidate.CapacityText)), Width = new DataGridLength(140) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Off target", Binding = new Binding(nameof(AcCandidate.DeviationText)), Width = new DataGridLength(90) });
            grid.Columns.Add(new DataGridTextColumn { Header = "In model", Binding = new Binding(nameof(AcCandidate.LoadedText)), Width = new DataGridLength(90) });
            return grid;
        }

        private void RunSearch()
        {
            var mapping = _mappingBox.SelectedItem as AcCatalogMapping;
            SelectedMapping = mapping;

            if (mapping == null)
            {
                _statusText.Text = "No catalog mapping available.";
                _results = new List<AcCandidate>();
                _grid.ItemsSource = _results;
                return;
            }

            if (!RevitUnits.TryParse(_units, SpecTypeId.CoolingLoad, _targetBox.Text, out double targetInternal))
                targetInternal = 0;
            double targetBtuH = 0;
            try { targetBtuH = UnitUtils.ConvertFromInternalUnits(targetInternal, UnitTypeId.BritishThermalUnitsPerHour); } catch { }

            double.TryParse(_toleranceBox.Text, out double tolerance);
            if (tolerance <= 0) tolerance = AcCatalogSettings.DefaultTolerancePercent;

            _results = AcCatalogSearch.Run(_doc, mapping, targetBtuH, tolerance, out string note);
            _grid.ItemsSource = _results;
            _statusText.Text = note ?? $"{_results.Count} candidate(s) within {tolerance:0.#}%.";
            _insertBtn.IsEnabled = false;
        }

        private void Accept()
        {
            SelectedCandidate = _grid.SelectedItem as AcCandidate;
            if (SelectedCandidate == null) return;
            DialogResult = true;
        }

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 4)
        };
    }
}
