// Assets/Editor/FindMissingScripts.cs

using UnityEngine;
using UnityEditor;

public class FindMissingScripts : EditorWindow
{
    [MenuItem("Tools/Find Missing Scripts")]
    static void ShowWindow()
    {
        GetWindow<FindMissingScripts>("Find Missing Scripts");
    }

    void OnGUI()
    {
        if (GUILayout.Button("Scan Scene"))
        {
            ScanForMissingScripts();
        }
    }

    void ScanForMissingScripts()
    {
        // 旧コード:
        // var allGameObjects = Object.FindObjectsOfType<GameObject>();

        // 新 API で全シーン内の GameObject を取得
        var allGameObjects = Object.FindObjectsByType<GameObject>();  

        int count = 0;
        foreach (var go in allGameObjects)
        {
            var components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null)
                {
                    Debug.LogError($"Missing script in GameObject '{go.name}' (in scene or prefab)",
                                   go);
                    count++;
                }
            }
        }

        Debug.Log($"Scan complete. Found {count} missing script(s).");
    }
}
