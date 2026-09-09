using System;
using System.Globalization;
using Autodesk.Revit.DB;

namespace AcLink
{
    /// <summary>
    /// Formatting/parsing helpers so catalog values are always shown and typed in
    /// the project's own units, with no conversion happening anywhere else.
    /// </summary>
    internal static class RevitUnits
    {
        /// <summary>Format an internal-unit value the way this project would display it.</summary>
        public static string Format(Units units, ForgeTypeId spec, double internalValue)
        {
            if (units != null && spec != null && !spec.Empty())
            {
                try { return UnitFormatUtils.Format(units, spec, internalValue, false); }
                catch { /* spec has no format options - fall through */ }
            }
            return internalValue.ToString("0.###", CultureInfo.CurrentCulture);
        }

        /// <summary>
        /// Parse what the user typed into an internal-unit value, accepting both a
        /// bare number in project units and a qualified one (e.g. "12000 Btu/h").
        /// </summary>
        public static bool TryParse(Units units, ForgeTypeId spec, string text, out double internalValue)
        {
            internalValue = 0.0;
            if (string.IsNullOrEmpty(text)) return false;

            if (units != null && spec != null && !spec.Empty())
            {
                try { return UnitFormatUtils.TryParse(units, spec, text, out internalValue); }
                catch { /* spec cannot be parsed - fall through */ }
            }

            return double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out internalValue)
                || double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out internalValue);
        }

        /// <summary>The project's unit symbol for a spec ("Btu/h", "kW"), or "".</summary>
        public static string Symbol(Units units, ForgeTypeId spec)
        {
            if (units == null || spec == null || spec.Empty()) return string.Empty;
            try { return LabelUtils.GetLabelForUnit(units.GetFormatOptions(spec).GetUnitTypeId()); }
            catch { return string.Empty; }
        }
    }
}
