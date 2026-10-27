using UnityEngine;

/// <summary>
/// 集群子对象（单只蝴蝶）：负责朝向、速度、游荡目标、逃跑状态。
/// 不处理动画（见 Game_FlockAnimator）、不处理生物群中心（见 Game_FlockAnchor）。
/// </summary>
public class Game_FlockChild_Test : MonoBehaviour, IGamePoolable
{
    public enum FlyState { Cruise, Glide, Dash }

    // ==================== 飞行手感 ====================
    [Header("飞行状态")]
    public float stateSwitchInterval = 3f;

    [Header("速度区间（x=min, y=max）")]
    public Vector2 speedSlow   = new Vector2(0.2f, 0.5f);
    public Vector2 speedMedium = new Vector2(0.6f, 1.2f);
    public Vector2 speedFast   = new Vector2(2.0f, 3.5f);

    [Header("转向区间（度/秒 的比例系数）")]
    public Vector2 dampGlide  = new Vector2(0.4f, 1.0f);
    public Vector2 dampCruise = new Vector2(1.2f, 2.5f);
    public Vector2 dampDash   = new Vector2(3.0f, 5.5f);

    [Header("游荡盒（相对锚点）")]
    public float boxX = 10f;
    public float boxY = 8f;
    public float arriveDistance = 1f;
    
    [Header("兴趣点")]
    [Header("被兴趣点吸引时的到达容差。应远小于 boxX×poiBoxScale，否则一进圈就频繁换目标")]
    public float poiArriveDistance = 0.2f;

    [Header("被兴趣点吸引时的取样盒倍率（覆盖 Game_FlockInterest 的 poiBoxScale，<0 表示用对方的）")]
    public float poiBoxScaleOverride = -1f;
    
    [Header("限速保护")]
    public float maxThinkDelta = 0.1f;

    // ==================== 逃跑（惊吓） ====================
    [Header("逃跑：目标点距离")]
    public float fleeDistance = 5f;

    [Header("逃跑：受惊瞬间的转身倍数（小 = 先慢慢转头再加速）")]
    public float fleeStartDampingMul = 0.4f;

    [Header("逃跑：转身倍数回复速度（约 2.5 秒恢复正常）")]
    public float fleeRecoverSpeed = 0.3f;

    [Header("逃跑：速度倍率范围（保留个体差异）")]
    public Vector2 fleeSpeedMulRange = new Vector2(0.9f, 1.5f);

    [Header("逃跑：最低逃跑速度（保证慢蝴蝶也能跑起来）")]
    public float fleeMinSpeed = 3.5f;

    [Header("逃跑：加速率（远大于正常的 2.5，产生爆发感）")]
    public float fleeAccel = 9f;

    [Header("逃跑：接近减速比例（1 = 不减速）")]
    public float fleeApproachFloor = 1f;

    // ==================== 硬推（可选） ====================
    [Header("硬推总位移（世界单位；0 = 纯惊飞。相机 size=5 时 1 ≈ 屏幕高的 10%）")]
    public float fleePushDistance = 0f;

    [Header("硬推衰减速度（越大推得越快越干脆，不影响总位移）")]
    public float fleePushDamping = 4f;

    // ==================== 视锥剔除 ====================
    [Header("视锥剔除")]
    public float cullPadding = 2f;

    [Header("回收半径（超出则瞬移回锚点附近；应为可视范围的 1.5~2 倍）")]
    public float recallRadius = 24f;
    
    // ==================== 体积 ====================
    [Header("体积")]
    [Tooltip("体积系数。1 = 原始大小。假深度会在应用这个系数之后再次缩放")]
    [Min(0.01f)] public float sizeScale = 1f;

    [Tooltip("每只蝴蝶的体积随机浮动（±比例）。0 = 不随机，让个体大小有差异")]
    [Range(0f, 0.5f)] public float sizeJitter = 0f;

    // 每只蝴蝶固定的随机倍率（生成时抽一次）
    private float _sizeMul = 1f;
    
    // ==================== 假深度 ====================
    [Header("假深度")]
    public bool  useFakeDepth = true;
    [Header("勾上：worldBottom/Top 自动跟随相机视野高度")]
    public bool autoDepthRange = true;
    public float worldBottom = -8f;
    public float worldTop    =  8f;
    public float nearScale   = 1.2f;
    public float farScale    = 0.7f;
    
    // ==================== 调试 ====================
    [Header("调试")]
    [Header("显示取样盒")]
    public bool _showPoiDebug = false;
    
    [Header("勾上后，被兴趣点吸引的蝴蝶会画青色线指向 POI 中心")]
    public bool showPoiDebug = false;
    
    // 诊断用
    [HideInInspector] public int dbgRetargetCount;
    private float dbgLastRetargetTime;
    [HideInInspector] public float dbgMinInterval = 999f;

    // ==================== 外部只读（供动画层） ====================
    public FlyState State        => _state;
    public float    CurrentSpeed => _speed;
    public float    FlapBoost    => _flapBoost;
    public bool     IsResting    => false;      // 尚未实现栖枝系统
    public bool     IsEscaping   => _fleeTimer > 0f;

    // ==================== 运行时状态 ====================
    private Game_FlockManager _manager;
    private Game_FlockAnchor  _anchor;
    private SpriteRenderer    _sr;    // 兴趣点
    private Game_FlockInterest.Point _poi;
    private Game_FlockPlay _play;
    
    private bool _forceRetarget;      // POI 过期时被标记，下一帧忽略距离强制重选

    private FlyState _state = FlyState.Cruise;

    private Vector2 _target;
    private float   _speed;
    private float   _targetSpeed;
    private float   _damping;
    private float   _stateTimer;
    private float   _lastThinkTime;
    
    // 嬉戏移动模式
    private bool  _playMode;
    private float _playOrbit;
    private float _playRadius = 1.6f;
    private int   _playSide  = 1;
    
    // 逃跑
    private float   _fleeTimer;
    private float   _fleeRatio = 1f;
    private Vector2 _fleeDir;
    private float   _fleeSpeedMul = 1f;
    private float   _dashTimer;
    private float   _flapBoost = 1f;
    
    // 栖枝轮换
    private float _restLeaveTimer;      // 剩余停留时间
    private bool  _restLeaveDecided;    // 本帧已决定离开，防止重复处理

    // 硬推
    private Vector2 _kbVel;

    // 回收
    private float _recallCheckTimer;
    
    /// <summary>供 Game_FlockPlay 每帧同步嬉戏状态</summary>
    public void SetPlayActive(bool active, float orbit, float radius, int side)
    {
        _playMode   = active;
        _playOrbit  = orbit;
        _playRadius = radius;
        _playSide   = side;
    }
    
    /// <summary>扇翅频率倍率。栖枝点上 &lt; 1（慢扇）</summary>
    public float RestBeat =>
        (_poi != null && _poi.kind == Game_FlockInterest.PointKind.RestSpot)
            ? _poi.animBeatMul
            : 1f;

    /// <summary>当前是否停在栖枝点上</summary>
    public bool OnRestSpot =>
        _poi != null && _poi.kind == Game_FlockInterest.PointKind.RestSpot;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        if (_manager == null) _manager = GetComponentInParent<Game_FlockManager>();
        if (_anchor  == null) _anchor  = GetComponentInParent<Game_FlockAnchor>();
        if (_play    == null) _play    = GetComponent<Game_FlockPlay>();   // ← 新增
    }
    
    private void Start()
    {
        if (autoDepthRange)
        {
            var cam = Camera.main;
            if (cam != null && cam.orthographic)
            {
                float half = cam.orthographicSize;
                worldBottom = cam.transform.position.y - half;
                worldTop    = cam.transform.position.y + half;
            }
        }
    }

    /// <summary>由 Manager 在取出后调用</summary>
    public void Init(Game_FlockManager manager, Game_FlockAnchor anchor, int index)
    {
        _manager = manager;
        _anchor  = anchor;
        if (_sr == null) _sr = GetComponentInChildren<SpriteRenderer>();

        ResetMotion();
        ReshuffleState();

        _speed = _targetSpeed;      // ★ 直接以目标速度出发，避免慢启动
    }

    public void OnSpawned()
    {
        ResetMotion();

        // 每只蝴蝶的逃跑速度倍率在这里抽签（池化复用时也会重抽）
        _fleeSpeedMul = Random.Range(fleeSpeedMulRange.x, fleeSpeedMulRange.y);
        
        // ★ 体积随机（每只固定，不复位）
        _sizeMul = sizeJitter > 0f
            ? 1f + Random.Range(-sizeJitter, sizeJitter)
            : 1f;
    }

    public void OnDespawned()
    {
        //蝴蝶被回收时如果还挂在某个 POI 的跟随列表上，那个引用会失效
        ClearInterest();          // ★ 防止 POI 的 followers 里留下已回收的对象
        _kbVel = Vector2.zero;
    }

    /// <summary>复位所有运动状态。放在这个位置，避免复用池对象时残留上一轮状态</summary>
    private void ResetMotion()
    {
        _speed         = 0f;
        _targetSpeed   = 0f;
        _target        = transform.position;
        _kbVel = Vector2.zero;
        _dashTimer     = 0f;
        _fleeTimer     = 0f;
        _fleeRatio     = 1f;
        _fleeDir       = Vector2.zero;
        _flapBoost     = 1f;
        _lastThinkTime = Time.time;
        _stateTimer    = stateSwitchInterval * Random.Range(0f, 1f);
        _recallCheckTimer = 0f;
        _restLeaveTimer   = 0f;
        _restLeaveDecided = false;
        _poi = null;
        _forceRetarget = false;
        
        // 嬉戏状态由 Game_FlockPlay.OnSpawned / OnDespawned 管理，这里不用管
    }

    // ==================== 每帧入口 ====================
    /// <summary>屏幕内：完整逻辑</summary>
    public void Tick(bool shouldThink)
    {
        // ★ 嬉戏：接管移动，但仍然要更新缩放
        if (_playMode)
        {
            MovePlay(Time.deltaTime);
            ApplyScale();
            return;
        }

        // POI 过期 → 忽略距离强制重选目标
        if (_forceRetarget)
        {
            _forceRetarget = false;
            PickNewTarget(true);
        }

        if (shouldThink) Think();
        Move(Time.deltaTime);

        ApplyScale();          // ★ 无条件调用（原来只有 useFakeDepth 才调）
    }
    /// <summary>屏幕外：继续飞 + 朝目标转向（否则会一直朝生成方向飞走）</summary>
    public void TickCulled(float dt)
    {
        Move(dt);
        _lastThinkTime = Time.time;

        Vector2 toTarget = _target - (Vector2)transform.position;
        if (toTarget.sqrMagnitude < 0.01f) return;

        Vector2 dir = toTarget.normalized;
        float targetAngle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;

        // 屏幕外转速减半，省性能但足够转向
        float turn = _damping * 0.5f * 90f * dt;
        float next = Mathf.MoveTowardsAngle(transform.eulerAngles.z, targetAngle, turn);
        transform.rotation = Quaternion.Euler(0f, 0f, next);
    }

    // ==================== 决策（隔帧） ====================
    private void Think()
    {
        if (_manager && _manager.showThinkDebug)
            Debug.DrawRay(transform.position, Vector3.up * 0.5f, Color.red, 0f);

        float now = Time.time;
        float dt = Mathf.Min(now - _lastThinkTime, maxThinkDelta);
        _lastThinkTime = now;

        // 逃跑计时与恢复
        if (_fleeTimer > 0f)
        {
            _fleeTimer -= dt;
            _fleeRatio = Mathf.MoveTowards(_fleeRatio, 1f, fleeRecoverSpeed * dt);
        }
        else if (_fleeRatio < 1f)
        {
            _fleeRatio = 1f;                    // 逃跑结束的收尾
        }

        // 惊吓期间锁定 Dash，不重掷状态
        if (_dashTimer > 0f)
        {
            _dashTimer -= dt;
        }
        else
        {
            _stateTimer -= dt;
            if (_stateTimer <= 0f) ReshuffleState();
        }

        // 到达判定
        // 到达判定
        var toTarget = _target - (Vector2)transform.position;

        var arrive = IsEscaping
            ? arriveDistance * 2.5f
            : (InInterest ? poiArriveDistance : arriveDistance);

        if (toTarget.sqrMagnitude < arrive * arrive)
        {
            PickNewTarget();
            return;
        }

        // 转向（受惊初期转身变慢 → 像"先懵一下再逃"）
        var dir = toTarget.normalized;
        var targetAngle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
        var turn = _damping * (IsEscaping ? _fleeRatio : 1f) * 90f * dt;
        var next = Mathf.MoveTowardsAngle(transform.eulerAngles.z, targetAngle, turn);
        transform.rotation = Quaternion.Euler(0f, 0f, next);
    }

    // ==================== 移动（每帧） ====================
    private void Move(float dt)
    {
        // 逃跑时加速率大幅提高 → 爆发感
        var accel = IsEscaping ? fleeAccel : 2.5f;
        _speed = Mathf.Lerp(_speed, _targetSpeed, dt * accel);

        Vector2 toTarget = _target - (Vector2)transform.position;

        // 逃跑时不做接近减速（否则逃跑中会突然刹车）
        var floor = IsEscaping ? fleeApproachFloor : 0.25f;
        var k = Mathf.Clamp01(toTarget.magnitude / (arriveDistance * 3f));
        var moveSpeed = Mathf.Lerp(_speed * floor, _speed, k);

        transform.position += transform.up * (moveSpeed * dt);

        // 可选硬推：总位移 = fleePushDistance
        if (fleePushDistance > 0f && _kbVel.sqrMagnitude > 1e-4f)
        {
            transform.position += (Vector3)(_kbVel * dt);
            _kbVel = Vector2.Lerp(_kbVel, Vector2.zero, dt * fleePushDamping);
            if (_kbVel.sqrMagnitude < 1e-3f) _kbVel = Vector2.zero;
        }

        _flapBoost = Mathf.MoveTowards(_flapBoost, 1f, dt * 1.5f);
    }
    
    /// <summary>
    /// 嬉戏移动：绕同伴公转。
    /// 目标点 = 同伴位置 + 旋转到当前相位方向的偏移，
    /// 因为偏移量恒等于公转半径，蝴蝶会稳定绕圈而不是螺旋收进去。
    /// </summary>
    private void MovePlay(float dt)
    {
        // ★ 兜底：一旦处于逃跑状态，立刻退出嬉戏移动
        if (IsEscaping)
        {
            _playMode = false;
            if (_play) _play.EndPlay(true);
            return;
        }

        if (!_play || !_play.Partner) { _playMode = false; return; }

        Vector2 me   = transform.position;
        Vector2 them = _play.Partner.transform.position;

        // 圆心到我的方向
        Vector2 dir = me - them;
        if (dir.sqrMagnitude < 1e-6f) dir = Vector2.up;
        dir.Normalize();

        // 旋转 90°，得到切线方向（决定绕行方向）
        Vector2 tangent = new Vector2(-dir.y, dir.x) * _playSide;

        // 目标点：在同伴周围、相位推进后的位置
        float ang = _playSide * _playOrbit * Mathf.PI * 2f;
        float cos = Mathf.Cos(ang);
        float sin = Mathf.Sin(ang);

        Vector2 offset = new Vector2(
            dir.x * cos - dir.y * sin,
            dir.x * sin + dir.y * cos) * _playRadius;

        Vector2 target = them + offset;

        // 转向
        Vector2 toTarget = target - me;
        if (toTarget.sqrMagnitude > 1e-6f)
        {
            Vector2 toDir = toTarget.normalized;
            float targetAngle = Mathf.Atan2(toDir.y, toDir.x) * Mathf.Rad2Deg - 90f;

            // 用独立的转速，不受常规 damping 波动影响
            float baseDamp = Mathf.Max(_damping, 2f);
            float turn = baseDamp * _play.playTurnMul * 90f * dt;

            float next = Mathf.MoveTowardsAngle(transform.eulerAngles.z, targetAngle, turn);
            transform.rotation = Quaternion.Euler(0f, 0f, next);
        }

        // 位移：用独立速度，且不做接近减速（那正是之前变慢的原因）
        _speed = Mathf.Lerp(_speed, _play.playSpeed, dt * 3f);
        transform.position += transform.up * (_speed * dt);

        _flapBoost = Mathf.MoveTowards(_flapBoost, 1f, dt * 1.5f);
    }

    // ==================== 状态 ====================
    private void ReshuffleState()
    {
        float r = Random.value;
        if      (r < 0.55f) _state = FlyState.Cruise;
        else if (r < 0.80f) _state = FlyState.Glide;
        else                _state = FlyState.Dash;

        switch (_state)
        {
            case FlyState.Glide:
                _targetSpeed = Random.Range(speedSlow.x, speedSlow.y);
                _damping     = Random.Range(dampGlide.x, dampGlide.y);
                break;
            case FlyState.Dash:
                _targetSpeed = Random.Range(speedFast.x, speedFast.y);
                _damping     = Random.Range(dampDash.x, dampDash.y);
                break;
            default:
                _targetSpeed = Random.Range(speedMedium.x, speedMedium.y);
                _damping     = Random.Range(dampCruise.x, dampCruise.y);
                break;
        }

        // 逃跑中的速度不被状态重掷覆盖
        if (IsEscaping)
            _targetSpeed = Mathf.Max(_targetSpeed, fleeMinSpeed);

        _stateTimer = stateSwitchInterval * Random.Range(0.7f, 1.3f);
        PickNewTarget();
    }

    // ==================== 目标点 ====================
    /// <summary>选目标点。<paramref name="force"/> = true 时忽略最小距离，直接用抽到的点</summary>
    public void PickNewTarget(bool force = false)
    {
        // ---- 诊断 ----
        dbgRetargetCount++;
        if (dbgLastRetargetTime > 0f)
        {
            float iv = Time.time - dbgLastRetargetTime;
            if (iv < dbgMinInterval) dbgMinInterval = iv;
        }
        dbgLastRetargetTime = Time.time;
        // ---- 诊断结束 ----
        
        // ---- 逃跑分支不变 ----
        if (IsEscaping)
        {
            Vector2 fd = _fleeDir.sqrMagnitude > 1e-4f
                ? _fleeDir
                : Random.insideUnitCircle.normalized;

            Vector2 lateral = new Vector2(-fd.y, fd.x) * Random.Range(-0.35f, 0.35f);

            _target = (Vector2)transform.position
                      + (fd + lateral).normalized * fleeDistance;
            return;
        }

        // ---- 取点中心 + 取样盒尺寸 ----
        float bx = boxX;
        float by = boxY;

        // ---- 取点中心：优先用范围内的兴趣点 ----
        var interest = Game_FlockInterest.Instance;

        Game_FlockInterest.Point poi = null;
        if (interest != null)
        {
            poi = _poi != null
                ? interest.FindNearestForRetain(transform.position, _poi)      // 已绑定 → 迟滞半径
                : interest.FindNearestAvailable(transform.position, this);     // 未绑定 → 找有空位的
        }

        if (poi != null)
        {
            bool justBound = _poi != poi;

            if (justBound)
            {
                ClearInterest();
                _poi = poi;
                poi.followers.Add(this);

                // ★ 进入栖枝点 → 按每个蝴蝶的随机时长开始计时
                if (poi.kind == Game_FlockInterest.PointKind.RestSpot && poi.restDuration > 0f)
                {
                    _restLeaveTimer   = poi.restDuration * Random.Range(0.6f, 1.4f);
                    _restLeaveDecided = false;
                }
                else
                {
                    _restLeaveTimer = 0f;
                }
            }

            // ★ 用这个点自己的游荡半径（栖枝会设得很小 → 悬停）
            float r = poi.wanderRadius > 0f ? poi.wanderRadius : interest.wanderRadius;

            // 圆内面积均匀分布
            Vector2 offset = Random.insideUnitCircle * (r * Mathf.Sqrt(Random.value));

            _target = poi.position + offset;

            if (showPoiDebug)
                Debug.DrawLine(transform.position, poi.position,
                    poi.kind == Game_FlockInterest.PointKind.RestSpot
                        ? Color.cyan : new Color(1f, 0.6f, 0f),
                    0f);

            return;
        }

        // ---- 不在任何兴趣点内：回到锚点体系 ----
        if (_poi != null) ClearInterest();

        Vector2 center = _anchor != null
            ? (Vector2)_anchor.Center
            : (Vector2)transform.position;

        Vector2 current = transform.position;
        Vector2 best = _target;

        float minDist  = Mathf.Max(boxX, boxY) * 0.5f;
        float minDist2 = minDist * minDist;

        for (int i = 0; i < 4; i++)
        {
            Vector2 candidate = center + new Vector2(
                Random.Range(-boxX, boxX),
                Random.Range(-boxY, boxY));

            best = candidate;
            if (force || (candidate - current).sqrMagnitude >= minDist2) break;
        }

        _target = best;
    }
    
    /// <summary>是否正被兴趣点吸引</summary>
    public bool InInterest => _poi != null;

    /// <summary>解除与当前兴趣点的绑定</summary>
    public void ClearInterest()
    {
        if (_poi == null) return;

        _poi.followers.Remove(this);
        _poi = null;
    }
    /// <summary>被兴趣点系统标记，要求强制重选目标</summary>
    public void MarkForceRetarget() => _forceRetarget = true;
    
    // ==================== 体积 ====================

    /// <summary>
    /// 重新抽取本只蝴蝶的体积随机倍率，并立刻应用缩放。
    /// 热重载配置、或运行时改 sizeJitter 后调用。
    /// </summary>
    public void RerollSizeMul()
    {
        _sizeMul = sizeJitter > 0f
            ? 1f + Random.Range(-sizeJitter, sizeJitter)
            : 1f;

        ApplyScale();          // 立刻生效，不用等下一帧
    }

    /// <summary>当前实际生效的体积倍率（含随机与假深度），只读</summary>
    public float CurrentScale => transform.localScale.x;
    
    // ==================== 栖枝轮换 ====================

    /// <summary>推进停留计时。返回 true 表示还没到点，应继续停留</summary>
    public bool TickRestCountdown(float dt, float totalDuration)
    {
        if (_restLeaveDecided) return false;         // 已决定离开
        if (_restLeaveTimer <= 0f) return false;     // 没在计时

        _restLeaveTimer -= dt;
        return _restLeaveTimer > 0f;
    }

    /// <summary>标记"本帧已决定离开"，防止重复处理</summary>
    public void MarkRestLeaveDecided() => _restLeaveDecided = true;

    /// <summary>离开栖枝后加冷却，避免立刻又坐回去</summary>
    public void CooldownRestSpot(float seconds)
    {
        _restLeaveTimer = 0f;
        // 用已有的冷却机制：让它暂时不参与"开始嬉戏"之类的决策
        // 这里直接给它一个新的目标，靠"走远了才算离开"自然错开
    }
    
    // ==================== 受惊入口 ====================
    /// <summary>强惊吓：点击 / 直接撞上</summary>
    public void Startle(Vector2 source, float duration, float falloff = 1f)
    {
        BeginFlee(source, duration);

        _state   = FlyState.Dash;
        _damping = Random.Range(dampDash.x, dampDash.y);

        ApplyPush(source, falloff);
        _flapBoost = 2f;
    }

    /// <summary>范围惊吓：掠过时惊动一圈。falloff 0~1</summary>
    public void Panic(Vector2 center, float falloff)
    {
        falloff = Mathf.Clamp01(falloff);
        BeginFlee(center, Mathf.Lerp(0.8f, 1.6f, falloff));
        ApplyPush(center, falloff);
    }

    /// <summary>划过驱散：最轻微的一档</summary>
    public void SweepAway(Vector2 nearest, Vector2 swipeDir, float swipeSpeed)
    {
        Vector2 away = (Vector2)transform.position - nearest;
        if (away.sqrMagnitude < 1e-4f) away = Random.insideUnitCircle.normalized;
        _fleeDir = (away.normalized + swipeDir * 0.25f).normalized;

        float s = Mathf.Clamp01(swipeSpeed);
        BeginFlee(nearest, Mathf.Lerp(0.35f, 0.8f, s));
        ApplyPush(nearest, Mathf.Lerp(0.3f, 0.8f, s));
    }

    private void BeginFlee(Vector2 source, float duration)
    {
        Vector2 away = (Vector2)transform.position - source;
        if (away.sqrMagnitude < 1e-4f) away = Random.insideUnitCircle.normalized;
        _fleeDir = away.normalized;

        _fleeTimer = Mathf.Max(_fleeTimer, duration);
        _fleeRatio = fleeStartDampingMul;
        _dashTimer = Mathf.Max(_dashTimer, duration);

        // 逃跑速度统一在这里决定：基础速度 × 个体倍率，并保证不低于 fleeMinSpeed
        float baseSpeed = Random.Range(speedFast.x, speedFast.y) * _fleeSpeedMul;
        _targetSpeed = Mathf.Max(_targetSpeed, Mathf.Max(baseSpeed, fleeMinSpeed));

        PickNewTarget();
        _flapBoost = Mathf.Max(_flapBoost, 1.8f);
    }

    /// <summary>按"总位移"施加硬推。falloff 0~1 缩放</summary>
    private void ApplyPush(Vector2 source, float falloff)
    {
        if (fleePushDistance <= 0f) return;

        Vector2 away = (Vector2)transform.position - source;
        if (away.sqrMagnitude < 1e-4f) away = Random.insideUnitCircle.normalized;

        // 总位移 = V₀ / k  →  V₀ = 目标位移 × k
        float dist = fleePushDistance * Random.Range(0.8f, 1.3f) * Mathf.Clamp01(falloff);
        _kbVel += away.normalized * (dist * fleePushDamping);
    }


    // ==================== 距离回收（由 Manager 驱动） ====================


    /// <summary>由 Manager 直接指定目标（用于"从屏幕外飞入"）</summary>
    public void SetTarget(Vector2 target) => _target = target;


    // ==================== 缩放（体积 + 假深度）====================
    /// <summary>
    /// 统一缩放入口。
    /// 最终大小 = sizeScale × 个体随机倍率 × [假深度的近大远小]
    /// 关掉 useFakeDepth 时仍然应用体积系数。
    /// </summary>
    private void ApplyScale()
    {
        float s = sizeScale * _sizeMul;

        if (useFakeDepth)
        {
            float d = Mathf.InverseLerp(worldBottom, worldTop, transform.position.y);
            s *= Mathf.Lerp(nearScale, farScale, d);

            if (_sr != null)
                _sr.sortingOrder = Mathf.RoundToInt(d * 100f);
        }

        transform.localScale = new Vector3(s, s, 1f);
    }
    // ==================== 调试 ====================
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = IsEscaping                ? Color.magenta :
                       _state == FlyState.Dash   ? Color.red :
                       _state == FlyState.Glide  ? Color.green : Color.yellow;
        Gizmos.DrawWireSphere(_target, 0.15f);
        Gizmos.DrawLine(transform.position, _target);

        if (_kbVel.sqrMagnitude > 1e-4f)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(transform.position, (Vector3)(_kbVel * 0.15f));
        }
    }
}