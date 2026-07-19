#if UNITY_EDITOR
using System.Collections.Generic;
using GaussianSplatting;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Minimal inspector for the splat UI: hides the dozens of internal UI references and shows only the bits a
    // world author touches - the additional description text and the gallery list.
    [CustomEditor(typeof(GaussianSplatRendererUI))]
    class GaussianSplatRendererUIEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target))
            {
                return;
            }

            serializedObject.Update();

            EditorGUILayout.LabelField("Gallery", EditorStyles.boldLabel);
            SerializedProperty galleryObjects = serializedObject.FindProperty("galleryObjects");
            SerializedProperty gallerySelectedIndex = serializedObject.FindProperty("_gallerySelectedIndex");
            EditorGUILayout.PropertyField(galleryObjects, new GUIContent("Splat Objects"), true);
            DrawActiveSplatPopup(galleryObjects, gallerySelectedIndex);
            EditorGUILayout.HelpBox(
                "Add splat objects here to turn the area into a gallery: in-game only the selected one renders, and " +
                "the list builds itself. Splat objects not listed here are left untouched.",
                MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("galleryEnabled"), new GUIContent("Gallery Enabled"));
            EditorGUILayout.HelpBox("Turn this off to temporarily disable gallery UI and selection enforcement without clearing the list or changing listed splat active states.", MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("galleryMasterLock"), new GUIContent("Master Lock"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Additional Description", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("customSubtitleEnglish"), new GUIContent("English"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("customSubtitleJapanese"), new GUIContent("Japanese"));

            if (serializedObject.ApplyModifiedProperties())
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    if (targets[i] is GaussianSplatRendererUI ui)
                    {
                        ui.ApplyGalleryInspectorState();
                        EditorUtility.SetDirty(ui);
                    }
                }
            }
        }

        static void DrawActiveSplatPopup(SerializedProperty galleryObjects, SerializedProperty selectedIndex)
        {
            if (galleryObjects == null || selectedIndex == null || !galleryObjects.isArray)
            {
                EditorGUILayout.HelpBox("Active splat selection is unavailable until the UI script metadata refreshes.", MessageType.Info);
                return;
            }

            List<int> indices = new List<int>();
            List<string> labels = new List<string>();
            for (int i = 0; i < galleryObjects.arraySize; i++)
            {
                GaussianSplatObject splat = galleryObjects.GetArrayElementAtIndex(i).objectReferenceValue as GaussianSplatObject;
                if (splat == null)
                {
                    continue;
                }
                indices.Add(i);
                labels.Add(i + ": " + DisplayName(splat));
            }

            if (indices.Count == 0)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Popup("Active Splat", 0, new[] { "No listed splats" });
                EditorGUI.EndDisabledGroup();
                return;
            }

            int selected = selectedIndex.intValue;
            int popupIndex = indices.IndexOf(selected);
            if (popupIndex < 0)
            {
                popupIndex = 0;
                selectedIndex.intValue = indices[0];
            }

            EditorGUI.BeginChangeCheck();
            popupIndex = EditorGUILayout.Popup("Active Splat", popupIndex, labels.ToArray());
            if (EditorGUI.EndChangeCheck())
            {
                selectedIndex.intValue = indices[Mathf.Clamp(popupIndex, 0, indices.Count - 1)];
            }
        }

        static string DisplayName(GaussianSplatObject splat)
        {
            if (splat == null)
            {
                return "";
            }
            if (!string.IsNullOrEmpty(splat.splatName))
            {
                return splat.splatName;
            }
            return splat.gameObject != null ? splat.gameObject.name : splat.name;
        }
    }
}
#endif
