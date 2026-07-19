using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GaussianSplatting.Tests
{
    // Guards the "scene marked dirty without an authoring change" regression:
    // queueing / clearing a pending fused rebake is transient editor-only state and must never write the
    // serialized scene. A serialized queue flag previously re-dirtied the scene right after every save.
    public class FusedBakeQueueDirtyTests
    {
        [Test]
        public void QueueingAndClearingFusedBake_DoesNotDirtyScene()
        {
            const string scenePath = "Assets/__GSFusedBakeQueueDirtyTest__.unity";
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            GaussianSplatCombiner combiner = null;
            MethodInfo clear = typeof(GaussianSplatCombiner).GetMethod("ClearQueuedFusedLODBake", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                var go = new GameObject("Combiner");
                EditorSceneManager.MoveGameObjectToScene(go, scene);
                combiner = go.AddComponent<GaussianSplatCombiner>();

                Assert.IsTrue(EditorSceneManager.SaveScene(scene, scenePath), "failed to save temp scene");
                Assert.IsFalse(scene.isDirty, "scene should be clean immediately after save");

                MethodInfo queue = typeof(GaussianSplatCombiner).GetMethod("QueueFusedLODBake", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(queue, "QueueFusedLODBake not found");
                Assert.IsNotNull(clear, "ClearQueuedFusedLODBake not found");

                queue.Invoke(combiner, new object[] { 12345 });
                Assert.IsFalse(scene.isDirty, "queueing a fused rebake must not dirty the scene");

                queue.Invoke(combiner, new object[] { 67890 }); // re-queue with a changed signature
                Assert.IsFalse(scene.isDirty, "re-queueing a fused rebake must not dirty the scene");

                clear.Invoke(combiner, null);
                Assert.IsFalse(scene.isDirty, "clearing the fused rebake queue must not dirty the scene");
            }
            finally
            {
                if (combiner != null && clear != null) clear.Invoke(combiner, null); // leave no entry in the static queue
                EditorSceneManager.CloseScene(scene, true);
                AssetDatabase.DeleteAsset(scenePath);
            }
        }
    }
}
