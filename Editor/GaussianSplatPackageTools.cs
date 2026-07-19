using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    static class GaussianSplatPackageTools
    {
        const string PackageRootAssetPath = "Assets/VRChatGaussianSplatting";
        const string PackageToolPath = "Editor/GaussianSplatPackageTools.cs";

        static readonly HashSet<string> DevelopmentRootFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Tools",
            "Tests",
            "ExampleSplats"
        };

        static readonly HashSet<string> DevelopmentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Example Scene.unity",
            "Advanced Examples.unity",
            "strip_high_sh.py",
            PackageToolPath
        };

        [MenuItem("Gaussian Splatting/Export Package...", false, 900)]
        static void ExportPackage()
        {
            string targetPath = EditorUtility.SaveFilePanel("Export Package", "", "VRChatGaussianSplatting.unitypackage", "unitypackage");
            if (string.IsNullOrEmpty(targetPath))
            {
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                string[] assets = BuildPackageAssetList();
                if (assets.Length == 0)
                {
                    throw new InvalidOperationException("No package assets matched the export filter.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ".");
                AssetDatabase.ExportPackage(assets, targetPath, ExportPackageOptions.Default);
                UnityEngine.Debug.Log($"[GaussianSplatting] Exported package with {assets.Length} assets: {targetPath}");
                EditorUtility.DisplayDialog("Package Export Complete", "Exported package:\n" + targetPath, "OK");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                EditorUtility.DisplayDialog("Package Export Failed", ex.Message, "OK");
            }
        }

        static string[] BuildPackageAssetList()
        {
            var assets = new List<string>();
            string rootFullPath = GetPackageRootFullPath();
            if (!Directory.Exists(rootFullPath))
            {
                throw new DirectoryNotFoundException(rootFullPath);
            }

            AddIfExportable(PackageRootAssetPath, assets);
            foreach (string directory in Directory.GetDirectories(rootFullPath, "*", SearchOption.AllDirectories))
            {
                AddIfExportable(ToAssetPath(directory), assets);
            }
            foreach (string file in Directory.GetFiles(rootFullPath, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                AddIfExportable(ToAssetPath(file), assets);
            }

            assets.Sort(StringComparer.OrdinalIgnoreCase);
            return assets.ToArray();
        }

        static void AddIfExportable(string assetPath, List<string> assets)
        {
            assetPath = NormalizeAssetPath(assetPath);
            if (!ShouldExport(assetPath))
            {
                return;
            }
            bool exists = AssetDatabase.IsValidFolder(assetPath) || AssetImporter.GetAtPath(assetPath) != null;
            if (exists)
            {
                assets.Add(assetPath);
            }
        }

        static bool ShouldExport(string assetPath)
        {
            if (!assetPath.Equals(PackageRootAssetPath, StringComparison.OrdinalIgnoreCase)
                && !assetPath.StartsWith(PackageRootAssetPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string relative = assetPath.Length == PackageRootAssetPath.Length
                ? string.Empty
                : assetPath.Substring(PackageRootAssetPath.Length + 1);
            if (string.IsNullOrEmpty(relative))
            {
                return true;
            }

            string top = relative;
            int slash = relative.IndexOf('/');
            if (slash >= 0)
            {
                top = relative.Substring(0, slash);
            }

            if (DevelopmentRootFolders.Contains(top))
            {
                return false;
            }
            if (relative.EndsWith("_PLAN.md", StringComparison.OrdinalIgnoreCase) || DevelopmentFiles.Contains(relative))
            {
                return false;
            }
            return true;
        }

        static string GetPackageRootFullPath()
        {
            string assetsRoot = Application.dataPath.Replace('\\', '/');
            return Path.GetFullPath(Path.Combine(assetsRoot, "VRChatGaussianSplatting"));
        }

        static string ToAssetPath(string fullPath)
        {
            string normalized = Path.GetFullPath(fullPath).Replace('\\', '/');
            string assetsRoot = Application.dataPath.Replace('\\', '/');
            if (!normalized.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Path is outside Assets: " + fullPath);
            }
            return NormalizeAssetPath("Assets" + normalized.Substring(assetsRoot.Length));
        }

        static string NormalizeAssetPath(string assetPath)
        {
            return assetPath.Replace('\\', '/').TrimEnd('/');
        }
    }
}
