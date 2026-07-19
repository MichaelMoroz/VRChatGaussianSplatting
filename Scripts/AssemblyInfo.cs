// Exposes internal members to the editor-only test assembly. Guarded so it is present only in normal
// editor C# compilation (not the UdonSharp pass, not player builds), keeping it out of the shipped build.
#if UNITY_EDITOR && !COMPILER_UDONSHARP
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GaussianSplatting.Tests")]
#endif
