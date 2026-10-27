using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 把运行时（Play 模式）代码生成的激活 UI 保存为预制体。
///
/// 工作流：
///   ① Play 进入激活场景（UI 由代码生成）
///   ② 菜单 Tools → License → 把激活UI保存为预制体
///   ③ 停止 Play → 打开 Assets/Resources/ActivationUI.prefab 美化
///   ④ 再次运行 → 自动使用预制体
///
/// 注意：
///   - 保存的是运行时对象，会带上当时的文本（指纹/提示）。运行时代码会覆盖，无害。
///   - 按钮的 onClick 是运行时 AddListener 绑定的，不会被序列化。
///     这不是问题——代码在实例化后会重新绑定。
///   - ★ 美化时不要改控件名字，代码按名字查找：
///       Title / Hint / FpLabel / FpRow / FpField / CopyBtn
///       LicLabel / LicenseField / BtnRow / SubmitBtn / QuitBtn / PasteBtn / Message
/// </summary>
public static class ActivationUISaver
{
    private const string ResourceFolder = "Assets/Resources";
    private const string PrefabName     = "ActivationUI";
    private const string PrefabPath     = ResourceFolder + "/" + PrefabName + ".prefab";

    // ==================== 保存 ====================
    [MenuItem("Tools/License/把激活UI保存为预制体", priority = 20)]
    public static void SaveActivationUI()
    {
        if (!Application.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "需要先运行",
                "请先进入 Play 模式，等激活界面显示出来后再点这个菜单。\n\n" +
                "原因：UI 是代码在运行时生成的，非运行状态下没有真正的对象可保存。",
                "好");

            return;
        }

        var ui = Object.FindObjectOfType<Game_ActivationUI>();

        if (ui == null)
        {
            Debug.LogError("[ActivationUISaver] 场景里没有 Game_ActivationUI");
            return;
        }

        if (ui.UsingPrefab)
        {
            bool go = EditorUtility.DisplayDialog(
                "已经在用预制体",
                $"当前 UI 是从预制体实例化的。\n" +
                $"继续会用运行时状态覆盖：\n{PrefabPath}\n\n要继续吗？",
                "覆盖", "取消");

            if (!go) return;
        }

        Canvas canvas = ui.GetComponentInChildren<Canvas>(true);

        if (canvas == null)
        {
            Debug.LogError("[ActivationUISaver] 在 Game_ActivationUI 的子物体里找不到 Canvas");
            return;
        }

        // ---- 确保目录存在 ----
        if (!Directory.Exists(ResourceFolder))
        {
            Directory.CreateDirectory(ResourceFolder);
            AssetDatabase.Refresh();
            Debug.Log($"[ActivationUISaver] 已创建目录 {ResourceFolder}");
        }

        // ---- 保存 ----
        try
        {
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(canvas.gameObject, PrefabPath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (prefab == null)
            {
                Debug.LogError($"[ActivationUISaver] 保存失败：{PrefabPath}");
                return;
            }

            Debug.Log(
                $"[ActivationUISaver] 已保存 → {PrefabPath}\n" +
                "① 停止 Play\n" +
                "② 双击打开这个预制体美化布局\n" +
                "③ 再次运行会自动使用它\n" +
                "★ 不要改控件名字（代码按名字绑定）");

            EditorGUIUtility.PingObject(prefab);
            Selection.activeObject = prefab;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ActivationUISaver] 保存异常：{e}");
        }
    }

    // ==================== 删除（回到代码生成）====================
    [MenuItem("Tools/License/删除激活UI预制体", priority = 21)]
    public static void DeleteActivationUI()
    {
        if (!File.Exists(PrefabPath))
        {
            EditorUtility.DisplayDialog("没有预制体", $"{PrefabPath} 不存在。", "好");
            return;
        }

        bool go = EditorUtility.DisplayDialog(
            "删除预制体",
            $"确定删除？\n{PrefabPath}\n\n删除后激活界面会回到代码生成的版本。",
            "删除", "取消");

        if (!go) return;

        AssetDatabase.DeleteAsset(PrefabPath);
        AssetDatabase.Refresh();

        Debug.Log($"[ActivationUISaver] 已删除 {PrefabPath} → 下次运行使用代码生成的 UI");
    }

    // ==================== 打开预制体 ====================
    [MenuItem("Tools/License/打开激活UI预制体", priority = 22)]
    public static void OpenActivationUI()
    {
        if (!File.Exists(PrefabPath))
        {
            EditorUtility.DisplayDialog(
                "没有预制体",
                $"{PrefabPath} 不存在。\n\n先 Play 一次，再用「把激活UI保存为预制体」。",
                "好");

            return;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

        if (prefab == null)
        {
            Debug.LogError($"[ActivationUISaver] 加载失败：{PrefabPath}");
            return;
        }

        AssetDatabase.OpenAsset(prefab);
    }

    // ==================== 菜单可用性 ====================
    [MenuItem("Tools/License/把激活UI保存为预制体", true)]
    private static bool SaveValidate()
    {
        // 非运行状态也能点（点了会给提示），这里只是保持菜单常亮
        return true;
    }
}
