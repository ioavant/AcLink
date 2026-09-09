using System;

namespace AcLink
{
    /// <summary>
    /// Shared-parameter identity for AC Link.
    ///
    /// Design: a SINGLE shared parameter <c>VXD_AClinkName</c> ties units into a
    /// system.  The unit's ROLE is encoded by a suffix on the value:
    ///   • outdoor unit (condenser) → base name + "-C"   e.g. "AC-01-C"
    ///   • indoor unit  (evaporator) → base name          e.g. "AC-01"
    /// The system itself is the set of units sharing the same BASE name; there is
    /// no separate registry / extensible storage.
    /// </summary>
    public static class AcLinkParams
    {
        // Fixed GUIDs — never change once shipped (existing models bind to them).
        public static readonly Guid AClinkName   = new Guid("ac11e000-0001-0000-0000-000000000001");
        public static readonly Guid AClinkUid    = new Guid("ac11e000-0002-0000-0000-000000000002");
        public static readonly Guid AClinkSysKey = new Guid("ac11e000-0003-0000-0000-000000000003");

        public const string AClinkNameText = "VXD_AClinkName";

        /// <summary>
        /// System key = the TRUE identity of a system; the name is just a label.
        /// Auto-generated at system creation and written to every unit.  Two systems
        /// accidentally named "AC-01" in different models have different keys, so
        /// they never merge silently; a cross-model join means sharing the key.
        /// </summary>
        public const string AClinkSysKeyText = "VXD_AClinkSysKey";

        /// <summary>
        /// Guard stamp = the owning element's ElementId.  Written whenever the unit
        /// is linked.  Because a copied element gets a NEW ElementId, a copy's stamp
        /// no longer matches its own id → the copy is detected and unlinked.
        /// Kept separate from VXD_AClinkName so the system name stays clean/readable.
        /// </summary>
        public const string AClinkUidText = "VXD_AClinkUid";

        /// <summary>Suffix that marks a value as the outdoor (condenser) unit.</summary>
        public const string OutdoorSuffix = "-C";

        /// <summary>
        /// Cross-model marker: keys of cross-model systems start with "X-".
        /// Unambiguous — a plain key's dash is always at position 5 ("K7F3-Q9W2").
        /// Encoding the flag in the key means it travels with the key into every
        /// model that joins the system; no extra parameter needed.
        /// </summary>
        public const string CrossKeyPrefix = "X-";

        public static bool IsCrossKey(string key) =>
            key != null && key.StartsWith(CrossKeyPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// New 8-character system key, e.g. "K7F3-Q9W2" ("X-K7F3-Q9W2" for a
        /// cross-model system).  Alphabet avoids the confusable characters
        /// I, L, O, 0, 1 so keys are easy to copy by eye.
        /// </summary>
        public static string NewSystemKey(bool crossModel = false)
        {
            const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
            var bytes = Guid.NewGuid().ToByteArray();
            var sb = new System.Text.StringBuilder(11);
            if (crossModel) sb.Append(CrossKeyPrefix);
            for (int i = 0; i < 8; i++)
            {
                sb.Append(alphabet[bytes[i] % alphabet.Length]);
                if (i == 3) sb.Append('-');
            }
            return sb.ToString();
        }
    }
}
