using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 全屏黑幕转场：渐黑 → 加载场景 → 渐显。
///
/// 零搭建：挂在任意空物体上即可，Canvas / CanvasGroup / Image 全部由脚本创建。
/// 常驻：DontDestroyOnLoad，跨场景保留。
///
/// 用法：
///   // 一体化（推荐）
///   Game_ScreenFade.Instance.TransitionTo("Main");
///
///   // 手动分步（加载后要做额外初始化时用）
///   yield return Game_ScreenFade.Instance.FadeOut(0.5f);
///   yield return SceneManager.LoadSceneAsync("Main");
///   yield return Game_ScreenFade.Instance.FadeIn(0.5f);
/// </summary>
public class Game_ScreenFade : MonoBehaviour
{
    public static Game_ScreenFade Instance { get; private set; }

    [Header("默认时长（秒）")]
    public float defaultFadeOut = 0.5f;
    public float defaultFadeIn  = 0.5f;

    [Header("曲线")]
    [Tooltip("渐黑曲线：横轴 0~1 = 进度，纵轴 = 黑幕不透明度（0 透明 → 1 全黑）")]
    public AnimationCurve fadeOutCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Tooltip("渐显曲线：横轴 0~1 = 进度，纵轴 = 黑幕不透明度（1 全黑 → 0 透明）")]
    public AnimationCurve fadeInCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

    [Header("外观")]
    public Color fadeColor = Color.black;

    [Tooltip("绘制层级。要比所有 UI Canvas 高")]
    public int sortingOrder = 32000;

    [Header("启动")]
    [Tooltip("启动时先全黑，第一帧后自动渐显（避免开场闪出未初始化的画面）")]
    public bool fadeInOnStart = true;

    [Tooltip("启动渐显时长（秒）")]
    public float startFadeInSeconds = 0.5f;

    [Header("调试")]
    public bool logDebug = true;

    // ==================== 运行时 ====================
    private CanvasGroup _group;
    private Image _image;
    private bool _transitioning;

    public bool IsTransitioning => _transitioning;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // ★ 只销毁自己这个组件，不要 Destroy(gameObject)。
            //   否则如果 Game_Config / Game_SplashLogo 挂在同一个物体上，
            //   会把它们一起杀掉。
            Destroy(this);
            return;
        }

        Instance = this;

        // ---- 常驻 ----
        if (transform.parent != null)
        {
            Debug.LogWarning($"[ScreenFade] {name} 挂在子物体上，DontDestroyOnLoad 会失效。已自动提到根级。");
            transform.SetParent(null);
        }

        DontDestroyOnLoad(gameObject);

        // ---- 自动创建 UI ----
        EnsureOverlay();

        if (fadeInOnStart)
        {
            SetBlackInstant();
            StartCoroutine(StartFadeIn());
        }
        else
        {
            SetClearInstant();
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private IEnumerator StartFadeIn()
    {
        // 等一帧，让第一个场景先完成渲染，否则会看到黑屏一闪再渐显
        yield return null;

        yield return FadeIn(startFadeInSeconds);
    }

    // ==================== 自动建 UI ====================
    private void EnsureOverlay()
    {
        if (_group != null) return;

        // ---- Canvas ----
        var canvasGo = new GameObject("FadeCanvas", typeof(Canvas));
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;

        // ---- CanvasGroup（改 alpha 不触发重建，比改 Image.color 高效）----
        _group = canvasGo.AddComponent<CanvasGroup>();
        _group.interactable   = false;
        _group.blocksRaycasts = false;

        // ---- 全屏黑图 ----
        var imgGo = new GameObject("Black", typeof(Image));
        imgGo.transform.SetParent(canvasGo.transform, false);

        var rt = imgGo.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        _image = imgGo.GetComponent<Image>();
        _image.color         = fadeColor;
        _image.raycastTarget = true;      // 转场时挡住点击
    }

    // ==================== 公开 API ====================
    /// <summary>渐黑（透明 → 全黑）</summary>
    public IEnumerator FadeOut(float duration = -1f)
    {
        if (duration < 0f) duration = defaultFadeOut;
        yield return FadeTo(fadeOutCurve, duration);
    }

    /// <summary>渐显（全黑 → 透明）</summary>
    public IEnumerator FadeIn(float duration = -1f)
    {
        if (duration < 0f) duration = defaultFadeIn;
        yield return FadeTo(fadeInCurve, duration);
    }

    /// <summary>立刻全黑</summary>
    public void SetBlackInstant()
    {
        if (_group == null) return;
        _group.alpha = 1f;
        _group.blocksRaycasts = true;
    }

    /// <summary>立刻透明</summary>
    public void SetClearInstant()
    {
        if (_group == null) return;
        _group.alpha = 0f;
        _group.blocksRaycasts = false;
    }

    /// <summary>
    /// 完整转场：渐黑 → 加载场景 → 渐显。
    /// 时长传负数则用 defaultFadeOut / defaultFadeIn。
    /// </summary>
    public void TransitionTo(string sceneName,
                             float fadeOutDuration = -1f,
                             float fadeInDuration  = -1f)
    {
        if (_transitioning)
        {
            Debug.LogWarning($"[ScreenFade] 转场进行中，忽略 TransitionTo({sceneName})");
            return;
        }

        StartCoroutine(TransitionRoutine(sceneName, fadeOutDuration, fadeInDuration));
    }

    // ==================== 内部 ====================
    private IEnumerator FadeTo(AnimationCurve curve, float duration)
    {
        if (_group == null) yield break;

        _group.blocksRaycasts = true;

        // 曲线无效 / 时长为 0 → 直接到位
        if (curve == null || curve.length == 0 || duration <= 0f)
        {
            _group.alpha = curve != null && curve.length > 0
                ? Mathf.Clamp01(curve.Evaluate(1f))
                : 1f;

            _group.blocksRaycasts = _group.alpha > 0.001f;
            yield break;
        }

        float t = 0f;

        while (t < duration)
        {
            t += Time.unscaledDeltaTime;

            float k = Mathf.Clamp01(t / duration);
            _group.alpha = Mathf.Clamp01(curve.Evaluate(k));

            yield return null;
        }

        _group.alpha = Mathf.Clamp01(curve.Evaluate(1f));
        _group.blocksRaycasts = _group.alpha > 0.001f;

        if (logDebug) Debug.Log($"[ScreenFade] 渐变结束，alpha={_group.alpha:F2}");
    }

    private IEnumerator TransitionRoutine(string sceneName, float outDur, float inDur)
    {
        _transitioning = true;

        if (outDur < 0f) outDur = defaultFadeOut;
        if (inDur  < 0f) inDur  = defaultFadeIn;

        if (logDebug) Debug.Log($"[ScreenFade] 转场 → {sceneName}");

        // ---- 1. 渐黑 ----
        yield return FadeOut(outDur);

        if (logDebug) Debug.Log("[ScreenFade] 已全黑，开始加载场景");

        // ---- 2. 加载 ----
        var op = SceneManager.LoadSceneAsync(sceneName);

        if (op == null)
        {
            Debug.LogError($"[ScreenFade] 无法加载场景 \"{sceneName}\"（检查 Build Settings 里是否包含）");

            yield return FadeIn(inDur);
            _transitioning = false;
            yield break;
        }

        while (!op.isDone) yield return null;

        // ---- 3. 等一帧，让新场景的 Awake/Start 跑完 ----
        yield return null;

        // ---- 4. 渐显 ----
        yield return FadeIn(inDur);

        _transitioning = false;

        if (logDebug) Debug.Log($"[ScreenFade] 转场完成：{sceneName}");
    }
}