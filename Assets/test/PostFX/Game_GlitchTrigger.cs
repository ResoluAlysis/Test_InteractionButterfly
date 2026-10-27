using UnityEngine;

/// <summary>
/// 故障触发入口。挂在任意常驻物体上即可。
/// 主要用途：调试按键 + 给业务代码一个统一的调用点。
/// </summary>
public class Game_GlitchTrigger : MonoBehaviour
{
    [Header("按键触发")]
    public bool enableKey = true;
    public KeyCode triggerKey = KeyCode.G;

    [Tooltip("相对 Volume 里 burstStrength 的倍率")]
    public float keyStrengthScale = 1f;

    [Header("自动触发（可选）")]
    [Tooltip("每隔这么多秒自动触发一次。0 = 关闭")]
    public float autoInterval = 0f;

    private float _autoTimer;

    private void Update()
    {
        // ---- 按键 ----
        if (enableKey && Input.GetKeyDown(triggerKey))
            Burst(keyStrengthScale);

        // ---- 自动 ----
        if (autoInterval > 0f)
        {
            _autoTimer -= Time.deltaTime;

            if (_autoTimer <= 0f)
            {
                _autoTimer = autoInterval;
                Burst(1f);
            }
        }
    }

    /// <summary>触发一次故障（业务代码可直接调用）</summary>
    public static void Burst(float strengthScale = 1f)
    {
        GlitchBurst.Request(strengthScale);
    }
}