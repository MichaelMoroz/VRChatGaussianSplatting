#if UNITY_EDITOR
using GaussianSplatting;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatCombiner))]
    [CanEditMultipleObjects]
    class GaussianSplatCombinerEditor : UnityEditor.Editor
    {
        SerializedProperty _combinedPositionsFormat;
        SerializedProperty _combinedRotationsFormat;
        SerializedProperty _combinedScalesFormat;
        SerializedProperty _combinedColorsFormat;
        SerializedProperty _combinedColorsCameraFormat;
        SerializedProperty _combinedStartRenderQueue;
        SerializedProperty _combinedTextureFormatsInitialized;

        void OnEnable()
        {
            _combinedPositionsFormat = serializedObject.FindProperty("combinedPositionsFormat");
            _combinedRotationsFormat = serializedObject.FindProperty("combinedRotationsFormat");
            _combinedScalesFormat = serializedObject.FindProperty("combinedScalesFormat");
            _combinedColorsFormat = serializedObject.FindProperty("combinedColorsFormat");
            _combinedColorsCameraFormat = serializedObject.FindProperty("combinedColorsCameraFormat");
            _combinedStartRenderQueue = serializedObject.FindProperty("combinedStartRenderQueue");
            _combinedTextureFormatsInitialized = serializedObject.FindProperty("combinedTextureFormatsInitialized");
        }

        public override void OnInspectorGUI()
        {
            DrawUdonSharpHeader();

            serializedObject.Update();

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Combined Texture Formats", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Changing these formats recreates the generated combined RenderTexture assets on the next editor refresh.", MessageType.Info);
            EditorGUILayout.PropertyField(_combinedPositionsFormat, new GUIContent("Positions"));
            EditorGUILayout.PropertyField(_combinedRotationsFormat, new GUIContent("Rotations"));
            EditorGUILayout.PropertyField(_combinedScalesFormat, new GUIContent("Scales"));
            EditorGUILayout.PropertyField(_combinedColorsFormat, new GUIContent("Colors"));
            EditorGUILayout.PropertyField(_combinedColorsCameraFormat, new GUIContent("Camera Colors"));
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Render Queue", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Starting render queue for the generated combined materials. Materials are assigned sequential queues from this value on the next editor refresh.", MessageType.Info);
            EditorGUILayout.PropertyField(_combinedStartRenderQueue, new GUIContent("Start Render Queue"));
            EditorGUILayout.EndVertical();

            bool formatChanged = EditorGUI.EndChangeCheck();
            if (formatChanged)
            {
                MarkFormatsInitialized();
            }

            bool changed = serializedObject.ApplyModifiedProperties();
            bool rebuildPressed = GUILayout.Button("Rebuild Combined Resources");
            if (changed || rebuildPressed)
            {
                RefreshCombinedResources();
            }

            EditorGUILayout.Space();
            DrawUdonSharpUtilities();
        }

        void MarkFormatsInitialized()
        {
            if (_combinedTextureFormatsInitialized != null)
            {
                _combinedTextureFormatsInitialized.boolValue = true;
            }
        }

        void RefreshCombinedResources()
        {
            for (int i = 0; i < targets.Length; i++)
            {
                GaussianSplatCombiner combiner = targets[i] as GaussianSplatCombiner;
                if (combiner == null || !combiner.gameObject.scene.IsValid())
                {
                    continue;
                }
                GaussianSplatRenderer owner = GaussianSplatRenderer.FindExistingSceneRenderer(combiner.gameObject.scene);
                if (owner != null)
                {
                    GaussianSplatRenderer.EnsureSceneRendererExists(owner.gameObject.scene);
                    EditorUtility.SetDirty(owner);
                }
                EditorUtility.SetDirty(combiner);
            }
        }

        void DrawUdonSharpHeader()
        {
            if (targets != null && targets.Length > 1)
            {
                UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(targets);
            }
            else
            {
                UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target);
            }
        }

        void DrawUdonSharpUtilities()
        {
            if (targets != null && targets.Length > 1)
            {
                UdonSharpGUI.DrawUtilities(targets);
            }
            else
            {
                UdonSharpGUI.DrawUtilities(target);
            }
        }
    }
}
#endif