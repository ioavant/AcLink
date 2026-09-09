using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using Binding = System.Windows.Data.Binding;
using TextBox = System.Windows.Controls.TextBox;
using Transform = Autodesk.Revit.DB.Transform;

namespace AcLink
{
    /// <summary>Action the user requested when the dialog closed.</summary>
    public enum SystemDialogAction { Close, AddUnits, Remove, SetOutdoor, ToggleCross, SetKey }

    /// <summary>
    /// Management view for one AC system.
    ///   • Show         — zoom the active view to the selected unit (stays open).
    ///   • Add Units…   — closes so the command can pick units to add as indoor.
    ///   • Set Outdoor  — make the selected unit the condenser.
    ///   • Remove       — drop the selected unit(s) from the system.
    /// Mutations are performed by the command (close → act → reopen) so there is
    /// never any unsaved state in the grid.
    /// </summary>
    public class SystemElementsDialog : Window
    {
        private class ElemRow
        {
            public ElementId Id { get; set; }
            public string Role { get; set; }
            public string Family { get; set; }
            public string Type { get; set; }
            public string CoolingFmt { get; set; }
            public string FactorFmt { get; set; }
            public string Level { get; set; }
            public string Model { get; set; }
            public bool IsRemote { get; set; }
            public string IdText { get; set; }
            public ElementId LinkInstanceId { get; set; }
        }

        private readonly UIDocument _uiDoc;
        private readonly DataGrid _grid;
        private readonly TextBox _commentsBox;

        public SystemDialogAction Action { get; private set; } = SystemDialogAction.Close;
        public List<ElementId> SelectedIds { get; private set; } = new List<ElementId>();

        /// <summary>Key entered via "Set key…" (SystemDialogAction.SetKey).</summary>
        public string KeyInput { get; private set; }

        /// <summary>System comment edited by the user (applied to all units by the command).</summary>
        public string Comments => _commentsBox?.Text ?? "";

        public SystemElementsDialog(AcSystem sys, UIDocument uiDoc)
        {
            _uiDoc = uiDoc;

            Title                 = $"AC Link — System \"{sys.BaseName}\"";
            Width                 = 920;
            Height                = 440;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background            = new SolidColorBrush(Color.FromRgb(245, 245, 245));

            var root = new DockPanel { Margin = new Thickness(14) };

            var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var header = new TextBlock
            {
                Text = $"Outdoor: {(sys.Outdoor != null ? 1 : 0)}   ·   Indoor: {sys.IndoorCount}" +
                       "      (Ctrl/Shift or Select All to act on several units at once)",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerRow.Children.Add(header);

            // System key + copy button (the key is what another model pastes to join).
            var keyPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var crossBox = new CheckBox
            {
                Content = "Cross-model",
                IsChecked = sys.IsCrossModel,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "Cross-model systems may have units in linked models (link scanning is enabled for them)"
            };
            // Attach handlers AFTER IsChecked is set so construction never fires them.
            crossBox.Checked   += (s, e) => { Action = SystemDialogAction.ToggleCross; Close(); };
            crossBox.Unchecked += (s, e) => { Action = SystemDialogAction.ToggleCross; Close(); };
            keyPanel.Children.Add(crossBox);
            keyPanel.Children.Add(new TextBlock
            {
                Text = $"Key: {sys.KeyLabel}",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 110, 160))
            });
            if (!string.IsNullOrEmpty(sys.SysKey))
            {
                var copyBtn = new Button { Content = "Copy key", Width = 75, Height = 22 };
                copyBtn.ToolTip = "Copy the system key — paste it in another model's Create dialog to join this system";
                copyBtn.Click += (s, e) =>
                {
                    try { Clipboard.SetText(sys.SysKey); } catch { }
                };
                keyPanel.Children.Add(copyBtn);
            }

            var setKeyBtn = new Button
            {
                Content = "Set key…",
                Width = 70,
                Height = 22,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Assign a key copied from another model — joins this system to that one"
            };
            setKeyBtn.Click += (s, e) =>
            {
                // Pre-fill from the clipboard when it already holds a key.
                string init = "";
                try
                {
                    var t = (Clipboard.GetText() ?? "").Trim();
                    if (LooksLikeKey(t)) init = t.ToUpperInvariant();
                }
                catch { }

                var d = new KeyInputDialog(init) { Owner = this };
                if (d.ShowDialog() == true && !string.IsNullOrWhiteSpace(d.KeyText))
                {
                    KeyInput = d.KeyText.Trim().ToUpperInvariant();
                    Action = SystemDialogAction.SetKey;
                    Close();
                }
            };
            keyPanel.Children.Add(setKeyBtn);

            headerRow.Children.Add(keyPanel);

            DockPanel.SetDock(headerRow, Dock.Top);
            root.Children.Add(headerRow);

            double indoorTotal = sys.IndoorCoolingTotalBtuH;
            var totals = new TextBlock
            {
                Text = indoorTotal > 0
                    ? $"Indoor cooling total — 100%: {indoorTotal:N0}   ·   " +
                      $"80%: {indoorTotal * 0.8:N0}   ·   70%: {indoorTotal * 0.7:N0}   Btu/h"
                    : "Indoor cooling total — n/a (no cooling load on indoor units)",
                Foreground = new SolidColorBrush(Color.FromRgb(0, 110, 160)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(totals, Dock.Top);
            root.Children.Add(totals);

            // ── Buttons (bottom) ───────────────────────────────────────────────
            var btnRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };

            var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            var selectAllBtn = MakeButton("Select All", 80);
            selectAllBtn.ToolTip = "Select every unit in the table";
            selectAllBtn.Click += (s, e) => { _grid.SelectAll(); _grid.Focus(); };
            var showBtn = MakeButton("Show", 90);
            showBtn.ToolTip = "Highlight the selected unit(s); reports any unit not on the current view";
            showBtn.Click += OnShow;
            leftButtons.Children.Add(selectAllBtn);
            var addBtn = MakeButton("Add Units…", 110);
            addBtn.ToolTip = "Pick more Mechanical Equipment units to add to this system";
            addBtn.Click += (s, e) => { Action = SystemDialogAction.AddUnits; Close(); };
            var outdoorBtn = MakeButton("Set as Outdoor", 120);
            outdoorBtn.ToolTip = "Make the selected unit the outdoor (condenser) of this system";
            outdoorBtn.Click += OnSetOutdoor;
            var removeBtn = MakeButton("Remove", 90);
            removeBtn.ToolTip = "Remove the selected unit(s) from this system";
            removeBtn.Click += OnRemove;
            leftButtons.Children.Add(showBtn);
            leftButtons.Children.Add(addBtn);
            leftButtons.Children.Add(outdoorBtn);
            leftButtons.Children.Add(removeBtn);
            DockPanel.SetDock(leftButtons, Dock.Left);
            btnRow.Children.Add(leftButtons);

            var closeBtn = new Button { Content = "Close", Width = 90, Height = 28, HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
            closeBtn.Click += (s, e) => { Action = SystemDialogAction.Close; Close(); };
            btnRow.Children.Add(closeBtn);

            DockPanel.SetDock(btnRow, Dock.Bottom);
            root.Children.Add(btnRow);

            // ── System comment (one field for the whole system) ────────────────
            var commentsPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            commentsPanel.Children.Add(new TextBlock
            {
                Text = "Comments (applied to all units of this system):",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            _commentsBox = new TextBox
            {
                Text = sys.Comments ?? "",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 44
            };
            commentsPanel.Children.Add(_commentsBox);
            DockPanel.SetDock(commentsPanel, Dock.Bottom);
            root.Children.Add(commentsPanel);

            // ── Grid (fills) ───────────────────────────────────────────────────
            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly          = true,
                CanUserAddRows      = false,
                SelectionMode       = DataGridSelectionMode.Extended,
                SelectionUnit       = DataGridSelectionUnit.FullRow,
                HeadersVisibility   = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                Background          = Brushes.White
            };
            _grid.Columns.Add(Col("Role", nameof(ElemRow.Role), 170));
            _grid.Columns.Add(Col("Family", nameof(ElemRow.Family), 150));
            _grid.Columns.Add(Col("Type", nameof(ElemRow.Type), 130));
            _grid.Columns.Add(Col("Cooling, Btu/h", nameof(ElemRow.CoolingFmt), 100));
            _grid.Columns.Add(Col("Load factor", nameof(ElemRow.FactorFmt), 80));
            _grid.Columns.Add(Col("Level", nameof(ElemRow.Level), 110));
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header  = "Model",
                Binding = new Binding(nameof(ElemRow.Model)),
                Width   = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _grid.MouseDoubleClick += OnShow;

            double indoorSum = sys.IndoorCoolingTotalBtuH;
            var rows = new List<ElemRow>();
            if (sys.Outdoor != null)
            {
                var r = ToRow(sys.Outdoor);
                var f = sys.OutdoorLoadFactor;
                r.FactorFmt = f.HasValue ? $"{f.Value * 100:F0}%" : "—";
                rows.Add(r);
            }
            rows.AddRange(sys.Indoor.Select(ToRow));
            _grid.ItemsSource = rows;
            if (rows.Count > 0) _grid.SelectedIndex = 0;

            root.Children.Add(_grid);
            Content = root;
        }

        private static ElemRow ToRow(AcUnit u) => new ElemRow
        {
            Id         = u.Id,
            Role       = u.RoleLabel,
            Family     = u.FamilyName,
            Type       = u.TypeName,
            CoolingFmt = u.CoolingLoadBtuH > 0 ? $"{u.CoolingLoadBtuH:N0}" : "—",
            FactorFmt  = "",
            Level      = u.LevelName,
            Model      = u.ModelLabel,
            IsRemote   = u.IsRemote,
            IdText     = u.IdText,
            LinkInstanceId = u.LinkInstanceId
        };

        private List<ElemRow> SelectedRows() =>
            _grid.SelectedItems.OfType<ElemRow>().ToList();

        private List<ElementId> CurrentSelection() =>
            SelectedRows().Where(r => !r.IsRemote).Select(r => r.Id).ToList();

        private void OnShow(object sender, RoutedEventArgs e)
        {
            var rows = SelectedRows();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "Select one or more units (or click Select All).",
                    "AC Link", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Any linked units in the selection → Link Section Box mechanics:
            // one reference-based selection (host + linked together) and a zoom of
            // the active view over the combined transformed bounds.
            if (rows.Any(r => r.IsRemote))
            {
                ShowViaReferences(rows);
                return;
            }

            var ids = rows.Select(r => r.Id).ToList();

            var doc  = _uiDoc.Document;
            var view = _uiDoc.ActiveView;   // the view the user is looking at right now

            // Which of the selected units are actually shown in the current view?
            var visibleSet = new HashSet<ElementId>(
                new FilteredElementCollector(doc, view.Id).ToElementIds());
            var notVisible = ids.Where(id => !visibleSet.Contains(id)).ToList();

            try
            {
                _uiDoc.Selection.SetElementIds(ids);   // highlight all selected units
                _uiDoc.ShowElements(ids);              // zoom / navigate to show them
            }
            catch { /* Revit shows its own notice if it cannot display them */ }

            if (notVisible.Count > 0)
            {
                var lines = notVisible.Select(id =>
                {
                    var el = doc.GetElement(id);
                    return $"•  {Describe(el)}  —  level: {LevelName(el, doc)}";
                });
                MessageBox.Show(this,
                    "These units are not shown in the view you were on. Their levels:\n\n" +
                    string.Join("\n", lines),
                    "AC Link — units on other levels",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ── Linked-element show (Link Section Box mechanics) ──────────────────

        /// <summary>
        /// Selects host + linked units through References and zooms the active view
        /// to their combined bounds.  Linked refs are built with CreateLinkReference;
        /// selection uses Selection.SetReferences via reflection (Revit 2023+ — on
        /// 2022 only the zoom happens).  Units whose link is unloaded are reported
        /// with their model name and element ID for Select by ID.
        /// </summary>
        private void ShowViaReferences(List<ElemRow> rows)
        {
            var doc  = _uiDoc.Document;
            var refs = new List<Reference>();
            XYZ min = null, max = null;
            var unresolved = new List<ElemRow>();

            foreach (var row in rows)
            {
                if (!row.IsRemote)
                {
                    var el = doc.GetElement(row.Id);
                    if (el == null) continue;
                    try { refs.Add(new Reference(el)); } catch { }
                    AccumulateBounds(el.get_BoundingBox(null), null, ref min, ref max);
                }
                else
                {
                    var inst = row.LinkInstanceId != null
                        ? doc.GetElement(row.LinkInstanceId) as RevitLinkInstance : null;
                    var el = inst?.GetLinkDocument()?.GetElement(row.Id);
                    if (el == null) { unresolved.Add(row); continue; }

                    try { refs.Add(new Reference(el).CreateLinkReference(inst)); }
                    catch { unresolved.Add(row); continue; }

                    Transform tf = null;
                    try { tf = inst.GetTotalTransform(); } catch { }
                    AccumulateBounds(el.get_BoundingBox(null), tf, ref min, ref max);
                }
            }

            if (min != null) ZoomActiveView(min, max);
            if (refs.Count > 0) TrySelectReferences(refs);

            if (unresolved.Count > 0)
            {
                var lines = unresolved.Select(r =>
                    $"•  {r.Family} : {r.Type}  —  {r.Model},  ID {r.IdText}");
                MessageBox.Show(this,
                    "Could not locate these linked units (link unloaded?).\n" +
                    "Open their model and use Manage → Select by ID:\n\n" +
                    string.Join("\n", lines),
                    "AC Link", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>Grows min/max over the 8 (optionally link-transformed) box corners.</summary>
        private static void AccumulateBounds(
            BoundingBoxXYZ bb, Transform tf, ref XYZ min, ref XYZ max)
        {
            if (bb == null) return;
            for (int i = 0; i < 8; i++)
            {
                var p = new XYZ(
                    (i & 1) == 0 ? bb.Min.X : bb.Max.X,
                    (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                    (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                if (tf != null) p = tf.OfPoint(p);
                min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
        }

        private void ZoomActiveView(XYZ min, XYZ max)
        {
            try
            {
                var doc = _uiDoc.Document;
                foreach (var uv in _uiDoc.GetOpenUIViews())
                    if (uv.ViewId == doc.ActiveView.Id)
                    { uv.ZoomAndCenterRectangle(min, max); break; }
            }
            catch { }
        }

        /// <summary>
        /// Selection.SetReferences exists from Revit 2023 — we compile against the
        /// 2022 API, so it is invoked via reflection; on 2022 this is a no-op and
        /// the user still gets the zoom.
        /// </summary>
        private void TrySelectReferences(List<Reference> refs)
        {
            try
            {
                var sel = _uiDoc.Selection;
                var mi  = sel.GetType().GetMethod("SetReferences");
                mi?.Invoke(sel, new object[] { refs });
            }
            catch { }
        }

        private static string Describe(Element e)
        {
            if (e is FamilyInstance fi)
                return $"{fi.Symbol?.FamilyName} : {fi.Symbol?.Name}";
            return e?.Name ?? "?";
        }

        private static string LevelName(Element e, Document doc)
        {
            try
            {
                var lvlId = e.LevelId;
                if (lvlId != null && lvlId != ElementId.InvalidElementId &&
                    doc.GetElement(lvlId) is Level lv)
                    return lv.Name;
            }
            catch { }

            // Fallback: nearest level by elevation to the unit's bounding box.
            var bb = e?.get_BoundingBox(null);
            if (bb != null)
            {
                double z = bb.Min.Z;
                Level nearest = null; double best = double.MaxValue;
                foreach (var l in new FilteredElementCollector(doc)
                                      .OfClass(typeof(Level)).Cast<Level>())
                {
                    double d = Math.Abs(l.ProjectElevation - z);
                    if (d < best) { best = d; nearest = l; }
                }
                if (nearest != null) return nearest.Name;
            }
            return "(unknown)";
        }

        private void OnSetOutdoor(object sender, RoutedEventArgs e)
        {
            if (WarnIfRemoteSelected()) return;
            var sel = CurrentSelection();
            if (sel.Count != 1)
            {
                MessageBox.Show(this, "Select exactly one unit to set as the outdoor (condenser).",
                    "AC Link", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SelectedIds = sel;
            Action = SystemDialogAction.SetOutdoor;
            Close();
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            if (WarnIfRemoteSelected()) return;
            var sel = CurrentSelection();
            if (sel.Count == 0)
            {
                MessageBox.Show(this, "Select one or more units to remove from the system.",
                    "AC Link", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SelectedIds = sel;
            Action = SystemDialogAction.Remove;
            Close();
        }

        /// <summary>
        /// True (and warns) when the selection includes units from linked models —
        /// those are read-only here and must be edited in their own model.
        /// </summary>
        private bool WarnIfRemoteSelected()
        {
            var remote = SelectedRows().Where(r => r.IsRemote).ToList();
            if (remote.Count == 0) return false;

            string models = string.Join(", ", remote.Select(r => r.Model).Distinct());
            MessageBox.Show(this,
                $"Selected unit(s) belong to linked model(s): {models}.\n" +
                "Linked elements are read-only here — open that model to edit them.",
                "AC Link", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        private static Button MakeButton(string text, double w) =>
            new Button { Content = text, Width = w, Height = 28, Margin = new Thickness(0, 0, 8, 0) };

        private static DataGridTextColumn Col(string header, string path, double w) =>
            new DataGridTextColumn
            {
                Header  = header,
                Binding = new Binding(path),
                Width   = new DataGridLength(w)
            };

        /// <summary>Loose key-shape check: "K7F3-Q9W2" or "X-K7F3-Q9W2".</summary>
        internal static bool LooksLikeKey(string s) =>
            !string.IsNullOrWhiteSpace(s) &&
            System.Text.RegularExpressions.Regex.IsMatch(
                s.Trim(), @"^(X-)?[A-Z2-9]{4}-[A-Z2-9]{4}$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Small prompt for pasting a system key copied from another model.</summary>
    internal class KeyInputDialog : Window
    {
        private readonly TextBox _box;

        public string KeyText => _box.Text?.Trim() ?? "";

        public KeyInputDialog(string initial)
        {
            Title                 = "AC Link — Set System Key";
            Width                 = 400;
            SizeToContent         = SizeToContent.Height;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background            = new SolidColorBrush(Color.FromRgb(245, 245, 245));

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock
            {
                Text = "Paste the system key copied from the other model\n" +
                       "(e.g. X-K7F3-Q9W2). All units of this system will get it:",
                Margin = new Thickness(0, 0, 0, 6)
            });
            _box = new TextBox { Text = initial ?? "" };
            root.Children.Add(_box);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button { Content = "OK", Width = 80, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(KeyText))
                {
                    MessageBox.Show(this, "Enter a system key.", "AC Link",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!SystemElementsDialog.LooksLikeKey(KeyText))
                {
                    var r = MessageBox.Show(this,
                        $"\"{KeyText}\" does not look like an AC Link key (X-XXXX-XXXX).\nUse it anyway?",
                        "AC Link", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                }
                DialogResult = true;
            };
            var cancel = new Button { Content = "Cancel", Width = 80, Height = 26, IsCancel = true };
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);
            root.Children.Add(btnRow);

            Content = root;
            _box.Focus();
            _box.SelectAll();
        }
    }
}
