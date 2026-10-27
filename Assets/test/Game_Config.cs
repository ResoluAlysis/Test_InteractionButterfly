using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// 外置配置：从 exe 同级目录读 config.ini，供各组件在 Start 时主动拉取。
///
/// 格式：
///   # 注释
///   [Section]
///   key: value        （也支持 key = value）
///
/// 特性：
///   - Awake 里同步读取（本地小文件，无需协程），保证所有组件 Start 前就绪
///   - 缺文件 / 缺项 / 值非法 → 全部容错，保留 Inspector 默认值
///   - F5 热重载：重新解析并广播给所有 IConfigurable
///   - [App] 节可配置帧率 / 垂直同步 / 系统指针可见性
/// </summary>
[DefaultExecutionOrder(-1000)]      // ★ 保证比其它组件先 Awake
public class Game_Config : MonoBehaviour
{
    public static Game_Config Instance { get; private set; }

    // ==================== 配置 ====================
    [Header("配置文件")]
    [Tooltip("相对 exe 同级目录的路径（编辑器下相对项目根）")]
    public string configFile = "config.ini";

    [Header("加载")]
    public bool loadOnStart = true;

    [Header("应用（帧率）")]
    [Tooltip("目标帧率。-1 = 不限。vSync 失效时作为兜底，建议始终填 60")]
    public int targetFrameRate = 60;

    [Tooltip("垂直同步。0 = 关，1 = 开（跟随显示器刷新率），2 = 半刷新率")]
    [Range(0, 2)] public int vSyncCount = 0;

    [Header("应用（指针）")]
    [Tooltip("是否显示系统鼠标指针。触摸屏 / 展示装置建议关掉")]
    public bool showCursor = true;

    [Header("热重载")]
    public bool enableHotReload = true;
    public KeyCode reloadKey = KeyCode.F5;

    [Header("调试")]
    public bool logOnLoad = true;
    public bool logMissingKeys = false;

    // ==================== 应用级设置 ====================
    private bool  _frameChecked;
    private float _frameCheckTime;

    // 光标强制显示（激活界面等需要鼠标操作的 UI 用）
    private bool  _cursorForceVisible;
    
    // ==================== 解析结果 ====================
    // [section][key] = value，section 与 key 均转小写
    private readonly Dictionary<string, Dictionary<string, string>> _data
        = new Dictionary<string, Dictionary<string, string>>();

    public bool Loaded { get; private set; }

    /// <summary>配置重新加载完成后触发（仅热重载时）。订阅者在 OnDestroy 里记得取消订阅。</summary>
    public event Action Reloaded;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        // ==================== 单例去重 ====================
        // 常驻后跨场景时，新场景里的 Game_Config 会与旧的重复 → 销毁自己
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        // ==================== 常驻 ====================
        // DontDestroyOnLoad 只对根物体有效，挂子物体上是会失效的
        if (transform.parent != null)
        {
            Debug.LogWarning($"[Config] {name} 挂在子物体上，DontDestroyOnLoad 会失效。已自动提到根级。");
            transform.SetParent(null);
        }

        DontDestroyOnLoad(gameObject);

        if (loadOnStart) LoadSync();

        // ★ 帧率设置必须在第一帧渲染之前生效
        ApplyAppSettings();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        // 切回窗口时系统可能把指针恢复成可见，重新应用一次
        if (hasFocus) ApplyCursorSettings();
    }

    private void Update()
    {
        UpdateFrameCheck();        // ★ 新增
        
        if (!enableHotReload) return;
        if (!Input.GetKeyDown(reloadKey)) return;

        Debug.Log($"[Config] {reloadKey} 按下 → 热重载");
        Reload();
    }

    // ==================== 加载 ====================
    /// <summary>同步重新加载（热重载用：重新解析 + 广播）</summary>
    public void Reload()
    {
        LoadSync();
        ApplyAppSettings();        // ★ [App] 节（帧率 / 指针）也重新应用
        Broadcast();
        Reloaded?.Invoke();        // ★ 广播改值之后，通知需要"立刻重应用"的组件
    }

    // ==================== 应用级设置 ====================
    /// <summary>应用 [App] 节的设置（帧率 / 垂直同步 / 指针）</summary>
    private void ApplyAppSettings()
    {
        const string S = "App";

        if (Loaded)
        {
            targetFrameRate = GetInt (S, "targetFrameRate", targetFrameRate);
            vSyncCount      = GetInt (S, "vSyncCount",      vSyncCount);
            showCursor      = GetBool(S, "showCursor",      showCursor);
        }

        ApplyFrameSettings();
        ApplyCursorSettings();
        
        // 2 秒后校验一次 vSync 是否真的生效
        _frameChecked   = false;
        _frameCheckTime = Time.unscaledTime + 2f;
    }

    /// <summary>把帧率设置推给引擎。幂等，可反复调用。</summary>
    public void ApplyFrameSettings()
    {
        vSyncCount = Mathf.Clamp(vSyncCount, 0, 2);

        QualitySettings.vSyncCount = vSyncCount;

        // ★ 关键改动：无论 vSync 是否开启，都设 targetFrameRate 作为兜底。
        //   vSync 在窗口化 / 无边框全屏下常被 Windows DWM 绕过，
        //   vSync 真正生效时 Unity 会忽略这个值，不生效时它兜底。
        Application.targetFrameRate = targetFrameRate;

        if (logOnLoad)
        {
            Debug.Log($"[Config] 帧率：targetFrameRate={Application.targetFrameRate}，" +
                      $"vSyncCount={QualitySettings.vSyncCount}，" +
                      $"mode={Screen.fullScreenMode}");
        }
    }

    /// <summary>临时强制显示光标（激活界面等需要鼠标操作的 UI 用）</summary>
    public void ForceCursorVisible()
    {
        _cursorForceVisible = true;
        ApplyCursorSettings();
    }

    /// <summary>取消强制显示（回到 showCursor 的设定）</summary>
    public void ReleaseCursor()
    {
        _cursorForceVisible = false;
        ApplyCursorSettings();
    }

    /// <summary>应用系统指针可见性。幂等，可反复调用。</summary>
    public void ApplyCursorSettings()
    {
        // ★ 强制显示优先于配置：激活界面必须能看见指针
        bool visible = _cursorForceVisible || showCursor;
        Cursor.visible = visible;

        // ★ 不改 lockState：
        //   Locked 会把指针吸到屏幕中心，Confined 在无边框全屏下行为不稳定。
        //   隐藏指针时保持 None 最安全（触摸装置本来也不依赖指针位置）。
        Cursor.lockState = CursorLockMode.None;

        if (logOnLoad)
            Debug.Log($"[Config] 系统指针：visible={Cursor.visible}");
    }
    
    /// <summary>启动后校验 vSync 是否真的生效，失效则自动回退到 targetFrameRate</summary>
    private void UpdateFrameCheck()
    {
        if (_frameChecked) return;
        if (vSyncCount <= 0) { _frameChecked = true; return; }
        if (Time.unscaledTime < _frameCheckTime) return;

        _frameChecked = true;

        var dt = Time.smoothDeltaTime;
        if (dt <= 0f) return;

        var fps = 1f / dt;
        var hz  = Screen.currentResolution.refreshRateRatio.value;
        if (hz <= 0f) hz = 60f;

        // 实测帧率明显超过刷新率 → vSync 没生效
        if (fps > hz * 1.5f)
        {
            Debug.LogWarning(
                $"[Config] vSync 未生效：实测 {fps:F0} fps，显示器 {hz:F0} Hz。\n" +
                "窗口化 / 无边框全屏模式下 Windows DWM 会绕过 Unity 的 vSync。\n" +
                "已自动回退到 targetFrameRate 限速。");

            vSyncCount = 0;
            ApplyFrameSettings();
        }
        else if (logOnLoad)
        {
            Debug.Log($"[Config] vSync 生效：{fps:F0} fps / {hz:F0} Hz");
        }
    }

    private void LoadSync()
    {
        _data.Clear();
        Loaded = false;

        string path = Game_ExternalAssets.ResolvePath(configFile);

        if (!System.IO.File.Exists(path))
        {
            Debug.LogWarning($"[Config] 找不到配置文件：{path}\n" +
                             "全部使用 Inspector / 默认值。放一份 config.ini 到该目录即可。");
            return;
        }

        string text;
        try
        {
            text = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);

            // 兼容 UTF-8 无 BOM 的 GBK 文件：如果全是替换字符，退回默认编码
            if (text.Contains('\uFFFD'))
                text = System.IO.File.ReadAllText(path, System.Text.Encoding.Default);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Config] 读取失败：{path}\n{e.Message}");
            return;
        }

        Parse(text);
        Loaded = true;

        if (logOnLoad)
        {
            int keys = 0;
            foreach (var kv in _data) keys += kv.Value.Count;
            Debug.Log($"[Config] 已加载 {configFile}：{_data.Count} 个节，{keys} 个键");
        }
    }

    private void Parse(string text)
    {
        string section = "";
        string[] lines = text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();

            if (line.Length == 0) continue;
            if (line[0] == '#' || line[0] == ';') continue;
            if (line.StartsWith("//")) continue;

            // ---- 节 ----
            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                if (end > 0)
                    section = line.Substring(1, end - 1).Trim().ToLowerInvariant();
                continue;
            }

            // ---- 键值（只按第一个冒号/等号分割，值里可以再带冒号）----
            int sep = line.IndexOf(':');
            if (sep < 0) sep = line.IndexOf('=');
            if (sep <= 0) continue;

            string key = line.Substring(0, sep).Trim().ToLowerInvariant();
            string val = line.Substring(sep + 1).Trim().Trim('"');

            if (key.Length == 0) continue;

            if (!_data.TryGetValue(section, out var dict))
            {
                dict = new Dictionary<string, string>();
                _data[section] = dict;
            }

            dict[key] = val;
        }
    }

    /// <summary>把配置广播给所有 IConfigurable（热重载用）</summary>
    public void Broadcast()
    {
        var monos = FindObjectsOfType<MonoBehaviour>(true);
        int applied = 0;

        for (int i = 0; i < monos.Length; i++)
        {
            if (!(monos[i] is IConfigurable c)) continue;

            try { c.ApplyConfig(this); applied++; }
            catch (Exception e) { Debug.LogError($"[Config] {monos[i].name}.ApplyConfig 异常：{e.Message}"); }
        }

        if (applied == 0)
        {
            Debug.LogWarning(
                "[Config] 场景里没有任何组件实现 IConfigurable，配置只解析未应用。\n" +
                "需要给组件加上接口，例如：\n" +
                "    public class Game_FlockManager : MonoBehaviour, IConfigurable\n" +
                "    { public void ApplyConfig(Game_Config cfg) { ... } }");
            return;
        }

        if (logOnLoad)
            Debug.Log($"[Config] 已应用到 {applied} 个组件");
    }

    // ==================== 取值 API ====================
    public bool TryGet(string section, string key, out string value)
    {
        value = null;
        if (string.IsNullOrEmpty(key)) return false;

        string s = string.IsNullOrEmpty(section) ? "" : section.ToLowerInvariant();

        if (!_data.TryGetValue(s, out var dict)) return false;
        if (!dict.TryGetValue(key.ToLowerInvariant(), out value)) return false;

        return true;
    }

    public float GetFloat(string section, string key, float def)
    {
        if (!TryGet(section, key, out string v)) { WarnMissing(section, key); return def; }

        if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
            return r;

        Debug.LogWarning($"[Config] {section}.{key} 不是合法数字：\"{v}\" → 用默认 {def}");
        return def;
    }

    public int GetInt(string section, string key, int def)
    {
        if (!TryGet(section, key, out string v)) { WarnMissing(section, key); return def; }

        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r))
            return r;

        // 允许写成 "60.0"
        if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            return Mathf.RoundToInt(f);

        Debug.LogWarning($"[Config] {section}.{key} 不是合法整数：\"{v}\" → 用默认 {def}");
        return def;
    }

    public bool GetBool(string section, string key, bool def)
    {
        if (!TryGet(section, key, out string v)) { WarnMissing(section, key); return def; }

        switch (v.ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "on":  return true;
            case "0": case "false": case "no": case "off": return false;
        }

        Debug.LogWarning($"[Config] {section}.{key} 不是合法布尔：\"{v}\" → 用默认 {def}");
        return def;
    }

    /// <summary>支持 "0.2, 0.5" 或 "0.2 0.5"</summary>
    public Vector2 GetVector2(string section, string key, Vector2 def)
    {
        if (!TryGet(section, key, out string v)) { WarnMissing(section, key); return def; }

        string[] parts = v.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            Debug.LogWarning($"[Config] {section}.{key} 需要两个数：\"{v}\" → 用默认 {def}");
            return def;
        }

        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            return new Vector2(x, y);

        Debug.LogWarning($"[Config] {section}.{key} 解析失败：\"{v}\" → 用默认 {def}");
        return def;
    }

    private void WarnMissing(string section, string key)
    {
        if (logMissingKeys)
            Debug.Log($"[Config] 未配置 {section}.{key} → 用默认值");
    }
}

/// <summary>组件实现这个接口即可接收热重载广播</summary>
public interface IConfigurable
{
    void ApplyConfig(Game_Config cfg);
}
