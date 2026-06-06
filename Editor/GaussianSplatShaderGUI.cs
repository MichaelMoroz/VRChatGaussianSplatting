#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    public class GaussianSplatShaderGUI : ShaderGUI
    {
        public override void OnMaterialPreviewGUI(MaterialEditor materialEditor, Rect r, GUIStyle background)
        {
        }

        public override void OnMaterialInteractivePreviewGUI(MaterialEditor materialEditor, Rect r, GUIStyle background)
        {
        }
    }
}
#endif
