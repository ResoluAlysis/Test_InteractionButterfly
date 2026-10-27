using UnityEngine;

/// <summary>
/// 蝴蝶"嬉戏"：每只蝴蝶按概率找最近同伴配对，一段时间内以"绕同伴公转"的方式移动。
///
/// 关键：嬉戏是一种独立的移动方式（由 Game_FlockChild_Test.MovePlay 执行），
/// 不走"选目标点 + 到达判定"那条路，因此不会出现目标点抽搐、模式不对称、接近减速。
/// </summary>
public class Game_FlockPlay : MonoBehaviour, IGamePoolable, IConfigurable
{
    [Header("引用（留空自动向父级查找）")]
    public Game_FlockChild_Test agent;

    // ==================== 触发 ====================
    [Header("触发")]
    [Tooltip("每秒检查一次的概率")]
    [Range(0f, 1f)] public float checkChancePerSecond = 0.08f;

    [Tooltip("检查失败后的冷却（秒）")]
    public float decisionCooldown = 1.5f;

    [Tooltip("扫描帧错开：每 N 帧才允许一次检查")]
    public int staggerFrames = 30;

    // ==================== 寻找同伴 ====================
    [Header("寻找同伴")]
    [Tooltip("搜索半径（世界单位）")]
    public float searchRadius = 4f;

    [Tooltip("同屏嬉戏组数上限。0 = 不限")]
    public int maxGroups = 0;

    // ==================== 嬉戏状态 ====================
    [Header("嬉戏状态")]
    [Tooltip("持续时长（秒）")]
    public Vector2 durationRange = new Vector2(3f, 6f);

    [Tooltip("公转圈数")]
    public Vector2 revolutionsRange = new Vector2(0.8f, 1.6f);

    // ==================== 轨道 ====================
    [Header("轨道")]
    [Tooltip("公转半径（世界单位）。★ 必须大于蝴蝶的转弯半径，否则会螺旋收进去")]
    public float orbitRadius = 1.6f;

    [Tooltip("公转半径的随机浮动比例")]
    [Range(0f, 0.5f)] public float orbitRadiusJitter = 0.25f;

    [Tooltip("公转期间的最大速度。过快会导致转弯半径变大、轨道外扩")]
    public float playSpeed = 1.6f;

    [Tooltip("公转期间的转速系数（与 Agent 的 damping 相乘）")]
    [Range(0.5f, 5f)] public float playTurnMul = 1.2f;

    // ==================== 调试 ====================
    [Header("调试")]
    public bool showGizmos = true;
    public bool logDebug = false;

    // ==================== 运行时 ====================
    private Game_FlockChild_Test _partner;
    private float _playTimer;
    private float _cooldownTimer;
    private float _orbit;          // 0~1 公转进度
    private float _orbitSpeed;     // 每秒进度
    private int   _side;           // +1 / -1
    private float _radius;         // 本次实际公转半径

    public Game_FlockChild_Test Partner => _partner;
    public bool IsPlaying => _playTimer > 0f && _partner != null;

    /// <summary>本次公转的半径（供 Gizmo / 调参参考）</summary>
    public float OrbitRadius => _radius;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        if (agent == null) agent = GetComponentInParent<Game_FlockChild_Test>();

        if (agent == null)
        {
            Debug.LogWarning($"[FlockPlay] {name} 找不到 Game_FlockChild_Test，组件已禁用");
            enabled = false;
            return;
        }
    }
    
    private void Start()
    {
        // ★ 在 Awake 之后、生成之前套用配置
        ApplyConfig(Game_Config.Instance);
    }

    public void ApplyConfig(Game_Config cfg)
    {
        if (cfg == null) return;

        const string S = "Play";
        
        checkChancePerSecond = Mathf.Clamp(cfg.GetFloat(S, "checkChancePerSecond", checkChancePerSecond), 0f, 1f);
        orbitRadius = Mathf.Max(0.1f, cfg.GetFloat(S, "orbitRadius", orbitRadius));
        playSpeed = Mathf.Max(0.1f, cfg.GetFloat(S, "playSpeed", playSpeed));
    }

    public void OnSpawned()
    {
        _partner       = null;
        _playTimer     = 0f;
        _orbit         = 0f;
        _cooldownTimer = Random.Range(0f, decisionCooldown);
    }

    public void OnDespawned()
    {
        EndPlay(false);
    }

    // ==================== 每帧 ====================
    private void Update()
    {
        // ★ 逃跑优先于嬉戏：被吓到就立刻脱离
        if (_playTimer > 0f && agent.IsEscaping)
        {
            EndPlay(true);          // 通知同伴一起退出
            return;
        }
        
        // ---- 计时 ----
        if (_playTimer > 0f)
        {
            _playTimer -= Time.deltaTime;
            _orbit += _orbitSpeed * Time.deltaTime;

            // 公转进度回绕到 0~1
            if (_orbit > 1f) _orbit -= Mathf.Floor(_orbit);

            // ★ 把"正在嬉戏"告诉 Agent，让它走 MovePlay 而不是常规 Move
            agent.SetPlayActive(true, _orbit, _radius, _side);

            if (_playTimer <= 0f) { EndPlay(true); return; }
            if (_partner == null) { EndPlay(false); return; }

            return;      // 嬉戏期间不做新决策
        }

        // 确保 Agent 处于非嬉戏状态
        agent.SetPlayActive(false, 0f, 0f, 1);

        // ---- 冷却 ----
        if (_cooldownTimer > 0f)
        {
            _cooldownTimer -= Time.deltaTime;
            return;
        }

        // ---- 帧错开 ----
        if (staggerFrames > 1 && (Time.frameCount % staggerFrames) != 0) return;

        // ---- 逃跑中不开始 ----
        if (agent.IsEscaping) return;

        // ---- 概率检查 ----
        if (Random.value > checkChancePerSecond * staggerFrames * Time.deltaTime)
            return;

        TryStartPlay();
    }

    // ==================== 配对 ====================
    private void TryStartPlay()
    {
        var mgr = Game_FlockManager.Instance;
        if (mgr == null) return;

        var agents = mgr.Agents;
        if (agents.Count < 2) return;

        var mine = transform.position;

        Game_FlockChild_Test best = null;
        float bestD2 = searchRadius * searchRadius;
        int playingGroups = 0;

        for (int i = 0; i < agents.Count; i++)
        {
            var other = agents[i];
            if (other == null || other == agent) continue;

            var op = other.GetComponent<Game_FlockPlay>();
            if (op == null) continue;

            if (other.IsEscaping) continue;

            if (op.IsPlaying) { playingGroups++; continue; }   // 已配对的不能抢

            float d2 = ((Vector2)other.transform.position - (Vector2)mine).sqrMagnitude;
            if (d2 > bestD2) continue;

            bestD2 = d2;
            best = other;
        }

        if (maxGroups > 0 && playingGroups >= maxGroups) return;

        if (best == null)
        {
            _cooldownTimer = decisionCooldown;
            return;
        }

        var bestPlay = best.GetComponent<Game_FlockPlay>();
        if (bestPlay == null) return;

        StartPlayWith(best, bestPlay);
    }

    private void StartPlayWith(Game_FlockChild_Test other, Game_FlockPlay otherPlay)
    {
        float duration = Random.Range(durationRange.x, durationRange.y);
        int   side     = Random.value < 0.5f ? 1 : -1;
        float revs     = Random.Range(revolutionsRange.x, revolutionsRange.y);
        float radius   = orbitRadius * Random.Range(1f - orbitRadiusJitter, 1f + orbitRadiusJitter);

        // ---- 我自己 ----
        _partner       = other;
        _playTimer     = duration;
        _orbit         = Random.value;               // 随机起始相位，避免同步
        _orbitSpeed    = revs / Mathf.Max(0.1f, duration);
        _side          = side;
        _radius        = radius;
        _cooldownTimer = duration + Random.Range(1f, 3f);

        // ---- 对方：同 side / 同 orbitSpeed / 同 radius，才能绕成一对 ----
        otherPlay._partner       = agent;
        otherPlay._playTimer     = duration;
        otherPlay._orbit         = _orbit + 0.5f;    // ★ 相位差半圈（180°），两人分别在对方两侧
        otherPlay._orbitSpeed    = _orbitSpeed;
        otherPlay._side          = side;
        otherPlay._radius        = radius;
        otherPlay._cooldownTimer = _cooldownTimer;

        if (logDebug)
            Debug.Log($"[FlockPlay] {name} 与 {other.name} 配对，{duration:F1}s，{revs:F1} 圈，半径 {radius:F2}");
    }

    public void EndPlay(bool notifyPartner)
    {
        var other = _partner;

        _partner   = null;
        _playTimer = 0f;
        _cooldownTimer = Mathf.Max(_cooldownTimer, Random.Range(1f, 3f));

        // 立刻恢复正常移动
        agent?.SetPlayActive(false, 0f, 0f, 1);
        agent?.MarkForceRetarget();

        if (!notifyPartner || other == null) return;

        var op = other.GetComponent<Game_FlockPlay>();
        if (op != null && op._partner == agent)
        {
            op._partner   = null;
            op._playTimer = 0f;
            op._cooldownTimer = Mathf.Max(op._cooldownTimer, Random.Range(1f, 3f));
            other.SetPlayActive(false, 0f, 0f, 1);
            other.MarkForceRetarget();
        }
    }

    // ==================== 调试 ====================
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!showGizmos || !Application.isPlaying) return;

        if (IsPlaying && _partner != null)
        {
            Vector2 them = _partner.transform.position;

            // 公转轨道（品红细圈）
            Gizmos.color = new Color(1f, 0.3f, 1f, 0.5f);
            Gizmos.DrawWireSphere(them, _radius);

            // 我与同伴的连线
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(transform.position, them);
            Gizmos.DrawWireSphere(transform.position, 0.2f);
        }
    }
#endif
}