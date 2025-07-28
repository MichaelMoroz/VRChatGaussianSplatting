using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
using VRC.Udon;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEditor;
using System.Collections.Generic;
#endif

namespace GaussianSplatting
{

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
[RequireComponent(typeof(MeshRenderer))]
public class GaussianSplatAnimator : UdonSharpBehaviour
{
    [SerializeField] public Material positionAnimator;
    [SerializeField] public Material colorAnimator;

    [SerializeField] public RenderTexture positionBuffer;
    [SerializeField] public RenderTexture colorBuffer;

    void Update() {
        int sideLength = positionBuffer.width;
        positionAnimator.SetInt("_ActualSplatCountSqrt", sideLength);
        positionAnimator.SetInt("_ActualSplatCount", sideLength * sideLength);
        colorAnimator.SetInt("_ActualSplatCountSqrt", sideLength);
        colorAnimator.SetInt("_ActualSplatCount", sideLength * sideLength);

        VRCGraphics.Blit(null, positionBuffer, positionAnimator);
        VRCGraphics.Blit(null, colorBuffer, colorAnimator);
    }
}

}