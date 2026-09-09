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
    /// Create / assign an AC system from the selected Mechanical Equipment.
    /// Shows a table with an editable "Outdoor" checkbox per unit (pre-filled by
    /// the classifier), a system-name box and a shared Comments box.
    /// </summary>
    public class CreateSystemDialog : Window
    {
        public class UnitRow : INotifyPropertyChanged
        {
            public ElementId Id;
            public string Family { get; set; }
            public string Type { get; set; }
            public string Description { get; set; }
            public string HeightFmt { get; set; }

            private bool _isOutdoor;
            public bool IsOutdoor
            {
                get => _isOutdoor;
                set { _isOutdoor = value; OnChanged(nameof(IsOutdoor)); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnChanged(string n) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private readonly ObservableCollection<UnitRow> _rows;
        private readonly TextBox _nameBox;
        private readonly TextBox _keyBox;
        private readonly TextBox _commentsBox;
        private readonly CheckBox _crossBox;

        public string SystemBaseName { get; private set; }
        public string Comments { get; private set; }

        /// <summary>
        /// Optional pasted system key — the explicit way to join a system that lives
        /// in a model not linked here.  Empty = auto-generate / resolve by name.
        /// </summary>
        public string SystemKeyInput => _keyBox?.Text?.Trim() ?? "";

        /// <summary>
        /// Cross-model system: its units may live in several models, so link scanning
        /// is enabled for it.  Most systems are in-model (5–10 % are cross), hence
        /// the opt-in checkbox.
        /// </summary>
        public bool IsCrossModel => _crossBox?.IsChecked == true;

        public IReadOnlyList<UnitRow> Rows => _rows;

        public CreateSystemDialog(IList<AcUnit> units, string suggestedName, string comments)
        {
            Title                 = "AC Link — Create / Assign System";
            Width                 = 720;
            SizeToContent         = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background            = new SolidColorBrush(Color.FromRgb(245, 245, 245));
            ResizeMode            = ResizeMode.CanResize;

            _rows = new ObservableCollection<UnitRow>(units.Select(u => new UnitRow
            {
                Id          = u.Id,
                Family      = u.FamilyName,
                Type        = u.TypeName,
                Description = u.Description,
                HeightFmt   = $"{u.HeightMm:F0}",
                IsOutdoor   = u.IsOutdoor
            }));

            var root = new StackPanel { Margin = new Thickness(16) };

            // ── System name ───────────────────────────────────────────────────
            root.Children.Add(Label("System name (the outdoor unit gets the “-C” suffix)"));
            _nameBox = new TextBox { Text = suggestedName ?? "", Margin = new Thickness(0, 0, 0, 10) };
            root.Children.Add(_nameBox);

            // ── System key (optional) ─────────────────────────────────────────
            root.Children.Add(Label("System key — leave empty to auto-generate; paste a key to join a system from another model"));
            _keyBox = new TextBox { Text = "", Margin = new Thickness(0, 0, 0, 10) };
            root.Children.Add(_keyBox);

            // ── Cross-model flag ──────────────────────────────────────────────
            _crossBox = new CheckBox
            {
                Content = "Cross-model system (units in linked models — enables link scanning for this system)",
                Margin  = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(_crossBox);

            // ── Table ─────────────────────────────────────────────────────────
            root.Children.Add(Label("Units in the system — check the outdoor unit (condenser):"));
            root.Children.Add(BuildGrid());

            // ── Comments ──────────────────────────────────────────────────────
            root.Children.Add(Label("Comments (written to all units in the system)"));
            _commentsBox = new TextBox
            {
                Text = comments ?? "",
                Margin = new Thickness(0, 0, 0, 12),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 40
            };
            root.Children.Add(_commentsBox);

            // ── Buttons ───────────────────────────────────────────────────────
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button { Content = "Create Link", Width = 120, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            ok.Click += OnOk;
            var cancel = new Button { Content = "Cancel", Width = 90, Height = 28, IsCancel = true };
            cancel.Click += (s, e) => DialogResult = false;
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);
            root.Children.Add(btnRow);

            Content = root;
        }

        private UIElement BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows      = false,
                CanUserDeleteRows   = false,
                HeadersVisibility   = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                MaxHeight           = 260,
                Margin              = new Thickness(0, 4, 0, 12),
                Background          = Brushes.White,
                ItemsSource         = _rows
            };

            grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header  = "Outdoor",
                Binding = new Binding(nameof(UnitRow.IsOutdoor)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width   = 70
            });
            grid.Columns.Add(ReadOnlyCol("Family", nameof(UnitRow.Family), 180));
            grid.Columns.Add(ReadOnlyCol("Type", nameof(UnitRow.Type), 160));
            grid.Columns.Add(ReadOnlyCol("Height, mm", nameof(UnitRow.HeightFmt), 80));
            grid.Columns.Add(new DataGridTextColumn
            {
                Header   = "Description",
                Binding  = new Binding(nameof(UnitRow.Description)),
                IsReadOnly = true,
                Width    = new DataGridLength(1, DataGridLengthUnitType.Star)
            });

            return grid;
        }

        private static DataGridTextColumn ReadOnlyCol(string header, string path, double w) =>
            new DataGridTextColumn
            {
                Header     = header,
                Binding    = new Binding(path),
                IsReadOnly = true,
                Width      = new DataGridLength(w)
            };

        private void OnOk(object sender, RoutedEventArgs e)
        {
            // Commit any pending checkbox edit.
            (Content as FrameworkElement)?.Focus();

            string name = (_nameBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "Please enter a system name.", "AC Link",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int outdoorCount = _rows.Count(r => r.IsOutdoor);
            if (outdoorCount > 1)
            {
                MessageBox.Show(this,
                    "A system can have only one outdoor unit (condenser).\n" +
                    "Please uncheck the extra \"Outdoor\" boxes.",
                    "AC Link", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (outdoorCount == 0)
            {
                var r = MessageBox.Show(this,
                    "No outdoor unit (condenser) is checked. Create a system from indoor units only?",
                    "AC Link", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }

            SystemBaseName = name;
            Comments       = _commentsBox.Text ?? "";
            DialogResult   = true;
        }

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4)
        };
    }
}
