using System.Collections.Generic;
using UnityEngine;

//Ciallo～(∠・ω< )⌒★

/// <summary>
/// 蝴蝶群体：生成在屏幕外 → 朝中心飞入 → 飞出范围立刻回收 → 定时补足。
/// 本类是全项目里唯一负责"生/死"的地方，Agent 只负责飞。
/// </summary>
public class Game_FlockManager : MonoBehaviour, IConfigurable
{
    public static Game_FlockManager Instance { get; private set; }

    [Header("引用")]
    public Game_ObjectPool pool;
    public Camera cam;

    [Header("外形提供者（可选）")]
    [Header("配置多套蝴蝶 prefab 随机使用。留空则只用下面的 agentPrefab")]
    public Game_ButterflyProvider prefabProvider;

    [Header("引用")]
    public Game_FlockAnchor anchor;

    [Header("单 prefab 模式。配了 prefabProvider 时此项被忽略")]
    public Game_FlockChild_Test agentPrefab;
    
    [Header("数量")]
    [Header("场上维持的蝴蝶数量")]
    public int targetCount = 60;
    [Header("分帧：每只蝴蝶每 N 帧思考一次")]
    public int divisor = 3; 
    [Header("开局是否预生成池对象")]
    public bool prewarmOnStart = true;

    [Header("生成 / 回收")]
    [Header("生成点：在相机视野外这么远处")]
    public float spawnDistance = 2f;
    [Header("回收点：在相机视野外这么远处。必须 > spawnDistance，Awake 会自动保证")]
    public float recycleMargin = 4f;
    [Header("每 respawnInterval 秒补 1 只，直到 targetCount")]
    public float respawnInterval = 0.15f;
    [Header("开局一次性生成，不走补足间隔")]
    public bool spawnAllAtStart = true;

    [Header("调试")]
    public bool showThinkDebug = false;
    [Header("现场部署时关掉（真实触摸屏上鼠标模拟会导致一次触摸触发两次）")]
    public bool simulateMouseForDebug = true;

    public List<Game_FlockChild_Test> Agents { get; } = new List<Game_FlockChild_Test>();

    private int       _frameCount;
    private float     _respawnTimer;
    private Transform _fallbackRoot;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        Instance = this;
        
        ApplyConfig(Game_Config.Instance);

        if (cam == null) cam = Camera.main;
        if (anchor == null) anchor = FindObjectOfType<Game_FlockAnchor>();
        Input.simulateMouseWithTouches = simulateMouseForDebug;

        // ★ 回收矩形必须严格包含生成矩形，否则蝴蝶会一生成就被回收
        if (recycleMargin <= spawnDistance)
        {
            float fixedMargin = spawnDistance + 1f;
            Debug.LogWarning($"[FlockManager] recycleMargin({recycleMargin}) 必须大于 spawnDistance({spawnDistance})，" +
                             $"已自动调整为 {fixedMargin}，否则蝴蝶会一生成就被回收。");
            recycleMargin = fixedMargin;
        }

        if (pool == null) pool = GetComponentInChildren<Game_ObjectPool>();

        if (pool != null)
        {
            pool.transform.SetParent(transform, false);
            pool.transform.localPosition = Vector3.zero;

            // 池容量兜底
            pool.maxPerPrefab = Mathf.Max(pool.maxPerPrefab, targetCount + 8);

            if (prewarmOnStart) PrewarmAll();
        }
        else
        {
            Debug.LogWarning("[FlockManager] 未配置 Game_ObjectPool，退化为直接 Instantiate/Destroy");
            var root = new GameObject("_PooledAgents");
            root.transform.SetParent(transform, false);
            _fallbackRoot = root.transform;
        }

        if (spawnAllAtStart)
        {
            for (int i = 0; i < targetCount; i++) SpawnOne();
        }
    }
    
    public void ApplyConfig(Game_Config cfg)
    {
        if (cfg == null) return;

        const string S = "Flock";

        divisor         = Mathf.Max(1,   cfg.GetInt  (S, "divisor",          divisor));
        spawnDistance   = Mathf.Max(0f,  cfg.GetFloat(S, "spawnDistance",    spawnDistance));
        recycleMargin   = Mathf.Max(spawnDistance + 0.5f,
            cfg.GetFloat(S, "recycleMargin",         recycleMargin));
        respawnInterval = Mathf.Max(0.01f, cfg.GetFloat(S, "respawnInterval", respawnInterval));
        spawnAllAtStart = cfg.GetBool(S, "spawnAllAtStart",       spawnAllAtStart);
        simulateMouseForDebug = cfg.GetBool(S, "simulateMouseForDebug", simulateMouseForDebug);

        // 数量最后改：改完 MaintainCount 会自动增减
        targetCount = Mathf.Max(0, cfg.GetInt(S, "count", targetCount));
        ApplyToAgents(cfg);
    }
    
    /// <summary>
    /// 把 [Agent] 节的参数套到所有在场蝴蝶 + 所有 prefab 模板。
    /// ★ 必须同时处理 agentPrefab 和 prefabProvider 里的每一个 prefab，
    ///   否则多 prefab 随机模式下新生成的蝴蝶仍用旧值。
    /// </summary>
    private void ApplyToAgents(Game_Config cfg)
    {
        if (cfg == null) return;

        // ---- 1. 在场蝴蝶 ----
        for (int i = 0; i < Agents.Count; i++)
        {
            var a = Agents[i];
            if (a == null) continue;

            ApplyToAgent(a, cfg, rerollSize: true);
        }

        // ---- 2. 所有 prefab 模板（后续生成的用新值）----
        ApplyToPrefabTemplates(cfg);
    }

    /// <summary>把配置套到单个 Agent 实例</summary>
    private static void ApplyToAgent(Game_FlockChild_Test a, Game_Config cfg, bool rerollSize)
    {
        if (a == null || cfg == null) return;

        const string S = "Agent";

        // ---- 体积 ----
        a.sizeScale  = Mathf.Max(0.01f, cfg.GetFloat(S, "sizeScale",  a.sizeScale));
        a.sizeJitter = Mathf.Clamp01(cfg.GetFloat(S, "sizeJitter",    a.sizeJitter));

        // ---- 速度区间 ----
        a.speedSlow   = cfg.GetVector2(S, "speedSlow",   a.speedSlow);
        a.speedMedium = cfg.GetVector2(S, "speedMedium", a.speedMedium);
        a.speedFast   = cfg.GetVector2(S, "speedFast",   a.speedFast);

        // ---- 游荡盒 ----
        a.boxX           = Mathf.Max(0.1f, cfg.GetFloat(S, "boxX",           a.boxX));
        a.boxY           = Mathf.Max(0.1f, cfg.GetFloat(S, "boxY",           a.boxY));
        a.arriveDistance = Mathf.Max(0.05f,cfg.GetFloat(S, "arriveDistance", a.arriveDistance));

        // ---- 假深度 ----
        a.useFakeDepth = cfg.GetBool (S, "useFakeDepth", a.useFakeDepth);
        a.worldBottom  = cfg.GetFloat(S, "worldBottom",  a.worldBottom);
        a.worldTop     = cfg.GetFloat(S, "worldTop",     a.worldTop);
        a.nearScale    = Mathf.Max(0.01f, cfg.GetFloat(S, "nearScale", a.nearScale));
        a.farScale     = Mathf.Max(0.01f, cfg.GetFloat(S, "farScale",  a.farScale));

        // ---- 体积随机倍率：在改完 sizeJitter 之后重抽 ----
        if (rerollSize) a.RerollSizeMul();
    }

    /// <summary>把所有可能的 prefab 模板都套一遍配置</summary>
    private void ApplyToPrefabTemplates(Game_Config cfg)
    {
        // ---- A. provider 模式：遍历每一条 ----
        if (prefabProvider != null && prefabProvider.entries != null)
        {
            for (int i = 0; i < prefabProvider.entries.Count; i++)
            {
                var e = prefabProvider.entries[i];
                if (e == null || e.prefab == null) continue;

                var comp = e.prefab.GetComponent<Game_FlockChild_Test>();
                if (comp == null) continue;

                // prefab 模板不重抽体积倍率（它的值只作为新实例的起点）
                ApplyToAgent(comp, cfg, rerollSize: false);

                if (Debug.isDebugBuild)
                    Debug.Log($"[FlockDebug] prefab 模板已更新：{e.prefab.name} " +
                              $"sizeScale={comp.sizeScale} jitter={comp.sizeJitter}");
            }
        }

        // ---- B. 单 prefab 模式 ----
        if (agentPrefab != null)
        {
            ApplyToAgent(agentPrefab, cfg, rerollSize: false);

            if (Debug.isDebugBuild)
                Debug.Log($"[FlockDebug] agentPrefab 已更新：sizeScale={agentPrefab.sizeScale} " +
                          $"jitter={agentPrefab.sizeJitter}");
        }
    }
    
    /// <summary>按提供者的权重分配预生成数量，避免运行时首次请求某变体时卡顿</summary>
    private void PrewarmAll()
    {
        if (pool == null) return;

        if (prefabProvider != null && prefabProvider.ValidCount > 0)
        {
            var entries = prefabProvider.entries;

            float total = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e != null && e.prefab != null && e.weight > 0f)
                    total += e.weight;
            }

            if (total <= 0f) return;

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e == null || e.prefab == null || e.weight <= 0f) continue;

                int n = Mathf.RoundToInt(targetCount * (e.weight / total));
                if (n <= 0) continue;

                pool.Prewarm(e.prefab, n);
            }
        }
        else if (agentPrefab != null)
        {
            pool.Prewarm(agentPrefab.gameObject, targetCount);
        }
        else
        {
            Debug.LogWarning("[FlockManager] prewarmOnStart 为真，但没有可用的 prefab（agentPrefab 与 prefabProvider 都为空）");
        }
    }

    private void Update()
    {
        _frameCount++;

        // ---- 调度 ----
        for (int i = 0; i < Agents.Count; i++)
        {
            var a = Agents[i];
            if (!a) continue;

            bool shouldThink = divisor <= 1 || (_frameCount + i) % divisor == 0;
            a.Tick(shouldThink);
        }

        // ---- 回收 + 补足 ----
        Recycle();
        Refill();
    }

    // ==================== 回收（矩形） ====================
    private void Recycle()
    {
        Vector2 center = GetCenter();
        GetRecycleHalfExtents(out float rx, out float ry);

        for (int i = Agents.Count - 1; i >= 0; i--)
        {
            var a = Agents[i];

            if (!a)
            {
                Agents.RemoveAt(i);
                continue;
            }

            // ★ 矩形判断：任一轴超出即回收
            Vector2 p = a.transform.position;
            float dx = Mathf.Abs(p.x - center.x);
            float dy = Mathf.Abs(p.y - center.y);

            if (dx <= rx && dy <= ry) continue;

            Agents.RemoveAt(i);
            if (pool) pool.Release(a.gameObject);
            else Destroy(a.gameObject);
        }
    }

    /// <summary>回收矩形的半宽 / 半高（相机视野 + recycleMargin）</summary>
    private void GetRecycleHalfExtents(out float rx, out float ry)
    {
        if (!cam)
        {
            rx = ry = recycleMargin;
            return;
        }

        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;      // ★ 宽高比在这里体现，天然跟分辨率

        rx = halfW + recycleMargin;
        ry = halfH + recycleMargin;
    }

    // ==================== 补足 ====================
    private void Refill()
    {
        if (Agents.Count >= targetCount) return;

        _respawnTimer -= Time.deltaTime;
        if (_respawnTimer > 0f) return;

        _respawnTimer = respawnInterval;
        SpawnOne();
    }

    // ==================== 生成（唯一的生成入口） ====================
    private void SpawnOne()
    {
        // ---- 1. 选 prefab ----
        GameObject chosenPrefab = PickPrefab();

        if (!chosenPrefab)
        {
            Debug.LogError("[FlockManager] 没有可用的蝴蝶 prefab（检查 agentPrefab 或 prefabProvider）");
            return;
        }

        var prefabComp = chosenPrefab.GetComponent<Game_FlockChild_Test>();
        if (!prefabComp)
        {
            Debug.LogError($"[FlockManager] prefab \"{chosenPrefab.name}\" 上没有 Game_FlockChild_Test 组件");
            return;
        }

        Vector2 center   = GetCenter();
        Vector2 spawnPos = RandomSpawnPoint(center);      // 生成点：屏幕外，朝中心方向

        // ---- 2. 取对象 ----
        Game_FlockChild_Test agent;

        if (pool)
        {
            agent = pool.Acquire(prefabComp, spawnPos, Quaternion.identity);
        }
        else
        {
            agent = Instantiate(prefabComp, spawnPos, Quaternion.identity, _fallbackRoot);
            agent.OnSpawned();
        }

        if (agent == null) return;

        // ★★★ 池里取出的实例可能是热重载前创建的，参数是旧快照。
        //     每次取出都重新套一遍最新配置（含重抽体积倍率）。
        ApplyToAgent(agent, Game_Config.Instance, rerollSize: true);

        // ---- 3. 通知提供者（NoRepeat 配额统计）----
        prefabProvider?.ReportSpawned(chosenPrefab);

        // ---- 4. 初始化 ----
        agent.Init(this, anchor, Agents.Count);
        agent.SetTarget(RandomEnterTarget(center));

        Agents.Add(agent);
    }

    /// <summary>按策略挑一个 prefab</summary>
    private GameObject PickPrefab()
    {
        if (prefabProvider && prefabProvider.ValidCount > 0)
            return prefabProvider.Pick();

        return agentPrefab ? agentPrefab.gameObject : null;
    }

    // ==================== 几何 ====================
    private Vector2 GetCenter()
    {
        // 以相机中心为准。想用锚点漂移就把下面换成 anchor.Center
        return cam ? (Vector2)cam.transform.position : Vector2.zero;
    }

    /// <summary>在相机外 spawnDistance 处随机取一点，按周长加权（长边撒得更多）</summary>
    private Vector2 RandomSpawnPoint(Vector2 center)
    {
        if (!cam) return center + Random.insideUnitCircle.normalized * spawnDistance;

        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        // 生成矩形的半宽 / 半高
        float sx = halfW + spawnDistance;
        float sy = halfH + spawnDistance;

        // 四边按长度加权，避免宽屏时上下边被过度采样
        float wx = sx * 2f;      // 上下边长度
        float wy = sy * 2f;      // 左右边长度
        float total = (wx + wy) * 2f;

        float t = Random.Range(0f, total);

        if (t < wx)               return center + new Vector2(-sx + t, -sy);                          // 下边
        if (t < wx + wy)          return center + new Vector2(sx, -sy + (t - wx));                   // 右边
        if (t < wx * 2f + wy)     return center + new Vector2(sx - (t - wx - wy), sy);               // 上边
        return center + new Vector2(-sx, sy - (t - wx * 2f - wy));                                   // 左边
    }

    /// <summary>飞入目标：相机内一个随机点</summary>
    private Vector2 RandomEnterTarget(Vector2 center)
    {
        if (!cam) return center;

        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        return center + new Vector2(
            Random.Range(-halfW * 0.7f, halfW * 0.7f),
            Random.Range(-halfH * 0.7f, halfH * 0.7f));
    }

    private void OnDrawGizmosSelected()
    {
        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        Vector2 center = Application.isPlaying ? GetCenter() : (Vector2)cam.transform.position;

        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        // 生成矩形（绿）
        Gizmos.color = new Color(0f, 1f, 0f, 0.6f);
        Gizmos.DrawWireCube(center, new Vector3(
            (halfW + spawnDistance) * 2f,
            (halfH + spawnDistance) * 2f, 0f));

        // 回收矩形（红）——必须完整包住绿的
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.6f);
        Gizmos.DrawWireCube(center, new Vector3(
            (halfW + recycleMargin) * 2f,
            (halfH + recycleMargin) * 2f, 0f));
    }
}