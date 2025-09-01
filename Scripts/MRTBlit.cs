using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

public class MRTBlit : UdonSharpBehaviour
{
    [SerializeField] Camera _cam;
    [SerializeField] MeshRenderer _quad;
    [SerializeField] float _expectedFar = 173.31f;
    [SerializeField] float _farTolerance = 0.05f;

    RenderBuffer[] _colorBufCache;
    bool _ready;

    void Start()
    {
        if (_cam == null || _quad == null) return;
        _cam.stereoTargetEye = StereoTargetEyeMask.None;
        _cam.useOcclusionCulling = false;
        _cam.enabled = false;
        _cam.farClipPlane = _expectedFar;
        _ready = true;
    }

    public void Blit(Material mat, RenderTexture[] outputs)
    {
        if (!_ready || mat == null || outputs == null || outputs.Length == 0) return;
        if (outputs[0] == null) return;

        int n = outputs.Length;
        if (_colorBufCache == null || _colorBufCache.Length != n) _colorBufCache = new RenderBuffer[n];
        for (int i = 0; i < n; i++) { if (outputs[i] == null) return; _colorBufCache[i] = outputs[i].colorBuffer; }

        _cam.SetTargetBuffers(_colorBufCache, outputs[0].depthBuffer);
        _quad.sharedMaterial = mat;

        var w = outputs[0].width; var h = outputs[0].height;
        mat.SetVector("_TimeParams", new Vector4(Time.time, Time.deltaTime, 0, 0));
        mat.SetVector("_Resolution", new Vector2(w, h));
        mat.SetFloat("_ExpectedFar", _expectedFar);
        mat.SetFloat("_FarTolerance", _farTolerance);

        _cam.Render();
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static void SetLayerRecursively(GameObject go, int layer)
    {
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    [ContextMenu("Setup MRT Blit Rig")]
    void SetupRig()
    {
        const string mirrorLayerName = "MirrorReflection";
        int layer = LayerMask.NameToLayer(mirrorLayerName);
        if (layer < 0) layer = 18;

        var root = new GameObject("UniversalMRTBlit_Rig");
        root.transform.SetParent(transform, false);

        var camGO = new GameObject("Camera");
        camGO.transform.SetParent(root.transform, false);

        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = "FullscreenQuad";
        q.transform.SetParent(root.transform, false);
        q.transform.localPosition = new Vector3(0, 0, 0.1f);
        q.transform.localScale = new Vector3(2, 2, 1);

        SetLayerRecursively(root, layer);

        var c = camGO.AddComponent<Camera>();
        c.orthographic = true; c.orthographicSize = 1f;
        c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = Color.black;
        c.nearClipPlane = 0.01f; c.farClipPlane = _expectedFar;
        c.cullingMask = 1 << layer;       // only MirrorReflection
        c.stereoTargetEye = StereoTargetEyeMask.None;
        c.depthTextureMode = DepthTextureMode.None;
        c.useOcclusionCulling = false;
        c.enabled = false;

        _cam = c;
        _quad = q.GetComponent<MeshRenderer>();

        EditorUtility.SetDirty(this);
        EditorSceneManager.MarkSceneDirty(gameObject.scene);
        Selection.activeObject = root;
        Debug.Log("Rig placed on MirrorReflection; main camera won’t draw it.");
    }
#endif
}