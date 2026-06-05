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
            EditorGUILayout.LabelField(GSEditorText.T("Combined Texture Formats", "統合テクスチャ形式"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(GSEditorText.T(
                "Changing these formats recreates the generated combined RenderTexture assets on the next editor refresh.",
                "これらの形式を変更すると、次のエディタ更新時に生成済みの統合 RenderTexture アセットが再作成されます。"), MessageType.Info);
            EditorGUILayout.PropertyField(_combinedPositionsFormat, GSEditorText.C("Positions", "位置"));
            EditorGUILayout.PropertyField(_combinedRotationsFormat, GSEditorText.C("Rotations", "回転"));
            EditorGUILayout.PropertyField(_combinedScalesFormat, GSEditorText.C("Scales", "スケール"));
            EditorGUILayout.PropertyField(_combinedColorsFormat, GSEditorText.C("Colors", "色"));
            EditorGUILayout.PropertyField(_combinedColorsCameraFormat, GSEditorText.C("Camera Colors", "カメラ色"));
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(GSEditorText.T("Render Queue", "レンダーキュー"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(GSEditorText.T(
                "Starting render queue for the generated combined materials. Materials are assigned sequential queues from this value on the next editor refresh.",
                "生成される統合マテリアルの開始レンダーキューです。次のエディタ更新時に、この値から順番にキューが割り当てられます。"), MessageType.Info);
            EditorGUILayout.PropertyField(_combinedStartRenderQueue, GSEditorText.C("Start Render Queue", "開始レンダーキュー"));
            EditorGUILayout.EndVertical();

            bool formatChanged = EditorGUI.EndChangeCheck();
            if (formatChanged)
            {
                MarkFormatsInitialized();
            }

            bool changed = serializedObject.ApplyModifiedProperties();
            bool rebuildPressed = GUILayout.Button(GSEditorText.T("Rebuild Combined Resources", "統合リソースを再構築"));
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
