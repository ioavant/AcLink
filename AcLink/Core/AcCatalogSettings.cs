using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AcLink
{
    /// <summary>
    /// The catalog mappings + tolerance + auto-create preference, edited via
    /// <see cref="CatalogMappingDialog"/>.
    ///
    /// Stored in a small JSON file next to the DLL (install folder) so one set of
    /// mappings serves every project on the machine — which family/CSV pair builds
    /// which unit style is a property of the office's library, not of one model.
    /// Falls back to a per-user copy under %AppData%\AcLink\ (AC Link's existing
    /// convention, see SharedParamHelper) if the install folder is not writable.
    /// </summary>
    [DataContract]
    internal class AcCatalogSettings
    {
        private const string FileName = "AcLink.CatalogSettings.json";

        public const double DefaultTolerancePercent = 15.0;

        /// <summary>
        /// UTF-8 WITHOUT a byte-order mark. File.WriteAllText(..., Encoding.UTF8)
        /// prepends one, and DataContractJsonSerializer.ReadObject rejects it
        /// outright — silently losing every saved mapping.
        /// </summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Why the last load failed, or null. Load() falls back to defaults on a
        /// bad file, and without this the fallback looks identical to "nothing saved yet".</summary>
        public static string LastLoadError { get; private set; }

        [DataMember(Name = "tolerancePercent", Order = 0)]
        public double TolerancePercent { get; set; }

        [DataMember(Name = "autoCreateSystem", Order = 1)]
        public bool AutoCreateSystem { get; set; }

        [DataMember(Name = "mappings", Order = 2)]
        public List<AcCatalogMapping> Mappings { get; set; }

        public AcCatalogSettings()
        {
            TolerancePercent = DefaultTolerancePercent;
            Mappings = new List<AcCatalogMapping>();
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            if (Mappings == null) Mappings = new List<AcCatalogMapping>();
            if (TolerancePercent <= 0.0) TolerancePercent = DefaultTolerancePercent;
        }

        // ── Locations ─────────────────────────────────────────────────────────

        private static string SharedPath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    if (string.IsNullOrEmpty(dir)) return null;

                    // The net8 build (Revit 2025+) lives one level deeper, in "net8\".
                    // The settings belong to the PRODUCT folder so every Revit version
                    // reads and writes the same file.
                    string leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                    if (string.Equals(leaf, "net8", StringComparison.OrdinalIgnoreCase))
                        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));

                    return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, FileName);
                }
                catch { return null; }
            }
        }

        private static string UserPath
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "AcLink", FileName);
                }
                catch { return null; }
            }
        }

        // ── Load / Save ───────────────────────────────────────────────────────

        /// <summary>The stored settings, or the defaults. Never throws.</summary>
        public static AcCatalogSettings Load()
        {
            LastLoadError = null;
            return ReadFrom(UserPath) ?? ReadFrom(SharedPath) ?? new AcCatalogSettings();
        }

        /// <summary>
        /// Write next to the DLL, falling back to a per-user copy when that folder
        /// is not writable for this account. False only if both attempts failed.
        /// </summary>
        public bool Save()
        {
            if (TryWrite(SharedPath))
            {
                try
                {
                    string user = UserPath;
                    if (user != null && File.Exists(user)) File.Delete(user);
                }
                catch { /* leaving it is bad but not worth failing the save over */ }
                return true;
            }
            return TryWrite(UserPath);
        }

        private static AcCatalogSettings ReadFrom(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (!File.Exists(path)) return null;

                byte[] bytes = File.ReadAllBytes(path);
                int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                    ? 3 : 0;

                using (var stream = new MemoryStream(bytes, start, bytes.Length - start))
                {
                    var serializer = new DataContractJsonSerializer(typeof(AcCatalogSettings));
                    return serializer.ReadObject(stream) as AcCatalogSettings;
                }
            }
            catch (Exception exception)
            {
                LastLoadError = "The catalog settings file could not be read, so the defaults are in use:\n"
                              + path + "\n\n" + exception.Message;
                return null;
            }
        }

        private bool TryWrite(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, ToJson(), Utf8NoBom);
                return true;
            }
            catch { return false; }
        }

        private string ToJson()
        {
            using (var buffer = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(
                           buffer, Encoding.UTF8, false, true, "  "))
                {
                    new DataContractJsonSerializer(typeof(AcCatalogSettings)).WriteObject(writer, this);
                    writer.Flush();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }
    }
}
