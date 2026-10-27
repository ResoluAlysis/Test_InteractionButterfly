using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 位置聚类版指针粒子生成器。
///
/// 解决的问题：雷达「鼠标移动」模式只给一个鼠标位置，且它在多个探测点之间跳，
/// 停留时间极不均匀（实测左 98% / 右 2%）。任何「跟随鼠标」的算法都会在
/// 停留久的那个位置生成更多粒子。
///
/// 解决方式：把单一的位置流聚类成多个「稳定位置」，每个位置按自己的节奏生成。
///
/// 和 Game_PointerTrailFaithful 的区别：
///   Faithful  = 复刻原工程（跟鼠标，必然不均匀）
///   Clustered = 改进版（聚类，各位置独立密度）
/// </summary>
public class Game_PointerTrailClustered : MonoBehaviour
{
    
    // ==================== 引用 ====================
    [Header("引用")]
    public Camera cam;
    public Game_ObjectPool pool;

    [Header("粒子预制体")]
    [Tooltip("和原工程一样：重复放同一个 prefab 可以当权重")]
    public GameObject[] effects;

    [Header("生成朝向")]
    public Vector3 spawnEuler = new Vector3(-90f, 0f, 0f);

    // ==================== 坐标 ====================
    [Header("坐标")]
    [Tooltip("平面 z（正交相机下用数学平面求交，不需要 Plane 层碰撞体）")]
    public float planeZ = 0f;

    [Tooltip("屏幕边缘噪点过滤（归一化坐标）。0.02 = 忽略最靠边的 2%")]
    [Range(0f, 0.2f)] public float edgeMargin = 0.02f;

    // ==================== 聚类 ====================
    [Header("聚类")]
    [Tooltip("多大的位移算「另一个位置」。要大于抖动幅度、小于两物体间距")]
    public float matchRadius = 1.0f;

    [Tooltip("最多同时追踪几个位置")]
    [Range(1, 16)] public int maxClusters = 8;

    [Tooltip("多久没被匹配就回收该位置（秒）")]
    public float staleTime = 0.5f;

    [Tooltip("聚类中心的平滑时间（秒）。0 = 完全跟随抖动；0.1 = 明显平滑")]
    public float smoothTime = 0.05f;

    [Tooltip("被匹配多少次后才开始生成（过滤偶发噪点）")]
    [Range(1, 10)] public int minHitsBeforeSpawn = 2;

    // ==================== 生成 ====================
    [Header("生成")]
    [Tooltip("每个位置的生成间隔（秒）。0.05 = 每秒 20 个")]
    public float spawnInterval = 0.05f;

    [Tooltip("生成位置的随机抖动（让粒子散开一点）")]
    public float positionJitter = 0.08f;

    [Tooltip("粒子存活时间（秒）。对齐 huaban02 的 startLifetime = 3")]
    public float lifetime = 3f;

    [Tooltip("寿命随机浮动比例")]
    [Range(0f, 0.6f)] public float lifetimeJitter = 0.2f;

    // ==================== 调试 ====================
    [Header("调试")]
    public bool showGizmos = true;
    public bool logDebug = false;

    // ==================== 运行时 ====================
    private class Cluster
    {
        public Vector2 position;        // 平滑后的中心
        public float   lastSeenTime;
        public float   spawnTimer;
        public int     hitCount;
        public int     spawnCount;
    }

    private readonly List<Cluster> _clusters = new List<Cluster>();
    private float _statTimer;
    private int   _statSpawnCount;

    // ==================== 生命周期 ====================
    private void Start()
    {
        if (cam == null) cam = Camera.main;

        if (pool == null)
        {
            var mgr = Game_FlockManager.Instance;

            if (mgr != null && mgr.pool != null) pool = mgr.pool;
            else pool = FindObjectOfType<Game_ObjectPool>();
        }

        if (pool == null)
            Debug.LogWarning("[TrailClustered] 找不到 Game_ObjectPool，会退化为 Instantiate/Destroy");

        if (effects == null || effects.Length == 0)
            Debug.LogWarning("[TrailClustered] effects 数组为空");
    }

    private void Update()
    {
        float now = Time.unscaledTime;
        float dt  = Time.unscaledDeltaTime;

        // ---------- 1. 取当前指针位置 ----------
        if (!TryGetWorld(Input.mousePosition, out Vector2 world))
        {
            UpdateClusters(now, dt);        // 没指针也要推进（让聚类自然过期）
            return;
        }

        // ---------- 2. 匹配或新建 ----------
        MatchOrCreate(world, now, dt);

        // ---------- 3. 每个聚类独立生成 ----------
        UpdateClusters(now, dt);

        // ---------- 统计 ----------
        if (logDebug)
        {
            _statTimer += dt;

            if (_statTimer >= 1f)
            {
                _statTimer = 0f;

                var sb = new System.Text.StringBuilder();
                sb.Append($"[TrailClustered] 聚类 {_clusters.Count} 个，过去 1 秒生成 {_statSpawnCount} 个:");

                for (int i = 0; i < _clusters.Count; i++)
                    sb.Append($" | C{i}({_clusters[i].position.x:F1},{_clusters[i].position.y:F1}) hits={_clusters[i].hitCount} spawns={_clusters[i].spawnCount}");

                Debug.Log(sb.ToString());

                _statSpawnCount = 0;
                for (int i = 0; i < _clusters.Count; i++) _clusters[i].spawnCount = 0;
            }
        }
    }

    // ==================== 聚类匹配 ====================
    private void MatchOrCreate(Vector2 world, float now, float dt)
    {
        Cluster best = null;
        float bestDist = matchRadius;

        for (int i = 0; i < _clusters.Count; i++)
        {
            float d = Vector2.Distance(_clusters[i].position, world);

            if (d < bestDist)
            {
                bestDist = d;
                best = _clusters[i];
            }
        }

        if (best == null)
        {
            // ---- 新建 ----
            if (_clusters.Count >= maxClusters)
            {
                // 淘汰最久未见的
                int oldest = 0;

                for (int i = 1; i < _clusters.Count; i++)
                    if (_clusters[i].lastSeenTime < _clusters[oldest].lastSeenTime)
                        oldest = i;

                if (logDebug)
                    Debug.Log($"[TrailClustered] 聚类已满，淘汰 C{oldest}({_clusters[oldest].position})");

                _clusters.RemoveAt(oldest);
            }

            var c = new Cluster
            {
                position     = world,
                lastSeenTime = now,
                hitCount     = 1,
                spawnTimer   = 0f,
            };

            _clusters.Add(c);

            if (logDebug)
                Debug.Log($"[TrailClustered] 新建聚类 C{_clusters.Count - 1} at {world}");

            return;
        }

        // ---- 更新 ----
        if (smoothTime > 0f)
        {
            float k = 1f - Mathf.Exp(-dt / smoothTime);
            best.position = Vector2.Lerp(best.position, world, k);
        }
        else
        {
            best.position = world;
        }

        best.lastSeenTime = now;
        best.hitCount++;
    }

    // ==================== 生成 / 清理 ====================
    private void UpdateClusters(float now, float dt)
    {
        for (int i = _clusters.Count - 1; i >= 0; i--)
        {
            Cluster c = _clusters[i];

            // 过期回收
            if (now - c.lastSeenTime > staleTime)
            {
                if (logDebug)
                    Debug.Log($"[TrailClustered] 回收 C{i}({c.position})，{staleTime:F2}s 未出现");

                _clusters.RemoveAt(i);
                continue;
            }

            // 命中次数不够（噪点）→ 只累积计时，不生成
            if (c.hitCount < minHitsBeforeSpawn) continue;

            c.spawnTimer += dt;

            float interval = Mathf.Max(0.005f, spawnInterval);
            int guard = 0;

            while (c.spawnTimer >= interval && guard++ < 8)
            {
                c.spawnTimer -= interval;

                SpawnAt(c.position);

                c.spawnCount++;
                _statSpawnCount++;
            }
        }
    }

    // ==================== 生成一个粒子 ====================
    private void SpawnAt(Vector2 pos)
    {
        if (effects == null || effects.Length == 0) return;

        Vector2 p = pos;

        if (positionJitter > 0f)
            p += Random.insideUnitCircle * positionJitter;

        GameObject prefab = effects[Random.Range(0, effects.Length)];
        if (prefab == null) return;

        Vector3    wp  = new Vector3(p.x, p.y, planeZ);
        Quaternion rot = Quaternion.Euler(spawnEuler);

        GameObject go;

        if (pool != null)
            go = pool.Acquire(prefab, wp, rot);
        else
            go = Instantiate(prefab, wp, rot);

        if (go == null) return;

        float life = lifetime * (1f + Random.Range(-lifetimeJitter, lifetimeJitter));
        life = Mathf.Max(0.05f, life);

        if (pool != null) pool.Release(go, life);
        else              Destroy(go, life);
    }

    // ==================== 坐标换算 ====================
    private bool TryGetWorld(Vector2 screenPos, out Vector2 world)
    {
        world = Vector2.zero;

        if (cam == null) return false;

        // 屏幕边缘噪点过滤
        if (edgeMargin > 0f)
        {
            float nx = screenPos.x / Mathf.Max(1f, Screen.width);
            float ny = screenPos.y / Mathf.Max(1f, Screen.height);

            if (nx < edgeMargin || nx > 1f - edgeMargin ||
                ny < edgeMargin || ny > 1f - edgeMargin)
                return false;
        }

        Ray r = cam.ScreenPointToRay(screenPos);

        if (Mathf.Abs(r.direction.z) < 1e-6f) return false;

        float t = (planeZ - r.origin.z) / r.direction.z;

        if (t <= 0f) return false;

        Vector3 p = r.origin + r.direction * t;

        world = new Vector2(p.x, p.y);
        return true;
    }

    // ==================== 调试 ====================
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!showGizmos || !Application.isPlaying) return;

        for (int i = 0; i < _clusters.Count; i++)
        {
            Cluster c = _clusters[i];

            bool ready = c.hitCount >= minHitsBeforeSpawn;

            Gizmos.color = ready
                ? new Color(0.2f, 1f, 0.4f, 0.9f)
                : new Color(0.6f, 0.6f, 0.6f, 0.5f);

            Vector3 p = new Vector3(c.position.x, c.position.y, planeZ);

            Gizmos.DrawWireSphere(p, 0.25f);

            // 匹配半径
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.25f);
            Gizmos.DrawWireSphere(p, matchRadius);

            // 生成散布半径
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.6f);
            Gizmos.DrawWireSphere(p, positionJitter);
        }
    }
#endif
}