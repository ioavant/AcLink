using System;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;

namespace AcLink
{
    internal class UpdateInfo
    {
        public string Version      { get; set; }
        public string DownloadUrl  { get; set; }
        public string ReleaseNotes { get; set; }
    }

    /// <summary>
    /// Background version check against a version.json hosted in a public GitHub
    /// repo. Mirrors ShaftMaster's UpdateChecker: no JSON library dependency (hand
    /// -rolled string scan), never throws into Revit, and the result is only ever
    /// surfaced as a clickable banner — there is no silent/automatic install.
    /// </summary>
    internal static class UpdateChecker
    {
        // Raw URL to version.json in the public releases repository.
        private const string VersionUrl =
            "https://raw.githubusercontent.com/ioavant/AcLink/main/version.json";

        public static UpdateInfo AvailableUpdate { get; private set; }

        public static void CheckInBackground()
        {
            Task.Run(() =>
            {
                try { DoCheck(); }
                catch { /* network unavailable, repo not published yet, etc. — ignore */ }
            });
        }

        private static void DoCheck()
        {
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "AcLink-UpdateChecker/1.0";
                string json = client.DownloadString(VersionUrl);
                var info = ParseJson(json);
                if (info?.Version == null) return;

                var current = Assembly.GetExecutingAssembly().GetName().Version;
                if (Version.TryParse(info.Version, out var remote) && remote > current)
                    AvailableUpdate = info;
            }
        }

        // Minimal hand-rolled JSON value extractor — no external dependency needed
        // for a flat 3-key object.
        private static UpdateInfo ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return new UpdateInfo
            {
                Version      = ExtractString(json, "version"),
                DownloadUrl  = ExtractString(json, "downloadUrl"),
                ReleaseNotes = ExtractString(json, "releaseNotes")
            };
        }

        private static string ExtractString(string json, string key)
        {
            string marker = "\"" + key + "\"";
            int i = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;

            int colon = json.IndexOf(':', i + marker.Length);
            if (colon < 0) return null;

            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;

            return json.Substring(q1 + 1, q2 - q1 - 1);
        }
    }
}
