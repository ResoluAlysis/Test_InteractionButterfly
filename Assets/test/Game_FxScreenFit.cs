using UnityEngine;

/// <summary>全屏粒子的锚点位置（3x3 九宫格）</summary>
public enum ScreenAnchor
{
    Center = 0,          // 屏幕中央
    TopCenter,           // 屏幕上方，水平居中
    BottomCenter,        // 屏幕下方，水平居中
    LeftCenter,          // 屏幕左侧，垂直居中
    RightCenter,         // 屏幕右侧，垂直居中
    TopLeft,             // 左上角
    TopRight,            // 右上角
    BottomLeft,          // 左下角
    BottomRight,         // 右下角
}

/// <summary>
/// 让粒子系统的发射区自动铺满相机视野。
///
/// 做三件事：
///   1. 把 Shape(Box) 的尺寸按 X / Y 轴分别适配相机视野（可单独开关）
///   2. 根据锚点把发射区贴到屏幕的指定位置（默认居中）
///   3. 按宽度比调 rateOverTime，保证换分辨率后粒子密度不变
///
/// 不碰 Start Size / Speed / Color / Lifetime —— 那些全部由你手调。
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class Game_FxScreenFit : MonoBehaviour
{
    [Header("相机（留空自动取 MainCamera）")]
    public Camera cam;

    // ==================== 自适应轴 ====================
    [Header("自适应轴")]
    [Tooltip("X 轴（水平）适配相机视野宽度。关掉则保持原始 Shape 宽度")]
    public bool fitX = true;

    [Tooltip("Y 轴（垂直）适配相机视野高度。关掉则保持原始 Shape 高度")]
    public bool fitY = true;

    // ==================== 发射区 ====================
    [Header("发射区")]
    [Tooltip("发射区相对相机视野的倍率。1 = 正好等于屏幕，1.2 = 四周多出 20% 余量")]
    public float areaScale = 1.2f;

    [Tooltip("发射面所在的 z 平面（世界坐标）。粒子会被生成在这个 z 上")]
    public float planeZ = 0f;

    [Tooltip("发射面相对物体的局部偏移")]
    public Vector2 areaOffset = Vector2.zero;

    // ==================== 锚点 ====================
    [Header("锚点")]
    [Tooltip("发射区相对相机视野的位置。只在 followCamera 开启时生效")]
    public ScreenAnchor anchor = ScreenAnchor.Center;

    [Tooltip("锚点到屏幕边缘的留白（世界单位）。0 = 完全贴边")]
    [Min(0f)] public float anchorPadding = 0f;

    // ==================== 发射率 ====================
    [Header("发射率")]
    [Tooltip("勾上：rateOverTime 按发射区尺寸等比例缩放，密度保持不变")]
    public bool scaleRateByArea = true;

    [Tooltip("关掉 scaleRateByArea 时使用的固定发射率")]
    public float fixedRate = 30f;

    // ==================== 跟随 ====================
    [Header("跟随")]
    [Tooltip("勾上：物体位置每帧跟随相机并按锚点定位。适合全屏背景粒子")]
    public bool followCamera = true;

    [Tooltip("跟随模式：LateUpdate 更稳（相机移动后不会再滞后一帧）")]
    public bool followInLateUpdate = true;

    [Header("位置钳制")]
    [Tooltip("发射面 z 钳到相机近裁剪面之后，避免被裁掉。只有 followCamera 时才需要")]
    public bool clampZToCameraDepth = true;

    [Header("调试")]
    public bool logChanges = false;

    // ==================== 运行时 ====================
    private ParticleSystem _ps;
    private float _origRate;

    private Vector3 _baseShapeScale = new Vector3(1f, 1f, 0.01f);  // Shape 的原始尺寸
    private float _baseHalfW;      // 首次刷新时的视野半宽，作为"基准"
    private float _baseHalfH;      // 首次刷新时的视野半高
    private float _lastHalfW = -1f;
    private float _lastHalfH = -1f;
    private bool _initialized;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        _ps = GetComponent<ParticleSystem>();

        if (cam == null) cam = Camera.main;

        if (cam == null)
        {
            Debug.LogWarning($"[FxScreenFit] {name} 找不到相机，组件已禁用");
            enabled = false;
            return;
        }

        // 校验 Shape 模块
        var shape = _ps.shape;
        if (!shape.enabled)
        {
            Debug.LogWarning($"[FxScreenFit] {name} 的 Shape 模块没启用，无法设置发射区");
        }
        else if (shape.shapeType != ParticleSystemShapeType.Box)
        {
            Debug.LogWarning($"[FxScreenFit] {name} 的 Shape 类型是 {shape.shapeType}，" +
                             "本组件按 Box 设计。请改成 Box（或改脚本用 radius 分支）");
        }

        // ★ 记录原始 Shape 尺寸（fitX / fitY 关掉时用它）
        _baseShapeScale = shape.scale;

        _origRate = _ps.emission.rateOverTime.constant;

        // 首次定位 + 刷新
        if (followCamera) SyncTransform();
        Refresh(true);
    }

    private void Update()
    {
        if (!followCamera || followInLateUpdate) return;

        SyncTransform();
        Refresh(false);
    }

    private void LateUpdate()
    {
        if (!followCamera || !followInLateUpdate) return;

        SyncTransform();
        Refresh(false);
    }

    // ==================== 跟随相机（按锚点定位）====================
    private void SyncTransform()
    {
        GetViewHalfSize(out float halfW, out float halfH);
        GetAnchorDir(out float ax, out float ay);

        // 锚点到屏幕边缘的留白
        float usableW = Mathf.Max(0f, halfW - anchorPadding);
        float usableH = Mathf.Max(0f, halfH - anchorPadding);

        Vector3 c = cam.transform.position;

        float z = planeZ;
        if (clampZToCameraDepth)
            z = Mathf.Max(planeZ, c.z + cam.nearClipPlane + 0.5f);

        transform.position = new Vector3(
            c.x + ax * usableW,
            c.y + ay * usableH,
            z);
    }

    /// <summary>锚点方向：-1 / 0 / +1</summary>
    private void GetAnchorDir(out float ax, out float ay)
    {
        switch (anchor)
        {
            case ScreenAnchor.TopLeft:      ax = -1f; ay =  1f; return;
            case ScreenAnchor.TopCenter:    ax =  0f; ay =  1f; return;
            case ScreenAnchor.TopRight:     ax =  1f; ay =  1f; return;
            case ScreenAnchor.LeftCenter:   ax = -1f; ay =  0f; return;
            case ScreenAnchor.RightCenter:  ax =  1f; ay =  0f; return;
            case ScreenAnchor.BottomLeft:   ax = -1f; ay = -1f; return;
            case ScreenAnchor.BottomCenter: ax =  0f; ay = -1f; return;
            case ScreenAnchor.BottomRight:  ax =  1f; ay = -1f; return;
            case ScreenAnchor.Center:
            default:                        ax =  0f; ay =  0f; return;
        }
    }

    /// <summary>相机可视范围的半宽 / 半高（不含 areaScale）</summary>
    private void GetViewHalfSize(out float halfW, out float halfH)
    {
        if (cam.orthographic)
        {
            halfH = cam.orthographicSize;
            halfW = halfH * cam.aspect;
        }
        else
        {
            // 透视相机：取"发射面所在平面"处的可视尺寸
            float dist = Mathf.Max(0.01f, Mathf.Abs(planeZ - cam.transform.position.z));
            halfH = dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            halfW = halfH * cam.aspect;
        }
    }

    // ==================== 刷新发射区 ====================
    /// <summary>强制立刻刷新一次（分辨率变化 / 场景切换后可手动调）</summary>
    public void RefreshNow()
    {
        Refresh(true);
        if (followCamera) SyncTransform();
    }

    private void Refresh(bool force)
    {
        if (_ps == null || cam == null) return;

        GetViewHalfSize(out float halfW, out float halfH);

        // ---- 只在"启用的轴真的变了"时才刷新 ----
        bool changedX = !Mathf.Approximately(halfW, _lastHalfW);
        bool changedY = !Mathf.Approximately(halfH, _lastHalfH);

        bool needRefresh = force
                        || (fitX && changedX)
                        || (fitY && changedY);

        if (!needRefresh) return;

        _lastHalfW = halfW;
        _lastHalfH = halfH;

        // ---- 发射区尺寸：按轴分别适配 ----
        float w = fitX ? halfW * 2f * areaScale : _baseShapeScale.x;
        float h = fitY ? halfH * 2f * areaScale : _baseShapeScale.y;

        var shape = _ps.shape;
        shape.scale    = new Vector3(w, h, _baseShapeScale.z);
        shape.position = new Vector3(areaOffset.x, areaOffset.y, 0f);

        // ---- 发射率 ----
        if (scaleRateByArea)
        {
            if (!_initialized)
            {
                // 第一次：把当前视野当作基准，保留你手调的 rateOverTime
                _baseHalfW = halfW;
                _baseHalfH = halfH;
                _origRate  = _ps.emission.rateOverTime.constant;
                _initialized = true;
            }

            // 有 X 适应 → 按宽度比（背景粒子通常横向铺开，保持疏密）
            // 只有 Y 适应 → 按高度比
            // 都不适应   → 不改发射率
            float ratio = 1f;

            if (fitX)
                ratio = _baseHalfW > 0.001f ? halfW / _baseHalfW : 1f;
            else if (fitY)
                ratio = _baseHalfH > 0.001f ? halfH / _baseHalfH : 1f;

            var emission = _ps.emission;
            emission.rateOverTime = _origRate * ratio;
        }
        else
        {
            var emission = _ps.emission;
            emission.rateOverTime = fixedRate;
        }

        if (logChanges)
        {
            Debug.Log($"[FxScreenFit] {name} fit=({fitX},{fitY}) anchor={anchor} " +
                      $"发射区 {w:F1} x {h:F1}, rate={_ps.emission.rateOverTime.constant:F1}");
        }
    }

    // ==================== 调试 ====================
#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (cam == null) return;

        GetViewHalfSize(out float halfW, out float halfH);
        GetAnchorDir(out float ax, out float ay);

        float usableW = Mathf.Max(0f, halfW - anchorPadding);
        float usableH = Mathf.Max(0f, halfH - anchorPadding);

        Vector3 c = cam.transform.position;
        Vector3 p = new Vector3(c.x + ax * usableW, c.y + ay * usableH, planeZ);

        // 锚点位置
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(p, 0.4f);

        // 屏幕边框
        Gizmos.color = new Color(0.3f, 1f, 1f, 0.4f);
        Gizmos.DrawWireCube(new Vector3(c.x, c.y, planeZ),
                            new Vector3(halfW * 2f, halfH * 2f, 0f));

        // 发射区（按当前开关预估）
        float w = fitX ? halfW * 2f * areaScale : _baseShapeScale.x;
        float h = fitY ? halfH * 2f * areaScale : _baseShapeScale.y;

        Gizmos.color = new Color(1f, 0.5f, 0.2f, 0.5f);
        Gizmos.DrawWireCube(p, new Vector3(w, h, 0f));
    }
#endif
}
