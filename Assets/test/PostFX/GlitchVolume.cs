using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 赛博故障风格的全屏后处理参数。
/// 在 Volume（Global）里 Add Override → Custom → Glitch 即可调。
/// </summary>
[System.Serializable, VolumeComponentMenu("Custom/Glitch")]
public class GlitchVolume : VolumeComponent, IPostProcessComponent
{
    [Header("总强度")]
    [Tooltip("所有通道的总体倍率")]
    public ClampedFloatParameter intensity = new ClampedFloatParameter(0.6f, 0f, 1f);

    // ==================== 脉冲 ====================
    [Header("脉冲（间歇性爆发）")]
    [Tooltip("开启后强度会随时间起伏，而不是一直抖动")]
    public BoolParameter pulseEnabled = new BoolParameter(true);

    [Tooltip("脉冲速度。越大爆发越频繁")]
    public ClampedFloatParameter pulseSpeed = new ClampedFloatParameter(0.6f, 0.05f, 5f);

    [Tooltip("尖锐度。越大越像'偶发故障'，越小越像持续噪声")]
    public ClampedFloatParameter pulseSharpness = new ClampedFloatParameter(4f, 1f, 12f);

    [Tooltip("脉冲的低谷值。0 = 完全静止，1 = 从不安静")]
    public ClampedFloatParameter pulseFloor = new ClampedFloatParameter(0.05f, 0f, 1f);

    // ==================== 块位移 ====================
    [Header("块位移（横向条带错位）")]
    [Tooltip("多少比例的条带会发生位移")]
    public ClampedFloatParameter blockAmount = new ClampedFloatParameter(0.12f, 0f, 1f);

    [Tooltip("条带数量。越大条带越细")]
    public ClampedFloatParameter blockCount = new ClampedFloatParameter(24f, 4f, 200f);

    [Tooltip("单条带的最大水平位移（UV 单位）")]
    public ClampedFloatParameter blockShift = new ClampedFloatParameter(0.06f, 0f, 0.5f);

    [Tooltip("条带变化速度")]
    public ClampedFloatParameter blockSpeed = new ClampedFloatParameter(12f, 0f, 60f);

    // ==================== RGB 分离 ====================
    [Header("RGB 分离")]
    [Tooltip("红/蓝通道的偏移量（UV 单位）")]
    public ClampedFloatParameter rgbSplit = new ClampedFloatParameter(0.0035f, 0f, 0.05f);

    // ==================== 扫描线 ====================
    [Header("扫描线")]
    public ClampedFloatParameter scanlineIntensity = new ClampedFloatParameter(0.18f, 0f, 1f);

    [Tooltip("扫描线数量")]
    public ClampedFloatParameter scanlineCount = new ClampedFloatParameter(540f, 10f, 2000f);

    [Tooltip("扫描线流动速度")]
    public ClampedFloatParameter scanlineSpeed = new ClampedFloatParameter(2f, 0f, 20f);

    // ==================== 噪点 ====================
    [Header("噪点")]
    public ClampedFloatParameter noiseAmount = new ClampedFloatParameter(0.055f, 0f, 0.5f);

    // ==================== 暗角 ====================
    [Header("暗角")]
    public ClampedFloatParameter vignette = new ClampedFloatParameter(0.55f, 0f, 2f);

    // ==================== 色彩 ====================
    [Header("色彩")]
    [Tooltip("1 = 原始饱和度，0 = 灰度，>1 = 过饱和")]
    public ClampedFloatParameter saturation = new ClampedFloatParameter(1.05f, 0f, 2f);

    [Tooltip("整体染色（叠加）")]
    public ColorParameter tint = new ColorParameter(new Color(1f, 1f, 1f, 1f));

    [Tooltip("染色强度")]
    public ClampedFloatParameter tintAmount = new ClampedFloatParameter(0f, 0f, 1f);
    
    // ==================== 触发式故障 ====================
    [Header("触发式故障")]
    [Tooltip("触发时的故障强度。1 = 明显，2~3 = 强烈")]
    public ClampedFloatParameter burstStrength = new ClampedFloatParameter(1.6f, 0f, 5f);

    [Tooltip("触发时的持续秒数")]
    public ClampedFloatParameter burstDuration = new ClampedFloatParameter(0.35f, 0.05f, 3f);

    // ==================== 接口 ====================
    public bool IsActive()
    {
        // ★ 即使 intensity 为 0，只要正在爆发也要执行
        return intensity.value > 0.0001f || GlitchBurst.IsRunning;
    }

    public bool IsTileCompatible() => false;
}