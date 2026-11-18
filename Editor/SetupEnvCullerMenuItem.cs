using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EnvironmentCulling.Editor
{
    public static class SetupEnvCullerMenuItem
    {
        [MenuItem("Tools/Environment Culler/Attach Distance Culler to 'Env'")]
        public static void AttachDistanceCullerToEnv()
        {
            GameObject envGo = GameObject.Find("Env");
            if (envGo == null)
            {
                Debug.LogError("[SetupEnvCuller] Could not find a GameObject named 'Env' in the active scene. " +
                               "Attach EnvironmentDistanceCuller manually to your environment root GameObject.");
                return;
            }

            EnvironmentDistanceCuller culler = envGo.GetComponent<EnvironmentDistanceCuller>();
            if (culler == null)
            {
                Undo.RegisterCompleteObjectUndo(envGo, "Attach EnvironmentDistanceCuller");
                culler = Undo.AddComponent<EnvironmentDistanceCuller>(envGo);
                Debug.Log("[SetupEnvCuller] Attached EnvironmentDistanceCuller to 'Env'.");
            }

            culler.ScanAndInitialize();
            EditorUtility.SetDirty(envGo);
            EditorSceneManager.MarkSceneDirty(envGo.scene);

            Debug.Log($"[SetupEnvCuller] Setup complete on 'Env' with {culler.TotalTrackedCount} tracked objects.");
        }
    }
}
