using UnityEngine;

/// <summary>
/// 一比一复刻目标工程 CreateEffect 的指针粒子生成算法。
///
/// 用途：先跑通"目标游戏的效果"，再在此基础上优化。
///
/// ⚠️ 和 Game_PointerTrail 冲突：两者都会生成粒子，不能同时启用。
///    要用这个组件，请先把场景里的 Game_PointerTrail 禁用。
///
/// ──────────── 原工程的完整逻辑 ────────────
/// Update → CreateFlowerEffect
///   触摸模式（Input.touchCount > 0）：
///     对每个 phase 为 Moved 或 Began 的触点 → Raycast → Create
///   鼠标模式：
///     每帧一次 → Raycast(Input.mousePosition) → Create
///
/// Create(ray):
///   if raycast 命中：
///     point = hit.point
///     if Distance(point, mouseTempPos) > mouseDistance:      // 原工程配置 = 0
///       pos = point + (Random(minOff,maxOff), 0, Random(minOff,maxOff))
///       spawn effects[Random.Range(0, effects.Length)] at pos，旋转 (-90,0,0)
///       6 秒后回收
///     mouseTempPos = point                                    // ← 在 if 外面
///
/// ──────────── 原工程【没有】的东西 ────────────
///   无插值、无 leftover 累积、无每指独立追踪、无节流、无平滑、无缩放随机
///
/// ──────────── 原工程的一个 bung（不修复，只记录）────────────
///   Physics.Raycast(ray, out hit, (1 &lt;&lt; NameToLayer("Plane")).value)
///   只传了 3 个参数，第三个被当成 maxDistance（= 1024），
///   layerMask 用的是默认值 → 实际没有层过滤。
///   本组件提供 useRaycast 开关，默认用数学平面（零配置、无副作用）。
/// </summary>
public class Game_PointerTrailFaithful : MonoBehaviour
{
    // ==================== 引用 ====================
    [Header("引用")]
    public Camera cam;

    [Header("对象池（留空自动找 Game_FlockManager.pool）")]
    public Game_ObjectPool pool;

    // ==================== 粒子预制体 ====================
    [Header("粒子预制体数组")]
    [Tooltip("和原工程一样：重复放同一个 prefab 可以当权重用（Random.Range 均匀抽取）")]
    public GameObject[] effects;

    [Header("生成朝向")]
    [Tooltip("原工程硬编码 Quaternion.Euler(-90, 0, 0)")]
    public Vector3 spawnEuler = new Vector3(-90f, 0f, 0f);

    // ==================== 坐标 ====================
    [Header("坐标换算")]
    [Tooltip("勾上 = 用 Physics.Raycast 打 planeMask 层（原工程做法，需要 Plane 层碰撞体）\n" +
             "关掉 = 用相机数学换算（零配置，推荐）")]
    public bool useRaycast = false;

    [Tooltip("useRaycast 时使用的层")]
    public LayerMask planeMask;

    [Tooltip("useRaycast = false 时使用的平面 z，以及射线求交的平面")]
    public float planeZ = 0f;

    // ==================== 参数（对应原工程 configFile.ini）====================
    [Header("参数")]
    [Tooltip("原工程「移动检测距离」。0 = 任何位移都生成")]
    public float mouseDistance = 0f;

    [Tooltip("原工程「粒子偏移最小值」")]
    public float posMinOffset = 0f;

    [Tooltip("原工程「粒子偏移最大值」")]
    public float posMaxOffset = 0f;

    [Tooltip("原工程硬编码 6 秒")]
    public float collectDelay = 6f;

    [Header("静止兜底（原工程没有，默认关闭）")]
    [Tooltip("即使 Distance = 0 也每隔这么多秒生成一个。0 = 关闭（纯复刻）\n" +
             "用途：万一雷达某一帧完全不动，也能保证『静止时仍在生成』")]
    public float stationarySpawnInterval = 0f;

    // ==================== 其它 ====================
    [Header("其它")]
    [Tooltip("原工程在 Start 里 Cursor.visible = false。本项目由 Game_Config 管光标，默认关掉")]
    public bool hideCursor = false;

    [Header("调试")]
    public bool logDebug = false;

    [Tooltip("每 30 帧打印一次 Distance 和是否生成，用来确认判据是否通过")]
    public bool logDetails = false;

    // ==================== 运行时 ====================
    private Vector3 _mouseTempPos;
    private int _spawnCount;
    private float _statTimer;

    private float _stationaryTimer;
    private float _lastDist = -1f;
    private bool  _lastSpawned;

    private void Start()
    {
        if (cam == null) cam = Camera.main;

        if (hideCursor) Cursor.visible = false;

        if (pool == null)
        {
            var mgr = Game_FlockManager.Instance;

            if (mgr != null && mgr.pool != null)
                pool = mgr.pool;
            else
                pool = FindObjectOfType<Game_ObjectPool>();
        }

        if (pool == null)
            Debug.LogWarning("[TrailFaithful] 找不到 Game_ObjectPool，会退化为 Instantiate/Destroy");

        if (effects == null || effects.Length == 0)
            Debug.LogWarning("[TrailFaithful] effects 数组为空，不会生成任何粒子");
    }

    private void Update()
    {
        CreateFlowerEffect();

        // ---- 统计：鼠标位置变化次数 vs 实际生成数 ----
        // 用途：确认"每秒只生成 10 个"是不是因为 Input.mousePosition 被整数像素量化
        // （Windows 上鼠标坐标是整数像素，雷达抖动不足 1 像素时位置不变 → Distance = 0 → 不生成）
        Vector3 mp = Input.mousePosition;

        if (mp.x != _lastMouse.x || mp.y != _lastMouse.y)
        {
            _posChangedCount++;
            _lastMouse = mp;
        }

        if (logDebug)
        {
            _statTimer += Time.unscaledDeltaTime;

            if (_statTimer >= 1f)
            {
                _statTimer = 0f;

                Debug.Log($"[TrailFaithful] 鼠标位置变化 {_posChangedCount} 次，" +
                          $"生成 {_spawnCount} 个粒子");

                _spawnCount      = 0;
                _posChangedCount = 0;
            }
        }
    }

    private int     _posChangedCount;
    private Vector3 _lastMouse;
    
    // ==================== 复刻：CreateFlowerEffect ====================
    private void CreateFlowerEffect()
    {
        if (Input.touchCount > 0)
        {
            int touchCount = Input.touchCount;

            for (int i = 0; i < touchCount; i++)
            {
                // 原工程只处理 Moved / Began
                TouchPhase phase = Input.GetTouch(i).phase;

                if (phase == TouchPhase.Moved || phase == TouchPhase.Began)
                {
                    if (TryGetHitPoint(Input.GetTouch(i).position, out Vector3 p))
                        Create(p);
                }
            }
        }
        else
        {
            if (TryGetHitPoint(Input.mousePosition, out Vector3 p))
                Create(p);
        }
    }

    // ==================== 复刻：Create ====================
    private void Create(Vector3 point)
    {
        float dist = Vector3.Distance(point, _mouseTempPos);

        bool spawned = false;

        // 原工程：Distance > mouseDistance 才生成
        if (dist > mouseDistance)
        {
            SpawnAt(point);
            _stationaryTimer = 0f;
            spawned = true;
        }
        else if (stationarySpawnInterval > 0f)
        {
            // 非原工程逻辑：静止兜底
            _stationaryTimer += Time.unscaledDeltaTime;

            if (_stationaryTimer >= stationarySpawnInterval)
            {
                _stationaryTimer = 0f;
                SpawnAt(point);
                spawned = true;
            }
        }

        // ★ 原工程：在 if 外面更新
        _mouseTempPos = point;

        _lastDist    = dist;
        _lastSpawned = spawned;

        if (logDetails && Time.frameCount % 30 == 0)
        {
            Debug.Log($"[TrailFaithful] dist={dist:F6} mouseDistance={mouseDistance} " +
                      $"spawned={spawned} point={point}");
        }
    }

    /// <summary>原工程 Create 里 if 内部的全部内容</summary>
    private void SpawnAt(Vector3 point)
    {
        // ★ 注意：原工程这里用的是 hit.point（不含偏移）算出来的 pos
        float posXOffset = Random.Range(posMinOffset, posMaxOffset);
        float posZOffset = Random.Range(posMinOffset, posMaxOffset);

        Vector3 pos = new Vector3(point.x + posXOffset, point.y, point.z + posZOffset);

        Spawn(pos);
    }

    private void Spawn(Vector3 pos)
    {
        if (effects == null || effects.Length == 0) return;

        int effectIndex = Random.Range(0, effects.Length);
        GameObject prefab = effects[effectIndex];

        if (prefab == null) return;

        Quaternion rot = Quaternion.Euler(spawnEuler);

        GameObject go;

        if (pool != null)
        {
            go = pool.Acquire(prefab, pos, rot);

            if (go != null && collectDelay > 0f)
                pool.Release(go, collectDelay);
        }
        else
        {
            go = Instantiate(prefab, pos, rot);

            if (collectDelay > 0f)
                Destroy(go, collectDelay);
        }

        _spawnCount++;

        if (logDebug && _spawnCount <= 3)
            Debug.Log($"[TrailFaithful] spawn #{_spawnCount} prefab={prefab.name} pos={pos}");
    }

    // ==================== 坐标换算 ====================
    /// <summary>
    /// 原工程是 Physics.Raycast 打 Plane 层。
    /// 这里提供两种实现，默认用数学换算（不需要 Plane 层碰撞体）。
    /// </summary>
    private bool TryGetHitPoint(Vector2 screenPos, out Vector3 point)
    {
        point = Vector3.zero;

        if (!cam) return false;

        // ---- 复刻模式：射线打碰撞体 ----
        if (useRaycast && planeMask.value != 0)
        {
            Ray ray = cam.ScreenPointToRay(screenPos);

            if (Physics.Raycast(ray, out RaycastHit hit, 500f, planeMask))
            {
                point = hit.point;
                return true;
            }

            return false;
        }

        // ---- 正交相机：直接换算，无除法、无 t 的符号问题 ----
        if (cam.orthographic)
        {
            float dist = planeZ - cam.transform.position.z;

            Vector3 p = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, dist));

            p.z = planeZ;
            point = p;

            return true;
        }

        // ---- 透视相机：才用射线求交 ----
        Ray r = cam.ScreenPointToRay(screenPos);

        if (Mathf.Abs(r.direction.z) < 1e-6f) return false;

        float t = (planeZ - r.origin.z) / r.direction.z;

        if (t <= 0f) return false;

        point = r.origin + r.direction * t;
        point.z = planeZ;

        return true;
    }
}
