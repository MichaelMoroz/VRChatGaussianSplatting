#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using GaussianSplatting.Editor.Importers;

namespace GaussianSplatting
{
    // Re-import a splat from the import metadata stamped on it at import time: either exactly (re-run with the
    // stored settings) or with edits (open the import window prefilled). Metadata lives in importMetadataJson.
    public static class GaussianSplatReimport
    {
        [MenuItem("CONTEXT/GaussianSplatObject/Reimport (exact)")]
        static void ReimportObjectExact(MenuCommand cmd) => ReimportExact((cmd.context as GaussianSplatObject)?.importMetadataJson);

        [MenuItem("CONTEXT/GaussianSplatObject/Reimport (edit settings)…")]
        static void ReimportObjectEdit(MenuCommand cmd) => ReimportEdit((cmd.context as GaussianSplatObject)?.importMetadataJson);

        static GaussianSplatImporter.ImportMetadata Parse(string json)
        {
            GaussianSplatImporter.ImportMetadata md = GaussianSplatImporter.ImportMetadata.FromJson(json);
            if (md == null || string.IsNullOrEmpty(md.sourcePath))
            {
                EditorUtility.DisplayDialog("Reimport", "This splat has no stored import metadata (it predates the metadata feature, or was imported another way). Re-import it once via Gaussian Splatting -> Import Splats... to stamp it.", "OK");
                return null;
            }
            if (!File.Exists(md.sourcePath))
            {
                EditorUtility.DisplayDialog("Reimport", $"Source file not found:\n{md.sourcePath}\n\nUse “Reimport (edit settings)…” and pick the file again.", "OK");
                return null;
            }
            return md;
        }

        static void ReimportExact(string json)
        {
            GaussianSplatImporter.ImportMetadata md = Parse(json);
            if (md == null) return;
            if (md.importAsLOD)
            {
                if (!TryImportLOD(md.sourcePath, md.prefabPath, md.lodChunkSize, md.options))
                {
                    return;
                }
            }
            else
            {
                GaussianSplatImporter.Import(md.sourcePath, md.prefabPath, md.options);
            }
            Debug.Log($"[GaussianSplatting] Re-imported {Path.GetFileName(md.sourcePath)} -> {md.prefabPath}");
        }

        static bool TryImportLOD(string sourcePath, string prefabPath, int chunkSize, GaussianSplatImporter.ImportOptions options)
        {
            Type importerType = Type.GetType("GaussianSplatting.GaussianSplatLODImporter, GaussianSplatting.Editor");
            if (importerType == null)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    importerType = assembly.GetType("GaussianSplatting.GaussianSplatLODImporter");
                    if (importerType != null)
                    {
                        break;
                    }
                }
            }

            MethodInfo importMethod = importerType?.GetMethod(
                "ImportLODToPrefab",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string), typeof(int), typeof(GaussianSplatImporter.ImportOptions) },
                null);
            if (importMethod == null)
            {
                EditorUtility.DisplayDialog("Reimport", "LOD importer entry point was not found in this package.", "OK");
                return false;
            }

            importMethod.Invoke(null, new object[] { sourcePath, prefabPath, chunkSize, options });
            return true;
        }

        static void ReimportEdit(string json)
        {
            GaussianSplatImporter.ImportMetadata md = GaussianSplatImporter.ImportMetadata.FromJson(json);
            if (md == null || string.IsNullOrEmpty(md.sourcePath))
            {
                EditorUtility.DisplayDialog("Reimport", "This splat has no stored import metadata. Re-import it once via Gaussian Splatting -> Import Splats... to stamp it.", "OK");
                return;
            }
            GaussianSplatImportWizard.OpenForReimport(md);
        }
    }
}
#endif
