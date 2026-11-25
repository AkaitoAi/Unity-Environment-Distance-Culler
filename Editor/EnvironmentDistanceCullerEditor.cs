using UnityEditor;
using UnityEngine;

namespace EnvironmentCulling.Editor
{
    [CustomEditor(typeof(EnvironmentDistanceCuller))]
    public class EnvironmentDistanceCullerEditor : UnityEditor.Editor
    {
        private static GUIStyle cachedLabelStyle;

        public override void OnInspectorGUI()
        {
            EnvironmentDistanceCuller culler = (EnvironmentDistanceCuller)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Environment Distance Culler dynamically optimizes children under this GameObject by enabling/disabling objects based on proximity to the target.",
                MessageType.Info);

            EditorGUILayout.Space();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Optimization Statistics", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Total Tracked Objects: {culler.TotalTrackedCount}");

            if (Application.isPlaying)
            {
                GUI.color = Color.green;
                EditorGUILayout.LabelField($"Active Objects: {culler.ActiveCount}");
                GUI.color = Color.cyan;
                EditorGUILayout.LabelField($"Culled Objects: {culler.CulledCount}");
                EditorGUILayout.LabelField($"Objects Evaluated Last Check: {culler.LastEvaluatedObjectCount}");
                EditorGUILayout.LabelField($"Occupied Category Cells Visited: {culler.LastVisitedCellCount}");
                EditorGUILayout.LabelField($"Visibility Changes: {culler.LastVisibilityChangeCount}");
                GUI.color = Color.white;
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();

            if (GUILayout.Button("Scan & Categorize Hierarchy Now", GUILayout.Height(30)))
            {
                Undo.RecordObject(culler, "Scan & Categorize Hierarchy");
                culler.ScanAndInitialize();
                EditorUtility.SetDirty(culler);
            }

            EditorGUILayout.Space();
            DrawDefaultInspector();
        }

        private void OnSceneGUI()
        {
            EnvironmentDistanceCuller culler = (EnvironmentDistanceCuller)target;
            if (culler == null) return;

            SerializedProperty drawGizmosProp = serializedObject.FindProperty("drawGizmosInScene");
            if (drawGizmosProp != null && !drawGizmosProp.boolValue) return;

            if (cachedLabelStyle == null)
            {
                cachedLabelStyle = new GUIStyle { fontStyle = FontStyle.Bold };
            }

            SerializedProperty targetProp = serializedObject.FindProperty("targetTransform");
            Transform targetTrans = targetProp != null ? (Transform)targetProp.objectReferenceValue : null;
            Vector3 center = targetTrans != null ? targetTrans.position : culler.transform.position;

            SerializedProperty categoriesProp = serializedObject.FindProperty("categories");
            if (categoriesProp == null || !categoriesProp.isArray) return;

            for (int i = 0; i < categoriesProp.arraySize; i++)
            {
                SerializedProperty catProp = categoriesProp.GetArrayElementAtIndex(i);
                SerializedProperty nameProp = catProp.FindPropertyRelative("categoryName");
                SerializedProperty distProp = catProp.FindPropertyRelative("maxDistance");
                SerializedProperty colorProp = catProp.FindPropertyRelative("gizmoColor");
                SerializedProperty alwaysVisProp = catProp.FindPropertyRelative("alwaysVisible");
                SerializedProperty frustumProp = catProp.FindPropertyRelative("enableFrustumCulling");

                if (alwaysVisProp != null && alwaysVisProp.boolValue) continue;

                bool frustumEnabled = frustumProp == null || frustumProp.boolValue;
                float radius = distProp != null ? distProp.floatValue : 100f;
                Color color = colorProp != null ? colorProp.colorValue : Color.green;
                string catName = nameProp != null ? nameProp.stringValue : $"Category {i}";

                Handles.color = color;
                Handles.DrawWireDisc(center, Vector3.up, radius);

                cachedLabelStyle.normal.textColor = color;

                Vector3 labelPos = center + new Vector3(radius * 0.707f, 0f, radius * 0.707f);
                string frustumSuffix = frustumEnabled ? string.Empty : " [No Frustum]";
                Handles.Label(labelPos, $"{catName} ({radius:0}m){frustumSuffix}", cachedLabelStyle);
            }
        }
    }
}
