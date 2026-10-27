using UnityEngine;

/// <summary>
/// 触摸 / 鼠标输入：掠过即惊，不需要按键。
/// 内层半径精确命中；外层半径制造范围惊吓（"掠过惊飞一片"）。
/// </summary>
public class Game_FlockGestureInput : MonoBehaviour, IConfigurable
{
    // ==================== 相机 ====================
    [Header("相机")]
    public Camera cam;

    [Header("射线命中层（留 Nothing 则整屏都可交互）")]
    public LayerMask planeMask;

    [Tooltip("玩法平面的 z。正交相机下只影响返回值的 z")]
    public float planeZ = 10f;

    // ==================== 触发阈值 ====================
    [Header("触发阈值")]
    [Tooltip("指针移动超过这个世界距离才重新触发")]
    public float moveThreshold = 0.15f;

    [Tooltip("两帧间移动超过这个距离就视为【跳变】：只惊扰终点附近，不做沿途线段扫掠")]
    public float maxSweepLength = 4f;

    [Tooltip("范围惊吓脉冲的最小间隔（秒）")]
    public float panicInterval = 0.1f;

    // ==================== 内层 ====================
    [Header("内层：精确命中")]
    public float sweepRadius = 1.6f;

    // ==================== 外层 ====================
    [Header("外层：范围惊吓（决定'掠过惊飞多宽一片'）")]
    public float panicRadius = 4f;

    // ==================== 点击 ====================
    [Header("点击（触摸屏按下时）")]
    public float tapRadius = 2.5f;
    public float tapDashDuration = 5f;
    
    [Header("指针范围")]
    [Header("勾上后，指针离开游戏视野时不触发惊扰")]
    public bool requirePointerInside = true;

    [Header("外部输入")]
    [Header("勾上后不再自己读鼠标/触摸，改由 Game_TuioPointerTrail 逐触点调 SweepExternal")]
    public bool useExternalInput = false;

    // ==================== 调试 ====================
    [Header("调试")]
    public bool showGizmos = true;
    public bool logDebug = false;

    // ==================== 运行时 ====================
    private Vector2 _lastWorld;
    private bool    _hasLast;
    private float   _panicTimer;

    // ==================== 生命周期 ====================
    private void Reset()
    {
        cam = Camera.main;
    }

    private void Awake()
    {
        if (cam == null) cam = Camera.main;

        ApplyConfig(Game_Config.Instance);
    }

    public void ApplyConfig(Game_Config cfg)
    {
        if (cfg == null) return;

        const string s = "GestureInput";
        
        moveThreshold  = Mathf.Clamp(cfg.GetFloat(s, "moveThreshold",  moveThreshold), 0.1f, 16f);
        maxSweepLength = Mathf.Max(0.5f, cfg.GetFloat(s, "maxSweepLength", maxSweepLength));
        sweepRadius = Mathf.Clamp(cfg.GetFloat(s, "sweepRadius", sweepRadius), 0.1f, 16f);
        panicRadius = Mathf.Clamp(cfg.GetFloat(s, "sweepRadius", sweepRadius), sweepRadius, 16f);
    }

    private void Update()
    {
        _panicTimer -= Time.deltaTime;

        // 外部驱动模式：由 Game_TuioPointerTrail 逐触点调用 SweepExternal
        if (useExternalInput) return;

        bool handledByTouch = false;

        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch t = Input.GetTouch(i);
            if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) continue;

            if (requirePointerInside && !IsPointerInside(t.position)) continue;

            handledByTouch = true;

            if (t.phase == TouchPhase.Began && TryGetWorld(t.position, out Vector2 down))
                StartleAll(down);

            ProcessPointer(t.position);
        }

        if (!handledByTouch)
        {
            if (requirePointerInside && !IsPointerInside(Input.mousePosition))
            {
                _hasLast = false;        // ← 清状态，移回时要重新记录起点
                return;
            }

            ProcessPointer(Input.mousePosition);
        }
    }

    private static bool IsPointerInside(Vector2 screenPos)
    {
        return screenPos.x >= 0f && screenPos.x < Screen.width &&
               screenPos.y >= 0f && screenPos.y < Screen.height;
    }

    // ==================== 核心 ====================
    private void ProcessPointer(Vector2 screenPos)
    {
        if (!TryGetWorld(screenPos, out Vector2 world)) return;

        if (!_hasLast)
        {
            _lastWorld = world;
            _hasLast = true;
            return;                                 // 第一帧只记录，不触发
        }

        Vector2 from = _lastWorld;
        float dist = Vector2.Distance(from, world);

        if (dist < moveThreshold) return;

        _lastWorld = world;

        // ★ 跳变检测。雷达「鼠标移动」模式会让光标在多个探测点之间交替闪，
        //   此时 from→world 是一条横跨屏幕的假线段。
        //   如果照常做线段扫掠，会把沿途所有蝴蝶都惊飞（而且每秒几十次）。
        //   跳变时只惊扰终点附近，退化为点检测。
        bool isJump = dist > maxSweepLength;

        Vector2 sweepFrom = isJump ? world : from;

        if (logDebug)
            Debug.Log($"[Gesture] from={sweepFrom} to={world} jump={isJump}");

        SweepAt(sweepFrom, world);                  // 内层：扫掠（跳变时是零长度线段 = 点检测）

        if (_panicTimer <= 0f)                      // 外层：范围惊吓（带节流）
        {
            _panicTimer = panicInterval;
            PanicAlong(sweepFrom, world);
        }
    }

    // ==================== 外部驱动 ====================
    /// <summary>
    /// TUIO 多点场景的入口。Game_TuioPointerTrail 每个触点每帧调用一次，
    /// 传入该触点自己的「上一帧 / 本帧」世界坐标。
    /// 内部复用和鼠标模式完全相同的跳变判定、扫掠、范围惊吓逻辑。
    /// </summary>
    public void SweepExternal(Vector2 from, Vector2 to)
    {
        float dist = Vector2.Distance(from, to);

        if (dist < moveThreshold) return;

        // 跳变时退化为点检测（和鼠标模式的处理一致）
        bool isJump = dist > maxSweepLength;

        Vector2 sweepFrom = isJump ? to : from;

        if (logDebug)
            Debug.Log($"[Gesture] external from={sweepFrom} to={to} jump={isJump}");

        SweepAt(sweepFrom, to);

        if (_panicTimer <= 0f)
        {
            _panicTimer = panicInterval;
            PanicAlong(sweepFrom, to);
        }
    }

    // ==================== 外层 ====================
    private void PanicAlong(Vector2 from, Vector2 to)
    {
        var agents = Game_FlockManager.Instance.Agents;
        if (agents.Count == 0) return;

        Vector2 mid  = (from + to) * 0.5f;
        float   half = (to - from).magnitude * 0.5f;
        float   r    = panicRadius + half;          // 用圆覆盖整条线段
        float   r2   = r * r;

        var touchedCount = 0;
        
        for (int i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (!a) continue;

            Vector2 p = a.transform.position;
            if ((p - mid).sqrMagnitude > r2) continue;   // 粗筛

            touchedCount++;
            
            float d = Vector2.Distance(p, mid);
            a.Panic(mid, Mathf.Clamp01(1f - d / r));     // 越近越强
        }
        
        if (touchedCount >= 10)
            Game_GlitchTrigger.Burst(1.2f);
    }

    // ==================== 内层 ====================
    private void SweepAt(Vector2 from, Vector2 to)
    {
        var agents = Game_FlockManager.Instance.Agents;
        if (agents.Count == 0) return;

        Vector2 swipeDir   = (to - from).normalized;
        float   swipeSpeed = Mathf.Clamp01((to - from).magnitude * 0.5f);

        for (int i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a == null) continue;

            Vector2 p = a.transform.position;
            if (PointSegmentDistance(p, from, to) <= sweepRadius)
                a.SweepAway(ClosestOnSegment(p, from, to), swipeDir, swipeSpeed);
        }
    }

    // ==================== 点击惊散 ====================
    public void StartleAll(Vector2 world)
    {
        var agents = Game_FlockManager.Instance.Agents;

        for (int i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (!a) continue;
            if (Vector2.Distance(a.transform.position, world) <= tapRadius)
                a.Startle(world, tapDashDuration, 1f);   // 第三参数是 falloff（1 = 满强度）
        }
    }

    // ==================== 几何 ====================
    private static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float denom = ab.sqrMagnitude;
        if (denom < 1e-6f) return a;

        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / denom);
        return a + ab * t;
    }

    private static float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        => Vector2.Distance(p, ClosestOnSegment(p, a, b));

    // ==================== 屏幕 → 世界 ====================
    private bool TryGetWorld(Vector2 screenPos, out Vector2 world)
    {
        // 只在真的设了层时才做射线查询
        if (planeMask.value != 0)
        {
            Ray ray = cam.ScreenPointToRay(screenPos);
            if (Physics.Raycast(ray, out RaycastHit hit, 500f, planeMask))
            {
                world = hit.point;
                return true;
            }
        }

        // 兜底：正交相机直接换算
        float dist = Mathf.Max(planeZ, cam.nearClipPlane + 0.01f);
        world = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, dist));
        return true;
    }

    // ==================== 调试 ====================
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!showGizmos || !Application.isPlaying || !_hasLast) return;

        Gizmos.color = new Color(1f, 0.4f, 0.3f, 0.9f);     // 内层
        Gizmos.DrawWireSphere(_lastWorld, sweepRadius);

        Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.35f);    // 外层
        Gizmos.DrawWireSphere(_lastWorld, panicRadius);
    }
#endif
}