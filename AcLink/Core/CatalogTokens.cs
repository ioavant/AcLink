using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace AcLink
{
    /// <summary>
    /// Translates the tokens in a type-catalog header — "BRITISH_THERMAL_UNITS_PER_HOUR",
    /// "HVAC_COOLING_LOAD" — into the Revit units and specs they name.
    ///
    /// Deliberately a hand-built table rather than walking UnitTypeId/SpecTypeId via
    /// reflection: a bad ForgeTypeId passed to certain Revit unit APIs can raise an
    /// AccessViolationException, a corrupted-state exception `catch` cannot intercept.
    /// A token that is not listed here resolves to null and the column is then
    /// treated as a plain number (flagged via UnitUnderstood), so an unknown unit
    /// shows up as something to look at rather than a silently wrong value.
    /// </summary>
    internal static class CatalogTokens
    {
        private static readonly HashSet<string> Unitless =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GENERAL", "FIXED", "" };

        private static Dictionary<string, ForgeTypeId> _units;
        private static Dictionary<string, ForgeTypeId> _specs;

        /// <summary>The unit a token names; null when it needs no conversion or is unknown.</summary>
        public static ForgeTypeId Unit(string token)
        {
            if (token == null) token = string.Empty;
            if (Unitless.Contains(token.Trim())) return null;
            if (_units == null) _units = BuildUnits();
            return Lookup(_units, token);
        }

        /// <summary>The spec a token names, or null when it is unknown or means "text".</summary>
        public static ForgeTypeId Spec(string token)
        {
            if (_specs == null) _specs = BuildSpecs();
            return Lookup(_specs, token);
        }

        /// <summary>True when a token is understood, or needs no understanding.</summary>
        public static bool KnowsUnit(string token)
        {
            if (token == null) token = string.Empty;
            return Unitless.Contains(token.Trim()) || Unit(token) != null;
        }

        private static ForgeTypeId Lookup(Dictionary<string, ForgeTypeId> map, string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            return map.TryGetValue(token.Trim(), out ForgeTypeId found) ? found : null;
        }

        private static Dictionary<string, ForgeTypeId> BuildUnits() =>
            new Dictionary<string, ForgeTypeId>(StringComparer.OrdinalIgnoreCase)
            {
                // Length
                { "MILLIMETERS", UnitTypeId.Millimeters },
                { "CENTIMETERS", UnitTypeId.Centimeters },
                { "METERS", UnitTypeId.Meters },
                { "DECIMAL_FEET", UnitTypeId.Feet },
                { "DECIMAL_INCHES", UnitTypeId.Inches },

                // Air flow
                { "CUBIC_METERS_PER_HOUR", UnitTypeId.CubicMetersPerHour },
                { "CUBIC_METERS_PER_SECOND", UnitTypeId.CubicMetersPerSecond },
                { "LITERS_PER_SECOND", UnitTypeId.LitersPerSecond },
                { "CUBIC_FEET_PER_MINUTE", UnitTypeId.CubicFeetPerMinute },
                { "CUBIC_FEET_PER_HOUR", UnitTypeId.CubicFeetPerHour },

                // Pressure
                { "PASCALS", UnitTypeId.Pascals },
                { "KILOPASCALS", UnitTypeId.Kilopascals },
                { "BARS", UnitTypeId.Bars },
                { "POUNDS_FORCE_PER_SQUARE_INCH", UnitTypeId.PoundsForcePerSquareInch },

                // Power / capacity — the ones an AC catalog actually uses
                { "WATTS", UnitTypeId.Watts },
                { "KILOWATTS", UnitTypeId.Kilowatts },
                { "HORSEPOWER", UnitTypeId.Horsepower },
                { "BRITISH_THERMAL_UNITS_PER_SECOND", UnitTypeId.BritishThermalUnitsPerSecond },
                { "BRITISH_THERMAL_UNITS_PER_HOUR", UnitTypeId.BritishThermalUnitsPerHour },
                { "VOLT_AMPERES", UnitTypeId.VoltAmperes },

                // Rotation, angle, ratio, mass, money
                { "REVOLUTIONS_PER_MINUTE", UnitTypeId.RevolutionsPerMinute },
                { "DEGREES", UnitTypeId.Degrees },
                { "PERCENTAGE", UnitTypeId.Percentage },
                { "KILOGRAMS_MASS", UnitTypeId.Kilograms },
                { "POUNDS_MASS", UnitTypeId.PoundsMass },
                { "CURRENCY", UnitTypeId.Currency }
            };

        private static Dictionary<string, ForgeTypeId> BuildSpecs() =>
            new Dictionary<string, ForgeTypeId>(StringComparer.OrdinalIgnoreCase)
            {
                { "LENGTH", SpecTypeId.Length },
                { "AREA", SpecTypeId.Area },
                { "VOLUME", SpecTypeId.Volume },
                { "ANGLE", SpecTypeId.Angle },
                { "NUMBER", SpecTypeId.Number },
                { "MASS", SpecTypeId.Mass },
                { "FORCE", SpecTypeId.Force },
                { "CURRENCY", SpecTypeId.Currency },

                { "HVAC_AIR_FLOW", SpecTypeId.AirFlow },
                { "HVAC_AIRFLOW", SpecTypeId.AirFlow },
                { "HVAC_PRESSURE", SpecTypeId.HvacPressure },
                { "HVAC_POWER", SpecTypeId.HvacPower },
                { "HVAC_COOLING_LOAD", SpecTypeId.CoolingLoad },
                { "HVAC_VELOCITY", SpecTypeId.HvacVelocity },
                { "HVAC_TEMPERATURE", SpecTypeId.HvacTemperature },

                { "PIPING_PRESSURE", SpecTypeId.PipingPressure },

                { "ELECTRICAL_POWER", SpecTypeId.ElectricalPower },
                { "ELECTRICAL_CURRENT", SpecTypeId.Current },
                { "ELECTRICAL_POTENTIAL", SpecTypeId.ElectricalPotential },
                { "ELECTRICAL_FREQUENCY", SpecTypeId.ElectricalFrequency },
                { "ELECTRICAL_APPARENT_POWER", SpecTypeId.ApparentPower }

                // OTHER and TEXT are deliberately absent — a null spec already
                // means "not a measured quantity".
            };
    }
}
