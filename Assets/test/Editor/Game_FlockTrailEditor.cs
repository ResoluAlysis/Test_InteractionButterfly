using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(Game_FlockTrail))]
public class Game_FlockTrailEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var t = (Game_FlockTrail)target;

        serializedObject.Update();

        // ---- 引用 ----
        EditorGUILayout.PropertyField(serializedObject.FindProperty("agent"));

        EditorGUILayout.Space();

        // ---- 受惊 ----
        EditorGUILayout.PropertyField(serializedObject.FindProperty("useFleeOverride"));

        EditorGUILayout.Space();
        
        EditorGUILayout.PropertyField(serializedObject.FindProperty("fadeOutHardCap"));

        // ---- 状态 → 拖尾 映射 ----
        var list = serializedObject.FindProperty("bindings");

        EditorGUILayout.LabelField("状态 → 拖尾 映射", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "每个状态指定一个拖尾对象。\n" +
            "• 子物体名：在本物体 / 子物体里按名字查找（最省事）\n" +
            "• 直接引用：可以指向场景里的对象或其它 Prefab\n" +
            "两者都填时，优先用「子物体名」。",
            MessageType.Info);

        for (int i = 0; i < list.arraySize; i++)
        {
            var item = list.GetArrayElementAtIndex(i);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(
                item.FindPropertyRelative("state"),
                new GUIContent("状态"), GUILayout.Width(EditorGUIUtility.currentViewWidth * 0.5f));

            if (GUILayout.Button("移除", GUILayout.Width(48f)))
            {
                list.DeleteArrayElementAtIndex(i);
                break;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(item.FindPropertyRelative("childObjectName"));
            EditorGUILayout.PropertyField(item.FindPropertyRelative("trailObject"));
            EditorGUILayout.PropertyField(item.FindPropertyRelative("fadeOutMaxWait"));

            // 状态校验
            var stateValue = (Game_FlockTrail.TrailState)item.FindPropertyRelative("state").enumValueIndex;
            var nameProp = item.FindPropertyRelative("childObjectName");
            var objProp = item.FindPropertyRelative("trailObject");
            EditorGUILayout.PropertyField(item.FindPropertyRelative("fadeOutMaxWait"));

            if (string.IsNullOrEmpty(nameProp.stringValue) && objProp.objectReferenceValue == null)
                EditorGUILayout.HelpBox("未指定拖尾对象，该状态不会有拖尾。", MessageType.Warning);

            EditorGUILayout.EndVertical();
        }

        if (GUILayout.Button("添加一条映射"))
            list.InsertArrayElementAtIndex(list.arraySize);

        EditorGUILayout.Space();

        // ---- 状态覆盖检查 ----
        var seen = new System.Collections.Generic.HashSet<Game_FlockTrail.TrailState>();
        for (int i = 0; i < list.arraySize; i++)
        {
            var st = (Game_FlockTrail.TrailState)list
                .GetArrayElementAtIndex(i)
                .FindPropertyRelative("state").enumValueIndex;

            if (!seen.Add(st))
                EditorGUILayout.HelpBox($"状态 {st} 配置了多条，只有第一条生效。", MessageType.Warning);
        }

        // 提示缺失的状态
        var missing = new System.Collections.Generic.List<string>();
        foreach (Game_FlockTrail.TrailState st in System.Enum.GetValues(typeof(Game_FlockTrail.TrailState)))
            if (!seen.Contains(st)) missing.Add(st.ToString());

        if (missing.Count > 0)
            EditorGUILayout.HelpBox("未配置的状态： " + string.Join(", ", missing), MessageType.None);

        serializedObject.ApplyModifiedProperties();
    }
}