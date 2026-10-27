using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 自制启动 Logo：从 exe 同级目录读图片显示，然后切到主场景。
///
/// 为什么不用 Unity 内置 Splash：它在引擎初始化阶段渲染，脚本还没执行，
/// 图片是打包时嵌入的资源，无法在运行时替换。只有自制场景才能"可替换"。
///
/// 所有可调参数都可以被 config.ini 的 [App] 节覆盖：
///   logoHoldSeconds        停留时长（秒）
///   logoScreenFraction     Logo 占屏幕的比例（0.1~1）
///   logoFadeInSeconds      渐入时长（秒）
///   logoFadeOutSeconds     渐出时长（秒）
///   logoMissingHoldSeconds 找不到图片时的等待（秒）
///   logoDir                图片文件夹（相对 exe 同级目录）
///   logoName               文件名（不含扩展名）
///
/// 场景结构建议：
///   Camera (Solid Color = 黑)
///   Canvas (Screen Space - Overlay)
///     └── LogoRoot (CanvasGroup + RawImage)
/// </summary>
public class Game_SplashLogo : MonoBehaviour, IConfigurable
{
    [Header("引用")]
    public CanvasGroup canvasGroup;
    public RawImage logoImage;

    [Header("场景")]
    [Tooltip("显示完后加载的场景名（必须在 Build Settings 里）")]
    public string nextSceneName = "Main";

    [Header("图片")]
    [Tooltip("相对 exe 同级目录的文件夹")]
    public string logoDir = "Logo";

    [Tooltip("文件名（不含扩展名）")]
    public string logoName = "logo";

    [Header("显示")]
    [Tooltip("加载完成后停留多久（秒）")]
    public float holdSeconds = 2f;

    [Tooltip("Logo 区域占屏幕的比例（1 = 铺满，0.6 = 居中六成）")]
    [Range(0.1f, 1f)] public float screenFraction = 0.55f;

    [Tooltip("是否保持图片原始宽高比")]
    public bool preserveAspect = true;

    // ==================== 渐入渐出 ====================
    [Header("渐入")]
    [Tooltip("淡入时长（秒）。0 = 瞬间出现")]
    public float fadeInSeconds = 0.6f;

    [Tooltip("淡入曲线：横轴 0~1 = 时间进度，纵轴 = 透明度（0 透明 / 1 不透明）")]
    public AnimationCurve fadeInCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("渐出")]
    [Tooltip("淡出时长（秒）。0 = 瞬间消失")]
    public float fadeOutSeconds = 0.6f;

    [Tooltip("淡出曲线：横轴 0~1 = 时间进度，纵轴 = 透明度")]
    public AnimationCurve fadeOutCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

    // ==================== 跳过 ====================
    [Header("跳过")]
    public bool allowSkip = true;
    public KeyCode skipKey = KeyCode.Escape;

    // ==================== 降级 ====================
    [Header("降级")]
    [Tooltip("读不到图片时，停留这么久后直接进主场景（避免卡死）")]
    public float missingHoldSeconds = 0.8f;

    [Header("调试")]
    public bool logDebug = true;

    private Texture2D _tex;

    private void Start()
    {
        // ★ 必须先套配置：holdSeconds / screenFraction / logoDir 等在后面的流程里要用
        ApplyConfig(Game_Config.Instance);

        // 初始透明度 = 淡入曲线的起点（这样即使曲线不是从 0 开始也正确）
        if (canvasGroup != null)
            canvasGroup.alpha = EvaluateCurve(fadeInCurve, 0f, 0f);

        StartCoroutine(Run());
    }

    private void OnDestroy()
    {
        if (_tex != null) Destroy(_tex);
    }

    // ==================== 配置 ====================
    public void ApplyConfig(Game_Config cfg)
    {
        if (cfg == null) return;

        const string S = "App";

        holdSeconds        = Mathf.Max(0f, cfg.GetFloat(S, "logoHoldSeconds",        holdSeconds));
        screenFraction     = Mathf.Clamp(cfg.GetFloat(S, "logoScreenFraction",       screenFraction), 0.1f, 1f);
        fadeInSeconds      = Mathf.Max(0f, cfg.GetFloat(S, "logoFadeInSeconds",      fadeInSeconds));
        fadeOutSeconds     = Mathf.Max(0f, cfg.GetFloat(S, "logoFadeOutSeconds",     fadeOutSeconds));
        missingHoldSeconds = Mathf.Max(0f, cfg.GetFloat(S, "logoMissingHoldSeconds", missingHoldSeconds));

        // 路径也允许现场改
        if (cfg.TryGet(S, "logoDir", out string dir) && !string.IsNullOrEmpty(dir))
            logoDir = dir.Trim();

        if (cfg.TryGet(S, "logoName", out string name) && !string.IsNullOrEmpty(name))
            logoName = name.Trim();

        if (logDebug && cfg.Loaded)
        {
            Debug.Log($"[SplashLogo] 配置：hold={holdSeconds:F1}s size={screenFraction:F2} " +
                      $"fadeIn={fadeInSeconds:F1}s fadeOut={fadeOutSeconds:F1}s " +
                      $"path={logoDir}/{logoName}");
        }
    }

    private IEnumerator Run()
    {
        // ---------- 1. 读图 ----------
        string path = Game_ExternalAssets.FirstExisting(
            $"{logoDir}/{logoName}.png",
            $"{logoDir}/{logoName}.jpg",
            $"{logoDir}/{logoName}.jpeg");

        bool hasImage = false;

        if (path != null)
        {
            yield return Game_ExternalAssets.LoadTexture(path, t => _tex = t);

            if (_tex != null)
            {
                _tex.wrapMode   = TextureWrapMode.Clamp;
                _tex.filterMode = FilterMode.Bilinear;

                if (logoImage != null)
                {
                    logoImage.texture = _tex;
                    logoImage.color   = Color.white;
                    ApplyLayout();
                }

                hasImage = true;

                if (logDebug)
                    Debug.Log($"[SplashLogo] 已加载 {path}（{_tex.width}x{_tex.height}）");
            }
        }

        if (!hasImage)
        {
            if (logDebug)
                Debug.LogWarning($"[SplashLogo] 找不到 Logo 图片：{Game_ExternalAssets.ResolvePath($"{logoDir}/{logoName}.png")}，直接进入下一步");

            if (canvasGroup != null) canvasGroup.alpha = 0f;

            yield return new WaitForSecondsRealtime(missingHoldSeconds);
            yield return LoadNext();

            yield break;
        }

        // ---------- 2. 淡入 ----------
        yield return Fade(fadeInCurve, fadeInSeconds, 1f);

        // ---------- 3. 停留（可跳过） ----------
        float t = 0f;
        while (t < holdSeconds)
        {
            if (allowSkip && HasSkipInput()) break;

            t += Time.unscaledDeltaTime;
            yield return null;
        }

        // ---------- 4. 淡出 ----------
        yield return Fade(fadeOutCurve, fadeOutSeconds, 0f);

        // ---------- 5. 进主场景 ----------
        yield return LoadNext();
    }

    private void ApplyLayout()
    {
        if (logoImage == null || _tex == null) return;

        var rt = logoImage.rectTransform;

        float sw = Screen.width;
        float sh = Screen.height;

        float maxW = sw * screenFraction;
        float maxH = sh * screenFraction;

        float w = maxW;
        float h = maxH;

        if (preserveAspect)
        {
            float aspect = (float)_tex.width / Mathf.Max(1, _tex.height);

            // 按宽度算高度，如果超出就用高度反推
            h = w / aspect;
            if (h > maxH)
            {
                h = maxH;
                w = h * aspect;
            }
        }

        rt.anchorMin        = new Vector2(0.5f, 0.5f);
        rt.anchorMax        = new Vector2(0.5f, 0.5f);
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta        = new Vector2(w, h);

        if (logDebug)
            Debug.Log($"[SplashLogo] 布局 {w:F0}x{h:F0}（屏幕 {sw:F0}x{sh:F0}，占比 {screenFraction:F2}）");
    }

    // ==================== 曲线渐变 ====================
    /// <summary>
    /// 按曲线做透明度渐变。
    /// 曲线纵轴直接就是 alpha，所以能做出"弹入""闪一下""先暗后亮"等效果。
    /// </summary>
    /// <param name="curve">横轴 0~1 = 时间进度，纵轴 = 目标 alpha</param>
    /// <param name="duration">时长（秒）</param>
    /// <param name="fallbackEnd">曲线无效时使用的终点 alpha</param>
    private IEnumerator Fade(AnimationCurve curve, float duration, float fallbackEnd)
    {
        if (canvasGroup == null) yield break;

        // 曲线没配 / 时长为 0 → 直接到终点
        if (curve == null || curve.length == 0 || duration <= 0f)
        {
            canvasGroup.alpha = EvaluateCurve(curve, 1f, fallbackEnd);
            yield break;
        }

        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;

            float k = Mathf.Clamp01(t / duration);
            canvasGroup.alpha = EvaluateCurve(curve, k, fallbackEnd);

            yield return null;
        }

        canvasGroup.alpha = EvaluateCurve(curve, 1f, fallbackEnd);
    }

    /// <summary>安全地求曲线值，并钳到合法 alpha 范围</summary>
    private static float EvaluateCurve(AnimationCurve curve, float time01, float fallback)
    {
        if (curve == null || curve.length == 0) return Mathf.Clamp01(fallback);

        return Mathf.Clamp01(curve.Evaluate(time01));
    }

    private bool HasSkipInput()
    {
        if (Input.GetKeyDown(skipKey)) return true;

        // 触摸 / 鼠标点击
        if (Input.touchCount > 0) return true;
        if (Input.GetMouseButtonDown(0)) return true;

        return false;
    }

    private IEnumerator LoadNext()
    {
        // ★ 优先走授权门禁：它会校验并把我们送到主场景或激活场景
        if (Game_LicenseGate.TryProceedAfterSplash())
        {
            if (logDebug) Debug.Log("[SplashLogo] Logo 播放完毕，已交给授权门禁");
            yield break;
        }

        // ---- 场景里没有 Gate → 退化为直接进 nextSceneName ----
        if (string.IsNullOrEmpty(nextSceneName))
        {
            Debug.LogError("[SplashLogo] nextSceneName 为空，且场景里没有 Game_LicenseGate");
            yield break;
        }

        if (logDebug) Debug.Log($"[SplashLogo] 没有授权门禁，直接加载 {nextSceneName}");

        var fade = Game_ScreenFade.Instance;

        if (fade != null)
        {
            // ★ 必须交给常驻的 Game_ScreenFade 做完整转场。
            //   不能在这里 yield return FadeOut/FadeIn：
            //   本协程挂在 Game_SplashLogo 上，加载场景时该对象被销毁 → 协程中断
            //   → FadeIn 永远不执行 → 黑幕一直 alpha=1 → 黑屏卡死。
            fade.TransitionTo(nextSceneName, 0.4f, 0.4f);
            yield break;
        }

        var op = SceneManager.LoadSceneAsync(nextSceneName);
        while (op != null && !op.isDone) yield return null;
    }
}
