// SPDX-License-Identifier: MIT
#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEngine;

namespace GaussianSplatting.Editor.Utils
{
    public struct ImportSplatData
    {
        public Vector3 pos;
        public Vector3 dc0;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }
}
#endif
