using NUnit.Framework;
using ReachyMiniTeleop.Reachy;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ReachyMiniTeleop.Tests.Editor
{
    public sealed class ReachySceneConfigurationTests
    {
        private const string ScenePath = "Assets/Scenes/ReachyMiniTeleop.unity";

        [Test]
        public void MainScene_ReservesControllerThumbsticksForSoundTriggers()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            GameObject runtime = FindSceneObject(scene, "ReachyTeleopRuntime");
            Assert.IsNotNull(runtime, "ReachyTeleopRuntime must exist in the main teleop scene.");

            var controllerSoundTrigger = runtime.GetComponent<ReachyControllerSoundTrigger>();
            Assert.IsNotNull(
                controllerSoundTrigger,
                "ReachyTeleopRuntime must include ReachyControllerSoundTrigger for Quest controller sound shortcuts.");
            Assert.IsTrue(controllerSoundTrigger.enabled);
            Assert.IsTrue(
                controllerSoundTrigger.useOvrInputFallback,
                "Quest builds should keep OVRInput fallback enabled when XR primary2DAxis is unavailable.");

            GameObject locomotor = FindSceneObject(scene, "Locomotor");
            Assert.IsNotNull(locomotor, "Locomotor should remain in the scene as an intentionally disabled Meta building block.");
            Assert.IsFalse(
                locomotor.activeSelf,
                "Locomotor must stay inactive so thumbstick directions trigger sounds instead of moving the scene.");
        }

        private static GameObject FindSceneObject(Scene scene, string objectName)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                {
                    if (transform.name == objectName)
                        return transform.gameObject;
                }
            }

            return null;
        }
    }
}
