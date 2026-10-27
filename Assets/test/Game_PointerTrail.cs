using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 指针划过留痕（对应目标工程的 CreateEffect / LineColliderController）。
///
/// 原理：把指针轨迹按固定间距撒成"点"，每个点从对象池取一个预制体丢在世界空间，
/// 延迟若干秒后回收。不画任何连续几何体 —— 尾迹是"一串逐渐消失的点"。
///
/// 与 Game_FlockGestureInput 完全解耦：它只管惊扰，这里只管留痕。
/// 划过即生效，不需要按鼠标键。
/// </summary>
public class Game_PointerTrail : MonoBehaviour, IConfigurable
{
    // ==================== 相机 / 射线 ====================
    [Header("相机")]
    public Camera cam;

    [Header("只在该层上留痕（留 Nothing 则整屏都可留痕）")]
    public LayerMask planeMask;

    [Header("玩法平面 z。正交相机下只影响返回值 z；没有碰撞面时用它兜底")]
    public float planeZ = 0f;

    // ==================== 预制体 ====================
    [Header("留痕预制体（随机挑一个）")]
    public GameObject[] trailPrefabs;

    // ==================== 间距 / 寿命 ====================
    [Header("撒点间距（世界单位，越小越密）")]
    [Header("沿轨迹前进这么远撒一个点。0.15~0.4 是比较自然的区间")]
    public float spacing = 0.25f;

    [Header("尾迹寿命（秒）")]
    [Header("每个点存活多久。目标游戏是 6 秒；太短会看起来在闪")]
    public float lifetime = 2.5f;

    [Header("寿命随机浮动比例，避免整齐消失")]
    [Range(0f, 0.6f)] public float lifetimeJitter = 0.3f;

    // ==================== 外观随机 ====================
    [Header("尺寸随机（避免整齐）")]
    public Vector2 scaleRange = new Vector2(0.7f, 1.3f);

    [Header("整体缩放")]
    [Tooltip("留痕整体缩放系数，叠在 scaleRange 的随机值之上。1 = 原始大小")]
    [Min(0.01f)] public float trailScale = 1f;

    [Header("位置抖动（0 = 严格沿轨迹）")]
    public float positionJitter = 0f;

    // ==================== 性能保护 ====================
    [Header("性能保护")]
    [Header("一帧最多撒多少个点。快速甩动时防止瞬间生成过多")]
    public int maxPointsPerFrame = 8;

    [Header("指针两帧间移动超过这个距离就视为【跳变】")]
    public float maxSegmentLength = 3f;

    [Header("跳变时在终点放一个点（雷达多探测点交替时必须开）")]
    public bool spawnOnJump = true;

    [Header("小于这个位移算「慢速/抖动」，在当前位置放一个点（事件模型）")]
    public float minMoveDistance = 0.001f;

    [Header("位置完全没变时也按间隔生成")]
    public bool spawnWhenStill = true;

    [Header("静止生成间隔（秒）。0.016 ≈ 每帧一个")]
    public float stillSpawnInterval = 0.016f;
    
    // ==================== 指针有效性 ====================
    [Header("指针范围")]
    [Header("勾上后，指针离开游戏视野时不会生成/续期兴趣点")]
    public bool requirePointerInside = true;
    [Header("兴趣点")]
    [Header("勾上后，把指针位置上报给 Game_FlockInterest（由它决定静止多久生成 POI）")]
    public bool reportToInterest = true;
    
    // ==================== 调试 ====================
    [Header("调试")]
    public bool showGizmos = true;

    // ==================== 运行时 ====================
    private struct PointerState
    {
        // ---- 留痕用 ----
        public Vector2 screenPrev;
        public Vector2 worldPrev;
        public bool    hasPrev;
        public float   leftover;
        public float   stillTimer;

        // ---- 兴趣点用 ----
        public Game_FlockInterest.PointerReport report;
    }

    private const int MaxPointers = 10;
    private readonly PointerState[] _pointers = new PointerState[MaxPointers];

    private void Reset()
    {
        cam = Camera.main;
    }

    private void Awake()
    {
        if (cam == null) cam = Camera.main;

        // ★ 从外置配置读取（Game_Config 有 DefaultExecutionOrder(-1000)，此时已就绪）
        ApplyConfig(Game_Config.Instance);

        if (trailPrefabs == null || trailPrefabs.Length == 0)
            Debug.LogWarning($"[PointerTrail] {name} 没有配置 trailPrefabs，只做兴趣点检测，不留痕");
    }

    public void ApplyConfig(Game_Config cfg)
    {
        if (cfg == null) return;

        const string s = "PointerTrail";
        
        trailScale = Mathf.Max(0.01f, cfg.GetFloat(s, "trailScale", trailScale));
        reportToInterest   = cfg.GetBool(s, "reportToInterest", reportToInterest);
        spacing = Mathf.Max(0.01f, cfg.GetFloat(s, "spacing", spacing));
        positionJitter = Mathf.Max(0f, cfg.GetFloat(s, "positionJitter", positionJitter));
        maxPointsPerFrame = Mathf.Clamp(cfg.GetInt(s, "maxPointsPerFrame", maxPointsPerFrame), 1, 16);
        maxSegmentLength = Mathf.Max(0.5f, cfg.GetFloat(s, "maxSegmentLength", maxSegmentLength));
    }
    
    private void Update()
    {
        if (!cam) return;

        var handledByTouch = false;

        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch t = Input.GetTouch(i);
            int id = ClampId(t.fingerId);

            if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled ||
                (requirePointerInside && !IsPointerInside(t.position)))
            {
                ReleasePointer(id);
                continue;
            }

            handledByTouch = true;
            ProcessPointer(id, t.position, true);      // 触摸：按住 = true
        }

        if (!handledByTouch)
        {
            if (requirePointerInside && !IsPointerInside(Input.mousePosition))
            {
                ReleasePointer(9);
                return;
            }

            // 鼠标：只有真的按下才算 held（否则松手后 POI 不会走 afterReleaseLifetime）
            bool held = Input.GetMouseButton(0) || Input.GetMouseButtonDown(0);
            ProcessPointer(9, Input.mousePosition, held);
        }
        
        // 临时加到 Game_PointerTrail.Update 里
        if (Input.touchCount == 0 && Time.frameCount % 20 == 0)
        {
            if (TryGetWorld(Input.mousePosition, out Vector2 w))
                Debug.Log($"[MouseWorld] {w}");
        }
    }

    private static int ClampId(int id) => (id < 0 || id >= MaxPointers) ? 9 : id;

    /// <summary>指针是否在游戏视野内</summary>
    private static bool IsPointerInside(Vector2 screenPos)
    {
        if (!Input.mousePresent && Input.touchCount == 0) return false;

        float w = Screen.width;
        float h = Screen.height;

        return screenPos.x >= 0f && screenPos.x < w &&
               screenPos.y >= 0f && screenPos.y < h;
    }

    /// <summary>指针离开游戏范围时清掉它的状态</summary>
    private void ReleasePointer(int id)
    {
        ref PointerState st = ref _pointers[id];

        // 指针离开 → 立刻解散由它产生的 POI
        if (st.report != null && st.report.poiCreated && Game_FlockInterest.Instance != null)
            Game_FlockInterest.Instance.RemovePointAt(st.report.stillWorld, 3f);

        st.hasPrev    = false;
        st.leftover   = 0f;
        st.stillTimer = 0f;
        st.report?.Reset();
    }
    
    private void ProcessPointer(int id, Vector2 screenPos, bool held)
    {
        if (requirePointerInside && !IsPointerInside(screenPos))
        {
            ReleasePointer(id);
            return;
        }

        if (!TryGetWorld(screenPos, out Vector2 world)) return;

        ref PointerState st = ref _pointers[id];

        // ---------- 第一次见到这个指针 ----------
        if (!st.hasPrev)
        {
            st.hasPrev    = true;
            st.screenPrev = screenPos;
            st.worldPrev  = world;
            st.leftover   = 0f;

            if (st.report == null) st.report = new Game_FlockInterest.PointerReport();
            st.report.Reset();
            st.report.SetStillPoint(world);
        }

        Vector2 from = st.worldPrev;
        float segLen = Vector2.Distance(from, world);

        st.screenPrev = screenPos;
        st.worldPrev  = world;

        // ---------- 1. 留痕 ----------
        //
        // 实测（雷达「鼠标移动」模式，单物体静止）：
        //   指针世界坐标在 0.2 × 0.08 范围内抖动，每帧位移约 0.003 ~ 0.01
        //   而 spacing = 0.25
        //   → 按 spacing 累积插值时 leftover 长期为负，几乎不生成 → 闪烁
        //
        // 所以按位移大小分四档，各用最适合的生成方式：
        if (trailPrefabs != null && trailPrefabs.Length > 0)
        {
            if (segLen > maxSegmentLength)
            {
                // ① 大跳变（雷达多探测点交替 / 物体瞬移）
                //    只在终点放一个，不插值（否则会拉一条横跨屏幕的线）
                st.leftover   = 0f;
                st.stillTimer = 0f;

                if (spawnOnJump) SpawnOne(world);
            }
            else if (segLen > spacing)
            {
                // ② 真实快速移动 → 沿线段插值，拖尾均匀
                SpawnAlongSegment(from, world, ref st.leftover);
                st.stillTimer = 0f;
            }
            else if (segLen > minMoveDistance)
            {
                // ③ 慢速 / 抖动 → 在当前位置放一个点（事件模型）
                //    ★ 这一档是解决"静止时闪烁"的关键：
                //      雷达每帧抖 0.003~0.01，落在这一档 → 每帧一个点
                st.leftover   = 0f;
                st.stillTimer = 0f;

                SpawnOne(world);
            }
            else
            {
                // ④ 完全没动 → 按时间兜底
                st.leftover = 0f;
                st.stillTimer += Time.deltaTime;

                if (spawnWhenStill && st.stillTimer >= stillSpawnInterval)
                {
                    st.stillTimer -= Mathf.Max(0.001f, stillSpawnInterval);
                    SpawnOne(world);
                }
            }
        }

        // ---------- 2. 上报兴趣点 ----------
        if (reportToInterest)
        {
            var interest = Game_FlockInterest.Instance;
            if (interest)
            {
                if (st.report == null) st.report = new Game_FlockInterest.PointerReport();

                st.report.active = true;
                st.report.held   = held;
                st.report.world  = world;

                interest.Report(st.report);
            }
        }
    }

    /// <summary>沿线段按 spacing 均匀撒点。leftover 让间距跨帧连续</summary>
    private void SpawnAlongSegment(Vector2 from, Vector2 to, ref float leftover)
    {
        Vector2 dir = to - from;
        float segLen = dir.magnitude;
        if (segLen < 1e-5f) return;

        dir /= segLen;

        float travelled = leftover;
        int spawned = 0;

        while (travelled + spacing <= segLen)
        {
            travelled += spacing;

            Vector2 p = from + dir * travelled;

            if (positionJitter > 0f)
                p += Random.insideUnitCircle * positionJitter;

            SpawnOne(p);

            if (++spawned >= maxPointsPerFrame) break;
        }

        // 剩余距离留到下一帧续用；若本帧被截断，也重置到合理值
        leftover = (spawned >= maxPointsPerFrame) ? 0f : (travelled - segLen);
    }

    private void SpawnOne(Vector2 pos)
    {
        var pool = GetPool();
        if (pool == null) return;

        GameObject prefab = trailPrefabs[Random.Range(0, trailPrefabs.Length)];
        if (prefab == null) return;

        GameObject go = pool.Acquire(prefab, new Vector3(pos.x, pos.y, planeZ), Quaternion.identity);
        if (go == null) return;

        float s = Random.Range(scaleRange.x, scaleRange.y) * trailScale;
        go.transform.localScale = new Vector3(s, s, 1f);

        float life = lifetime * (1f + Random.Range(-lifetimeJitter, lifetimeJitter));
        pool.Release(go, Mathf.Max(0.05f, life));
    }

    // ==================== 池 ====================
    private Game_ObjectPool _cachedPool;

    private Game_ObjectPool GetPool()
    {
        if (_cachedPool != null) return _cachedPool;

        var mgr = Game_FlockManager.Instance;
        if (mgr != null && mgr.pool != null)
        {
            _cachedPool = mgr.pool;
            return _cachedPool;
        }

        _cachedPool = FindObjectOfType<Game_ObjectPool>();
        if (_cachedPool == null)
            Debug.LogWarning("[PointerTrail] 场景里找不到 Game_ObjectPool，无法留痕");

        return _cachedPool;
    }

    // ==================== 屏幕 → 世界 ====================
    private bool TryGetWorld(Vector2 screenPos, out Vector2 world)
    {
        if (cam == null) { world = Vector2.zero; return false; }

        if (planeMask.value != 0)
        {
            Ray ray = cam.ScreenPointToRay(screenPos);
            if (Physics.Raycast(ray, out RaycastHit hit, 500f, planeMask))
            {
                world = hit.point;
                return true;
            }
        }

        // 兜底：正交相机直接换算（planeZ 需在裁剪范围内）
        float dist = Mathf.Max(planeZ, cam.nearClipPlane + 0.01f);
        world = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, dist));
        return true;
    }

    // ==================== 调试 ====================
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!showGizmos || !Application.isPlaying) return;

        Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.8f);

        for (int i = 0; i < MaxPointers; i++)
        {
            if (!_pointers[i].hasPrev) continue;
            Gizmos.DrawWireSphere(_pointers[i].worldPrev, spacing * 0.5f);
        }
    }
#endif
}