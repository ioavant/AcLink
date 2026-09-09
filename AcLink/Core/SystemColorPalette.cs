namespace AcLink
{
    /// <summary>
    /// Deterministic colour palette for systems. Colour is assigned by the
    /// system's position in the (alphabetically sorted) systems list, so the
    /// swatch in the table and the view override always match.
    /// </summary>
    public static class SystemColorPalette
    {
        private static readonly (byte r, byte g, byte b)[] Palette =
        {
            (228,  26,  28), ( 55, 126, 184), ( 77, 175,  74), (152,  78, 163),
            (255, 127,   0), (210, 180,   0), (166,  86,  40), (247, 129, 191),
            (102, 194, 165), (140,  86,  75), ( 23, 190, 207), (188, 189,  34),
        };

        public static (byte r, byte g, byte b) Rgb(int i)
        {
            int n = Palette.Length;
            return Palette[((i % n) + n) % n];
        }

        public static Autodesk.Revit.DB.Color Revit(int i)
        {
            var c = Rgb(i);
            return new Autodesk.Revit.DB.Color(c.r, c.g, c.b);
        }
    }
}
