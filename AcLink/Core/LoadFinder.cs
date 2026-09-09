using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AcLink
{
    /// <summary>
    /// Finds the cooling load of a unit and returns it in Btu/hr.
    ///
    /// Strategy:
    ///   1. Look for a parameter (instance, then type) whose DATA TYPE is
    ///      SpecTypeId.CoolingLoad — robust against arbitrary parameter names and
    ///      never confuses it with Heating Load.  Prefer one whose name contains
    ///      "cool"; otherwise take the largest cooling-load value found.
    ///   2. Fallback: parse a number + unit token embedded in the type name
    ///      (e.g. "Cassette 36000 Btu", "VRF 14kW", "5 TR").
    /// </summary>
    public static class LoadFinder
    {
        public static double CoolingLoadBtuH(Element e)
        {
            double internalVal = FindCoolingLoadInternal(e);
            if (internalVal > 0)
            {
                try
                {
                    return UnitUtils.ConvertFromInternalUnits(
                        internalVal, UnitTypeId.BritishThermalUnitsPerHour);
                }
                catch { }
            }

            string typeName = (e as FamilyInstance)?.Symbol?.Name ?? e?.Name ?? "";
            return ParseLoadBtuH(typeName) ?? 0;
        }

        // ── Parameter search (by data type) ──────────────────────────────────

        private static double FindCoolingLoadInternal(Element e)
        {
            var named = new List<double>();
            var any   = new List<double>();

            CollectFrom(e?.Parameters, named, any);
            if (e is FamilyInstance fi)
                CollectFrom(fi.Symbol?.Parameters, named, any);

            if (named.Count > 0) return named.Max();
            if (any.Count   > 0) return any.Max();
            return 0;
        }

        private static void CollectFrom(ParameterSet pset, List<double> named, List<double> any)
        {
            if (pset == null) return;
            foreach (Parameter p in pset)
            {
                if (p == null || p.StorageType != StorageType.Double || !p.HasValue) continue;

                ForgeTypeId dt;
                try { dt = p.Definition?.GetDataType(); } catch { continue; }
                if (dt == null || !dt.Equals(SpecTypeId.CoolingLoad)) continue;

                double v = p.AsDouble();
                if (v <= 0) continue;

                any.Add(v);
                if ((p.Definition.Name ?? "").ToLowerInvariant().Contains("cool"))
                    named.Add(v);
            }
        }

        // ── Type-name fallback ───────────────────────────────────────────────

        // Order matters: multi-letter tokens (kw) are tried before "w".
        private static readonly (string token, double toBtuH)[] UnitMap =
        {
            ("btu",  1.0),
            ("kcal", 3.96567),
            ("ton",  12000.0),
            ("tr",   12000.0),
            ("kw",   3412.142),
            ("hp",   2544.43),
            ("w",    3.412142),
        };

        public static double? ParseLoadBtuH(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string s = name.ToLowerInvariant();

            foreach (var (token, factor) in UnitMap)
            {
                var m = Regex.Match(s, @"(\d+(?:[.,]\d+)?)\s*" + token + @"\b");
                if (m.Success &&
                    double.TryParse(m.Groups[1].Value.Replace(',', '.'),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out double num))
                {
                    return num * factor;
                }
            }
            return null;
        }
    }
}
