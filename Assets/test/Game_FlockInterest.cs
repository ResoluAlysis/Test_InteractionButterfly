using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 兴趣点（POI）系统。所有 POI 参数都在这里，调用方只负责上报指针位置。
///
/// 两类点：
///   Pointer  —— 指针静止产生，限时（afterReleaseLifetime / maxHoldLifetime）
///   RestSpot —— 栖枝点（花/树枝），永久存在，由 Game_FlockRestSpot 注册
/// </summary>
public class Game_FlockInterest : MonoBehaviour, IConfigurable
{
    public static Game_FlockInterest Instance { get; private set; }

    public enum PointKind { Pointer, RestSpot }

    public class Point
    {
        public int       id;
        public PointKind kind = PointKind.Pointer;
        public Vector2   position;

        /// <summary>>0 = 限时；0 或负 = 永久（栖枝）</summary>
        public float expireTime;

        // ---- 每点独立的吸引参数 ----
        public float maxRange      = 6f;
        public float wanderRadius  = 2.5f;
        public float animBeatMul   = 1f;
        public int   maxOccupants  = 0;

        /// <summary>栖枝专用：最多停多久（秒）。到点后蝴蝶主动飞走。0 = 不轮换</summary>
        public float restDuration  = 0f;

        public readonly List<Game_FlockChild_Test> followers = new List<Game_FlockChild_Test>();

        // 轮换用（避免遍历中改动列表）
        private bool _isIterating;
        private readonly List<Game_FlockChild_Test> _pendingLeaves = new List<Game_FlockChild_Test>();

        public bool IsPermanent => expireTime <= 0f;

        public bool HasSpaceFor(Game_FlockChild_Test a)
        {
            if (maxOccupants <= 0) return true;
            if (a && followers.Contains(a)) return true;
            return followers.Count < maxOccupants;
        }

        /// <summary>推进本点的栖枝轮换。由 Game_FlockInterest 每帧调用</summary>
        public void Update()
        {
            if (restDuration <= 0f || followers.Count == 0) return;

            _isIterating = true;

            for (int i = followers.Count - 1; i >= 0; i--)
            {
                var a = followers[i];

                // 清理已被回收/销毁的
                if (a == null || !a.isActiveAndEnabled)
                {
                    followers.RemoveAt(i);
                    continue;
                }

                // 逃跑中不安排离开
                if (a.IsEscaping) continue;

                // 还没到点 → 继续停
                if (a.TickRestCountdown(Time.deltaTime, restDuration)) continue;

                // ★ 到点 → 请求离开
                a.MarkRestLeaveDecided();

                if (_isIterating) _pendingLeaves.Add(a);
                else              ApplyLeave(a);
            }

            _isIterating = false;

            // 统一处理离开（此时不再遍历 followers）
            if (_pendingLeaves.Count > 0)
            {
                for (int i = 0; i < _pendingLeaves.Count; i++)
                    ApplyLeave(_pendingLeaves[i]);

                _pendingLeaves.Clear();
            }
        }

        private void ApplyLeave(Game_FlockChild_Test a)
        {
            if (!a) return;

            followers.Remove(a);
            a.ClearInterest();          // 解绑
            a.MarkForceRetarget();      // 下一帧重选目标 → 飞走
        }

        public void Start()
        {
            ApplyConfig(Game_Config.Instance);
        }
        
        public void ApplyConfig(Game_Config cfg)
        {
            if (cfg == null) return;
            const string S = "RestSpot";
            
            restDuration = Mathf.Max(0f, cfg.GetFloat(S, "restDuration", restDuration));
            maxOccupants = Mathf.Max(1, cfg.GetInt(S, "maxOccupants", maxOccupants));

            //someField = cfg.GetFloat(S, "someKey", someField);
        }
    }

    // ==================== 生成（指针） ====================
    [Header("指针兴趣点")]
    [Tooltip("总开关。关掉后指针静止不再产生兴趣点，且已有的指针 POI 立刻清除（栖枝点不受影响）")]
    public bool enablePointerInterest = true;
    
    [Header("指针 POI")]
    [Tooltip("指针静止多久才产生兴趣点（秒）")]
    public float holdTime = 0.6f;

    [Tooltip("判定为静止的半径（世界单位）")]
    public float stillRadius = 0.35f;

    [Tooltip("松手后兴趣点存活多久（秒）")]
    public float afterReleaseLifetime = 4f;

    [Tooltip("按住不放时的最长寿命（秒）。<=0 表示按住期间不过期")]
    public float maxHoldLifetime = 8f;

    // ==================== 默认吸引参数 ====================
    [Header("默认吸引参数（可被每个点覆盖）")]
    [Tooltip("默认吸引半径")]
    public float attractRadius = 6f;

    [Tooltip("默认游荡半径")]
    public float wanderRadius = 2.5f;

    [Tooltip("解绑迟滞：超出 maxRange × 这个倍数才解绑")]
    [Range(1f, 2f)] public float releaseHysteresis = 1.3f;

    // ==================== 调试 ====================
    [Header("调试")]
    public bool showGizmos = true;
    public bool logDebug = false;

    private readonly List<Point> _points = new List<Point>();
    private int _nextId = 1;
    private bool _lastEnablePointerInterest = true;
    
    public IReadOnlyList<Point> Points => _points;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        Instance = this;

        // 参数体检
        if (holdTime <= 0f)
        {
            Debug.LogWarning($"[Interest] holdTime({holdTime}) 必须 > 0，已改为 0.6");
            holdTime = 0.6f;
        }

        if (afterReleaseLifetime <= 0f)
        {
            Debug.LogWarning($"[Interest] afterReleaseLifetime({afterReleaseLifetime}) 必须 > 0，" +
                             "否则指针 POI 会立即过期（表现为蝴蝶目标狂闪）。已改为 4");
            afterReleaseLifetime = 4f;
        }

        if (wanderRadius <= 0f) wanderRadius = 2.5f;
        
        _lastEnablePointerInterest = enablePointerInterest;
        // 注意：不要在这里压 attractRadius，栖枝点会用到自己的 maxRange
    }

    private void Start()
    {
        ApplyConfig(Game_Config.Instance);
    }

    public void ApplyConfig(Game_Config cfg)
    {
        if (!cfg) return;
        const string S = "Interest";

        enablePointerInterest = cfg.GetBool(S, "enablePointerInterest", enablePointerInterest);
        holdTime = Mathf.Max(0f, cfg.GetFloat(S, "holdTime", holdTime));
        stillRadius =  Mathf.Max(0.1f, cfg.GetFloat(S, "stillRadius", stillRadius));
        afterReleaseLifetime = Mathf.Max(0f, cfg.GetFloat(S, "afterReleaseLifetime", afterReleaseLifetime));
        maxHoldLifetime = Mathf.Max(0f, cfg.GetFloat(S, "maxHoldLifetime", maxHoldLifetime));
        attractRadius = Mathf.Max(0.1f, cfg.GetFloat(S, "attractRadius", attractRadius));
        wanderRadius = Mathf.Max(0.1f, cfg.GetFloat(S, "wanderRadius", wanderRadius));
        //divisor         = Mathf.Max(1,   cfg.GetInt  (S, "divisor",          divisor));
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        // ★ 开关变化检测：必须在 if (_points.Count == 0) 之前
        if (_lastEnablePointerInterest != enablePointerInterest)
        {
            _lastEnablePointerInterest = enablePointerInterest;

            if (!enablePointerInterest)
            {
                RemoveAllPointerPoints();

                if (logDebug) Debug.Log("[Interest] 指针兴趣点关闭 → 已清除所有指针 POI");
            }
            else if (logDebug)
            {
                Debug.Log("[Interest] 指针兴趣点开启");
            }
        }
        
        if (_points.Count == 0) return;

        float now = Time.time;

        for (int i = _points.Count - 1; i >= 0; i--)
        {
            var p = _points[i];

            // ★ 驱动栖枝轮换（内部会处理延迟离开）
            p.Update();

            // 永久点（栖枝）不参与过期
            if (p.IsPermanent) continue;
            if (now < p.expireTime) continue;

            ReleaseFollowers(p);
            _points.RemoveAt(i);
        }
    }

    /// <summary>解散某点的所有跟随者，让它们重选目标</summary>
    private void ReleaseFollowers(Point p)
    {
        var followers = p.followers;
        for (int f = 0; f < followers.Count; f++)
        {
            var a = followers[f];
            if (a == null) continue;

            a.ClearInterest();
            a.MarkForceRetarget();
        }
        followers.Clear();
    }
    
    /// <summary>清除所有指针 POI（栖枝点保留）</summary>
    private void RemoveAllPointerPoints()
    {
        for (int i = _points.Count - 1; i >= 0; i--)
        {
            var p = _points[i];
            if (p.kind != PointKind.Pointer) continue;

            ReleaseFollowers(p);
            _points.RemoveAt(i);
        }
    }
    
    // ==================== 指针上报 ====================
    public bool Report(PointerReport r)
    {
        // ★ 开关关闭时直接忽略上报
        if (!enablePointerInterest) return false;
        
        if (r == null || !r.active) return false;

        bool still = r.hasStillPoint &&
                     (r.world - r.stillWorld).sqrMagnitude <= stillRadius * stillRadius;

        if (!still)
        {
            r.SetStillPoint(r.world);
            r.stillTimer = 0f;
            r.poiCreated = false;
            return false;
        }

        r.stillTimer += Time.deltaTime;

        if (!r.poiCreated && r.stillTimer < holdTime) return false;

        bool isNew = !r.poiCreated;

        // 按住时用 maxHoldLifetime（<=0 表示不过期 → 给极大值）
        float life = r.held
            ? (maxHoldLifetime > 0f ? maxHoldLifetime : 99999f)
            : afterReleaseLifetime;

        AddPoint(r.stillWorld, life, r.poiCreated, r.poiId, out int usedId);

        r.poiCreated = true;
        r.poiId      = usedId;

        return isNew;
    }

    /// <summary>创建一个指针兴趣点</summary>
    public Point AddPoint(Vector2 pos, float lifetime, bool updateExisting, int existingId, out int usedId)
    {
        if (lifetime <= 0f) lifetime = Mathf.Max(afterReleaseLifetime, 0.5f);

        // 附近已有指针 POI 就复用它
        var existing = FindNearest(pos, Mathf.Max(stillRadius * 2f, 1f));
        if (existing != null)
        {
            existing.position   = pos;
            existing.expireTime = Time.time + lifetime;
            usedId = existing.id;
            return existing;
        }

        var p = new Point
        {
            id             = _nextId++,
            kind           = PointKind.Pointer,
            position       = pos,
            expireTime     = Time.time + lifetime,
            maxRange       = attractRadius,
            wanderRadius   = wanderRadius,
            animBeatMul    = 1f,
            maxOccupants   = 0,
        };

        _points.Add(p);
        WakeNearby(pos, attractRadius);

        if (logDebug) Debug.Log($"[Interest] 新建指针 POI @ {pos}，寿命 {lifetime:F1}s");

        usedId = p.id;
        return p;
    }

    // ==================== 栖枝注册 ====================
    /// <summary>注册一个栖枝点（永久）</summary>
    public Point AddRestSpot(Vector2 pos, float hoverRadius, float beatMul,
        float maxRange, int maxOccupants, float restDuration)
    {
        var p = new Point
        {
            id            = _nextId++,
            kind          = PointKind.RestSpot,
            position      = pos,
            expireTime    = 0f,                       // 永久
            maxRange      = maxRange,
            wanderRadius  = hoverRadius,
            animBeatMul   = beatMul,
            maxOccupants  = maxOccupants,
            restDuration  = restDuration,
        };

        _points.Add(p);
        WakeNearby(pos, maxRange);

        if (logDebug)
            Debug.Log($"[Interest] 注册栖枝 @ {pos}，悬停 {hoverRadius}，容量 {maxOccupants}，停留 {restDuration:F1}s");

        return p;
    }

    public void RemovePoint(Point p)
    {
        if (p == null) return;

        ReleaseFollowers(p);
        _points.Remove(p);
    }
    
    /// <summary>
    /// 立即移除某位置附近的指针 POI（指针离开游戏视野时调用）。
    /// 只移除 Pointer 类型的点 —— 栖枝是永久点，不该被指针行为误删。
    /// </summary>
    public void RemovePointAt(Vector2 pos, float radius = 2f)
    {
        float r2 = radius * radius;

        for (int i = _points.Count - 1; i >= 0; i--)
        {
            var p = _points[i];

            // ★ 栖枝点跳过，否则鼠标划出屏幕会把花上的蝴蝶全赶走
            if (p.kind == PointKind.RestSpot) continue;

            if ((p.position - pos).sqrMagnitude > r2) continue;

            ReleaseFollowers(p);
            _points.RemoveAt(i);

            if (logDebug) Debug.Log($"[Interest] 移除指针 POI @ {p.position}");
        }
    }

    // ==================== 查询 ====================

    /// <summary>最近的点，不限容量（内部用 / 调试用）</summary>
    public Point FindNearest(Vector2 worldPos, float radius)
    {
        if (_points.Count == 0) return null;

        float best = radius * radius;
        Point result = null;

        for (int i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            float d2 = (p.position - worldPos).sqrMagnitude;
            if (d2 > best) continue;

            best = d2;
            result = p;
        }

        return result;
    }

    /// <summary>
    /// 是否继续留在当前绑定的点上（带迟滞，防止边缘反复进出）。
    /// 当前点满了也允许保留——否则满员的栖枝会把自己的蝴蝶赶走。
    /// </summary>
    public Point FindNearestForRetain(Vector2 worldPos, Point current)
    {
        if (current == null) return null;

        // 已被移除？
        bool alive = false;
        for (int i = 0; i < _points.Count; i++)
        {
            if (_points[i] != current) continue;
            alive = true;
            break;
        }
        if (!alive) return null;

        float limit = current.maxRange * releaseHysteresis;
        float d2 = (current.position - worldPos).sqrMagnitude;

        return d2 <= limit * limit ? current : null;
    }

    /// <summary>找最近且"还有空位"的点（进入用）</summary>
    public Point FindNearestAvailable(Vector2 worldPos, Game_FlockChild_Test agent)
    {
        if (_points.Count == 0) return null;

        float bestD2 = float.MaxValue;
        Point result = null;

        for (int i = 0; i < _points.Count; i++)
        {
            var p = _points[i];

            if (!p.HasSpaceFor(agent)) continue;

            float d2 = (p.position - worldPos).sqrMagnitude;
            if (d2 > p.maxRange * p.maxRange) continue;
            if (d2 >= bestD2) continue;

            bestD2 = d2;
            result = p;
        }

        return result;
    }

    /// <summary>某位置是否在某个点的吸引范围内（给 Game_FlockRestSpot 做摆放校验）</summary>
    public bool IsOverlapping(Vector2 pos, float maxRange)
    {
        float r2 = maxRange * maxRange;

        for (int i = 0; i < _points.Count; i++)
            if ((_points[i].position - pos).sqrMagnitude < r2) return true;

        return false;
    }

    // ==================== 内部 ====================
    private void WakeNearby(Vector2 pos, float radius)
    {
        var mgr = Game_FlockManager.Instance;
        if (mgr == null) return;

        var agents = mgr.Agents;
        float r2 = radius * radius;

        for (int i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a == null) continue;
            if ((a.transform.position - (Vector3)pos).sqrMagnitude > r2) continue;

            a.MarkForceRetarget();
        }
    }

    // ==================== 调试 ====================
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!showGizmos || !Application.isPlaying || _points.Count == 0) return;

        for (int i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            bool isRest = p.kind == PointKind.RestSpot;

            // 吸引半径
            Gizmos.color = isRest
                ? new Color(0.2f, 0.9f, 1f, 0.22f)
                : new Color(1f, 0.8f, 0.2f, 0.35f);
            Gizmos.DrawWireSphere(p.position, p.maxRange);

            // 悬停/游荡半径
            Gizmos.color = isRest
                ? new Color(0.2f, 1f, 0.6f, 0.9f)
                : new Color(0.3f, 1f, 1f, 0.5f);
            Gizmos.DrawWireSphere(p.position, p.wanderRadius);

            // 中心
            Gizmos.color = isRest ? Color.cyan : new Color(1f, 0.5f, 0f, 1f);
            Gizmos.DrawWireSphere(p.position, 0.3f);

            // 容量占用
            if (p.maxOccupants > 0)
            {
#if UNITY_EDITOR
                UnityEditor.Handles.color = Color.white;
                UnityEditor.Handles.Label(
                    p.position + Vector2.up * 0.6f,
                    $"{p.followers.Count}/{p.maxOccupants}");
#endif
            }

            // 跟随者连线
            for (int f = 0; f < p.followers.Count; f++)
            {
                var a = p.followers[f];
                if (a == null) continue;
                Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.25f);
                Gizmos.DrawLine(p.position, a.transform.position);
            }
        }
    }
#endif

    // ==================== 上报数据结构 ====================
    public class PointerReport
    {
        public bool    active;
        public bool    held;
        public Vector2 world;

        public bool    hasStillPoint;
        public Vector2 stillWorld;
        public float   stillTimer;
        public bool    poiCreated;
        public int     poiId;

        public void SetStillPoint(Vector2 p)
        {
            hasStillPoint = true;
            stillWorld = p;
        }

        public void Reset()
        {
            hasStillPoint = false;
            stillTimer = 0f;
            poiCreated = false;
            poiId = 0;
        }
    }
}