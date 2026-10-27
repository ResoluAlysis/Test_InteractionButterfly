using UnityEngine;

/// <summary>
/// 蝴蝶音效：按飞行状态概率触发不同音效池，受惊时立刻出声。
///
/// 性能保护三层：
///   1. 概率触发（不是每帧必播）
///   2. 相机距离剔除（远处不发声）
///   3. 全局并发上限（Game_FlockAudioManager.maxConcurrent）
///
/// 与 Game_FlockTrail / Game_FlockAnimator 同一模式：只读 Agent，不回写。
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class Game_FlockAudio : MonoBehaviour, IGamePoolable, IConfigurable
{
    // ==================== 引用 ====================
    [Header("引用（留空自动向父级查找）")]
    public Game_FlockChild_Test agent;

    [Header("音源对象（留空则用本物体上的 AudioSource）")]
    public AudioSource audioSource;

    // ==================== 音效池 ====================
    [Header("飞行音效池（按状态）")]
    public AudioClip[] cruiseClips;
    public AudioClip[] glideClips;
    public AudioClip[] dashClips;

    [Header("受惊音效池（切换时立刻播）")]
    public AudioClip[] fleeClips;

    [Header("停驻音效池（预留，需要有栖枝系统）")]
    public AudioClip[] restClips;

    // ==================== 触发概率 ====================
    [Header("每秒触发概率（0.05 = 每秒 5% 机会）")]
    [Range(0f, 1f)] public float cruiseChance = 0.05f;
    [Range(0f, 1f)] public float glideChance  = 0.02f;
    [Range(0f, 1f)] public float dashChance   = 0.12f;
    [Range(0f, 1f)] public float restChance   = 0.05f;

    [Header("受惊时立即播出的概率")]
    [Range(0f, 1f)] public float fleeChance = 0.8f;

    [Header("概率抖动：让每只蝴蝶的节奏不同")]
    [Range(0f, 1f)] public float chanceJitter = 0.5f;

    // ==================== 音色随机 ====================
    [Header("音高 / 音量随机区间")]
    public Vector2 pitchRange  = new Vector2(0.85f, 1.05f);
    public Vector2 volumeRange = new Vector2(0.5f, 0.75f);

    // ==================== 并发 ====================
    [Header("同时可播放的最大音效数（环形缓冲）")]
    [Range(1, 4)] public int voicesOnThisAgent = 1;

    // ==================== 距离剔除 ====================
    [Header("距离剔除：离相机超过这个距离不发声（0 = 不剔除）")]
    public float audibleDistance = 25f;

    [Header("剔除检查间隔（帧）")]
    [Range(1, 30)] public int distanceCheckInterval = 10;

    // ==================== 运行时 ====================
    private AudioSource[] _sources;
    private int    _voiceIndex;
    private float  _timer;
    private float  _chanceMul = 1f;
    private bool   _audible = true;
    private int    _lastCheckFrame = -999;

    private Game_FlockChild_Test.FlyState _lastState;
    private bool _lastEscaping;

    private void Awake()
    {
        if (agent == null) agent = GetComponentInParent<Game_FlockChild_Test>();

        if (audioSource == null) audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
        {
            Debug.LogWarning($"[FlockAudio] {name} 找不到 AudioSource，组件已禁用");
            enabled = false;
            return;
        }

        // 2D 音效：正交俯视相机下不需要 3D 衰减
        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake   = false;
        audioSource.loop          = false;

        // 环形缓冲
        int n = Mathf.Max(1, voicesOnThisAgent);
        _sources = new AudioSource[n];
        _sources[0] = audioSource;

        for (int i = 1; i < n; i++)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.spatialBlend = 0f;
            src.playOnAwake   = false;
            src.loop          = false;
            _sources[i] = src;
        }

        // 每只蝴蝶的触发节奏不同
        _chanceMul = 1f + Random.Range(-chanceJitter, chanceJitter);
        _chanceMul = Mathf.Max(0.05f, _chanceMul);

        _timer = Random.Range(0f, 1f);
    }
    private void Start()
    {
        // ★ 在 Awake 之后、生成之前套用配置
        ApplyConfig(Game_Config.Instance);
    }

    public void ApplyConfig(Game_Config cfg)
    {
        if (cfg == null) return;

        const string S = "Audio";
        
        cruiseChance = Mathf.Clamp01(cfg.GetFloat(S, "CruiseChance", cruiseChance));
        dashChance = Mathf.Clamp01(cfg.GetFloat(S, "dashChance", dashChance));
        fleeChance = Mathf.Clamp01(cfg.GetFloat(S, "fleeChance", fleeChance));
    }

    // ==================== 池化回调 ====================
    public void OnSpawned()
    {
        StopAll();

        _timer = Random.Range(0f, 1f);
        _lastState = (Game_FlockChild_Test.FlyState)(-1);
        _lastEscaping = false;
        
        ApplyConfig(Game_Config.Instance);   // ★ 池化复用 → 重新拉最新配置
    }

    public void OnDespawned()
    {
        StopAll();
    }

    private void StopAll()
    {
        if (_sources == null) return;
        for (int i = 0; i < _sources.Length; i++)
            if (_sources[i] != null) _sources[i].Stop();
    }

    // ==================== 每帧 ====================
    private void Update()
    {
        if (!agent || _sources == null) return;

        // ---- 三层保护之 2：距离剔除 ----
        if (audibleDistance > 0f && Time.frameCount - _lastCheckFrame >= distanceCheckInterval)
        {
            _lastCheckFrame = Time.frameCount;
            _audible = Game_FlockAudioManager.IsWithinAudibleRange(transform.position, audibleDistance);
        }

        if (!_audible) return;

        bool escaping = agent.IsEscaping;

        // ---- 受惊：状态切换的那一帧尝试出声 ----
        if (escaping && !_lastEscaping)
        {
            _timer = 0f;      // 受惊后重新计时

            if (Random.value < fleeChance * _chanceMul)
                PlayFromPool(fleeClips);
        }

        _lastEscaping = escaping;

        // ---- 飞行状态音效：按秒概率触发 ----
        var state = agent.State;

        if (state != _lastState)
        {
            _lastState = state;
            _timer = Random.Range(0f, 0.5f);      // 换状态后稍微等一下
            return;
        }

        _timer += Time.deltaTime;
        if (_timer < 1f) return;

        _timer = 0f;

        if (escaping) return;                      // 受惊期间只播 flee 音

        float chance = GetChance(state) * _chanceMul;
        if (Random.value < chance)
            PlayFromPool(GetPool(state));
    }

    private float GetChance(Game_FlockChild_Test.FlyState s)
    {
        switch (s)
        {
            case Game_FlockChild_Test.FlyState.Glide: return glideChance;
            case Game_FlockChild_Test.FlyState.Dash:  return dashChance;
            default:                                  return cruiseChance;
        }
    }

    private AudioClip[] GetPool(Game_FlockChild_Test.FlyState s)
    {
        switch (s)
        {
            case Game_FlockChild_Test.FlyState.Glide: return glideClips;
            case Game_FlockChild_Test.FlyState.Dash:  return dashClips;
            default:                                  return cruiseClips;
        }
    }

    // ==================== 播放 ====================
    private void PlayFromPool(AudioClip[] pool)
    {
        if (pool == null || pool.Length == 0) return;

        var clip = pool[Random.Range(0, pool.Length)];
        if (clip == null) return;

        // ---- 三层保护之 3：全局并发上限 ----
        if (Game_FlockAudioManager.Instance != null &&
            !Game_FlockAudioManager.Instance.TryReserveSlot())
            return;

        var src = _sources[_voiceIndex];
        _voiceIndex = (_voiceIndex + 1) % _sources.Length;

        if (src == null) return;

        src.pitch  = Random.Range(pitchRange.x,  pitchRange.y);
        src.volume = Random.Range(volumeRange.x, volumeRange.y);

        src.clip = clip;
        src.Play();
    }

    // ==================== 调试 ====================
#if UNITY_EDITOR
    [HideInInspector] public int dbgPlayCount;

    private void OnDrawGizmosSelected()
    {
        if (agent == null) return;

        Gizmos.color = _audible ? Color.green : new Color(0.4f, 0.4f, 0.4f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, 0.3f);
    }
#endif
}