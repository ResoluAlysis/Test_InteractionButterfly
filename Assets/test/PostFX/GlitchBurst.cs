using UnityEngine;

/// <summary>
/// 触发式故障：调用 Request() 后，画面会出现一次短暂的强烈故障。
/// 状态是静态的，不依赖任何场景对象，业务代码可以直接调用。
///
/// 参数（强度和时长）来自 Volume 里的 GlitchVolume，这里只负责"请求"和"衰减曲线"。
/// </summary>
public static class GlitchBurst
{
    private static bool  _requested;
    private static float _requestedScale = 1f;

    private static float _remaining;
    private static float _duration;
    private static float _strength;

    /// <summary>是否正在爆发</summary>
    public static bool IsRunning => _remaining > 0f || _requested;

    /// <summary>
    /// 请求一次故障爆发。
    /// 连续调用不会叠加，而是取最强的那次重新计时。
    /// </summary>
    /// <param name="strengthScale">相对 Volume 里 burstStrength 的倍率。1 = 用配置值</param>
    public static void Request(float strengthScale = 1f)
    {
        _requested = true;
        _requestedScale = Mathf.Max(_requestedScale, Mathf.Max(0.01f, strengthScale));
    }

    /// <summary>
    /// 由 GlitchPass 每帧调用：消费请求、推进计时、返回当前爆发强度。
    /// </summary>
    public static float Tick(float dt, float strength, float duration)
    {
        // ---- 消费请求 ----
        if (_requested)
        {
            _requested = false;

            _strength  = Mathf.Max(_strength * (_remaining / Mathf.Max(0.001f, _duration)),
                                   strength * _requestedScale);
            _duration  = Mathf.Max(0.05f, duration);
            _remaining = _duration;

            _requestedScale = 1f;
        }

        // ---- 衰减 ----
        if (_remaining <= 0f) return 0f;

        _remaining -= dt;
        if (_remaining <= 0f)
        {
            _remaining = 0f;
            _strength  = 0f;
            return 0f;
        }

        float t = Mathf.Clamp01(_remaining / _duration);      // 1 → 0

        // 前段慢、后段快 → 像"放电"而不是线性淡出
        return _strength * t * (0.6f + 0.4f * t);
    }

    /// <summary>立刻停止（热重载 / 场景切换时用）</summary>
    public static void Reset()
    {
        _requested      = false;
        _requestedScale = 1f;
        _remaining      = 0f;
        _strength       = 0f;
        _duration       = 0f;
    }
}