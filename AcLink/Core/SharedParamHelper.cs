using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AcLink
{
    /// <summary>
    /// Ensures the AC Link shared parameters (VXD_AClinkName, VXD_AClinkUid) are
    /// bound to the Mechanical Equipment category AND set to "values can vary by
    /// group instance" — otherwise every instance of a group would share one value
    /// and per-unit system assignment inside groups would be impossible.
    ///
    /// Uses ForgeTypeId / GroupTypeId throughout (required for Revit 2025+/2026).
    /// </summary>
    public static class SharedParamHelper
    {
        private const string GroupName = "AC Link";

        private static readonly string[] OurParamNames =
            { AcLinkParams.AClinkNameText, AcLinkParams.AClinkUidText, AcLinkParams.AClinkSysKeyText };

        private static string OurParamFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcLink",
                "AcLink_SharedParams.txt");

        public static void EnsureParameters(Document doc)
        {
            bool allBound = OurParamNames.All(n => IsBoundByName(doc, n));
            if (!allBound)
                BindParameters(doc);

            // Always (idempotently) make sure the params can vary by group instance —
            // this also migrates models bound by an earlier AC Link version.
            EnsureVaryBetweenGroups(doc);
        }

        // ── Binding ──────────────────────────────────────────────────────────

        private static void BindParameters(Document doc)
        {
            var app = doc.Application;
            string origFile = app.SharedParametersFilename;

            try
            {
                EnsureFileExists();
                app.SharedParametersFilename = OurParamFilePath;

                var defFile = app.OpenSharedParameterFile();
                if (defFile == null) return;

                var group = defFile.Groups.get_Item(GroupName)
                         ?? defFile.Groups.Create(GroupName);

                var cats = app.Create.NewCategorySet();
                cats.Insert(doc.Settings.Categories
                              .get_Item(BuiltInCategory.OST_MechanicalEquipment));
                var binding = app.Create.NewInstanceBinding(cats);

                using (var tx = new Transaction(doc, "Bind AC Link shared parameters"))
                {
                    tx.Start();
                    TryBind(doc, group, defFile, binding,
                        AcLinkParams.AClinkNameText, AcLinkParams.AClinkName,
                        SpecTypeId.String.Text, GroupTypeId.IdentityData);
                    TryBind(doc, group, defFile, binding,
                        AcLinkParams.AClinkUidText, AcLinkParams.AClinkUid,
                        SpecTypeId.String.Text, GroupTypeId.IdentityData,
                        userModifiable: false);
                    TryBind(doc, group, defFile, binding,
                        AcLinkParams.AClinkSysKeyText, AcLinkParams.AClinkSysKey,
                        SpecTypeId.String.Text, GroupTypeId.IdentityData,
                        userModifiable: false);
                    tx.Commit();
                }
            }
            finally
            {
                app.SharedParametersFilename = origFile ?? string.Empty;
            }
        }

        private static void TryBind(
            Document doc, DefinitionGroup group, DefinitionFile defFile,
            InstanceBinding binding, string name, Guid guid,
            ForgeTypeId spec, ForgeTypeId groupTypeId, bool userModifiable = true)
        {
            if (IsBoundByName(doc, name)) return;

            // Reuse an existing definition with this GUID anywhere in the file.
            ExternalDefinition def = null;
            foreach (DefinitionGroup g in defFile.Groups)
            {
                foreach (Definition d in g.Definitions)
                    if (d is ExternalDefinition ed && ed.GUID == guid) { def = ed; break; }
                if (def != null) break;
            }

            if (def == null)
            {
                var opts = new ExternalDefinitionCreationOptions(name, spec)
                {
                    GUID = guid,
                    UserModifiable = userModifiable
                };
                def = group.Definitions.Create(opts) as ExternalDefinition;
            }

            if (def != null)
                doc.ParameterBindings.Insert(def, binding, groupTypeId);
        }

        // ── Vary by group instance ───────────────────────────────────────────

        private static void EnsureVaryBetweenGroups(Document doc)
        {
            var toSet = new List<InternalDefinition>();

            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is InternalDefinition d &&
                    OurParamNames.Contains(d.Name) &&
                    !d.VariesAcrossGroups)
                    toSet.Add(d);
            }

            if (toSet.Count == 0) return;

            using (var tx = new Transaction(doc, "AC Link – allow values to vary by group"))
            {
                tx.Start();
                foreach (var d in toSet) d.SetAllowVaryBetweenGroups(doc, true);
                tx.Commit();
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static bool IsBoundByName(Document doc, string name)
        {
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
                if (it.Key != null && it.Key.Name == name) return true;
            return false;
        }

        private static void EnsureFileExists()
        {
            string dir = Path.GetDirectoryName(OurParamFilePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(OurParamFilePath))
                File.WriteAllText(OurParamFilePath,
                    "# This is a Revit shared parameter file.\r\n" +
                    "# Do not edit manually.\r\n" +
                    "*META\tVERSION\tMINVERSION\r\n" +
                    "META\t2\t1\r\n" +
                    "*GROUP\tID\tNAME\r\n" +
                    "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n");
            }
    }
}
