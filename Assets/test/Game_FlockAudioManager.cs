using UnityEngine;

/// <summary>
/// 全局音频预算 + 相机可见性缓存。
/// 蝴蝶数量大时（数百只以上）必须有这层，否则 AudioSource 会把音频线程压垮。
/// </summary>
public class Game_FlockAudioManager : MonoBehaviour, IConfigurable
{
    public static Game_FlockAudioManager Instance { get; private set; }

    [Header("全局并发上限（超过则跳过本次播放）")]
    [Tooltip("Unity 默认同时约 32 个音源。留一点余量给其它音效")]
    public int maxConcurrent = 24;

    [Header("滑窗统计周期（秒）")]
    public float windowSeconds = 0.1f;

    [Header("调试")]
    public bool logStats = false;

    private int   _slotsUsed;
    private float _windowTimer;
    private int   _skipped;

    // 供距离剔除复用的相机缓存
    private static Camera _cam;
    private static int    _camFrame = -1;
    private static float  _halfH, _halfW;
    private static Vector3 _camPos;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        Instance = this;
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
        
        maxConcurrent = Mathf.Max(1, cfg.GetInt(S, "MaxConcurrent",  maxConcurrent));
        
    }
    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        _windowTimer += Time.deltaTime;
        if (_windowTimer < windowSeconds) return;

        _windowTimer = 0f;

        if (logStats)
            Debug.Log($"[Audio] 本周期播放={_slotsUsed} 跳过={_skipped} 上限={maxConcurrent}");

        _skipped    = 0;
        _slotsUsed  = 0;
    }

    // ==================== 预算 ====================
    /// <summary>尝试占用一个并发名额。返回 false 表示本次应该跳过播放</summary>
    public bool TryReserveSlot()
    {
        if (_slotsUsed >= maxConcurrent)
        {
            _skipped++;
            return false;
        }

        _slotsUsed++;
        return true;
    }

    // ==================== 距离剔除 ====================
    /// <summary>目标是否在相机的"可听范围"内（屏幕矩形外扩 distance）</summary>
    public static bool IsWithinAudibleRange(Vector3 worldPos, float distance)
    {
        RefreshCamera();

        if (_cam == null) return true;

        float dx = Mathf.Abs(worldPos.x - _camPos.x);
        float dy = Mathf.Abs(worldPos.y - _camPos.y);

        return dx <= _halfW + distance && dy <= _halfH + distance;
    }

    private static void RefreshCamera()
    {
        if (_camFrame == Time.frameCount) return;
        _camFrame = Time.frameCount;

        if (_cam == null) _cam = Camera.main;
        if (_cam == null)
        {
            _halfH = _halfW = float.MaxValue;
            _camPos = Vector3.zero;
            return;
        }

        _halfH  = _cam.orthographicSize;
        _halfW  = _halfH * _cam.aspect;
        _camPos = _cam.transform.position;
    }
}