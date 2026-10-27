using System;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 授权状态显示：
///   ① 右下角小角标（默认关闭，剩余天数 ≤ warnDays 时出现）
///   ② 按 toggleKey（默认 F9）打开详情面板
///   ③ 启动时在 Console 打印剩余天数
///
/// 常驻单例。放在 00_Logo 场景里任意空物体上。
///
/// 设计意图：观众看不到任何提示，运维按 F9 就能查。
///
/// config.ini 的 [App] 节可覆盖：
///   licenseBadge     1 = 显示角标（默认 0）
///   licenseWarnDays  剩余多少天开始显示角标 / 打警告（默认 30）
/// </summary>
[DefaultExecutionOrder(-880)]
public class Game_LicenseStatus : MonoBehaviour, IConfigurable
{
    public static Game_LicenseStatus Instance { get; private set; }

    // ==================== 配置 ====================
    [Header("角标")]
    [Tooltip("是否在屏幕右下角显示剩余天数。默认关（观众看不到）")]
    public bool showBadge = false;

    [Tooltip("剩余天数 ≤ 这个值时显示角标并打印警告")]
    public int warnDays = 30;

    [Tooltip("角标离屏幕边缘的距离")]
    public Vector2 badgeMargin = new Vector2(28f, 28f);

    public int badgeFontSize = 20;

    [Header("详情面板")]
    [Tooltip("打开/关闭详情面板的按键")]
    public KeyCode toggleKey = KeyCode.F9;

    [Tooltip("允许按 Esc 关闭详情面板")]
    public bool allowEscToClose = true;

    [Header("配色")]
    public Color normalColor = new Color(0.75f, 0.85f, 0.95f, 0.85f);
    public Color warnColor   = new Color(1f, 0.75f, 0.25f, 0.95f);
    public Color errorColor  = new Color(1f, 0.35f, 0.35f, 1f);
    public Color panelBg     = new Color(0.06f, 0.07f, 0.09f, 0.96f);
    public Color accentColor = new Color(0.25f, 0.95f, 0.95f, 1f);

    [Header("调试")]
    public bool logDebug = true;

    // ==================== 运行时 ====================
    private Font _font;

    private Canvas     _canvas;
    private CanvasGroup _badgeGroup;
    private Text       _badgeText;

    private GameObject _panelRoot;
    private Text       _panelText;

    private float _nextRefresh;
    private int   _lastDays = int.MinValue;
    private bool  _lastLicensed;
    private bool  _warnedOnce;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;

        if (transform.parent != null)
        {
            transform.SetParent(null);
            Debug.LogWarning($"[LicenseStatus] {name} 挂在子物体上，DontDestroyOnLoad 会失效。已自动提到根级。");
        }

        DontDestroyOnLoad(gameObject);

        ApplyConfig(Game_Config.Instance);

        _font = LoadBuiltinFont();

        BuildUI();
        Refresh();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        // ---- 定时刷新（每秒一次，很便宜）----
        if (Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + 1f;
            Refresh();
        }

        // ---- 快捷键 ----
        if (Input.GetKeyDown(toggleKey))
            TogglePanel();

        if (_panelRoot != null && _panelRoot.activeSelf &&
            allowEscToClose && Input.GetKeyDown(KeyCode.Escape))
        {
            TogglePanel(false);
        }
    }

    // ==================== 配置 ====================
    public void ApplyConfig(Game_Config cfg)
    {
        if (cfg == null) return;

        const string S = "App";

        showBadge = cfg.GetBool(S, "licenseBadge",    showBadge);
        warnDays  = Mathf.Max(0, cfg.GetInt(S, "licenseWarnDays", warnDays));

        if (logDebug && cfg.Loaded)
            Debug.Log($"[LicenseStatus] 配置：badge={showBadge} warnDays={warnDays} key={toggleKey}");
    }

    // ==================== 刷新 ====================
    private void Refresh()
    {
        bool licensed = Game_LicenseGate.IsLicensed;

        // ---- 未授权 ----
        if (!licensed)
        {
            SetBadge("未授权", errorColor);

            if (logDebug && !_warnedOnce)
            {
                Console_Warn("[LicenseStatus] 当前未授权");
                _warnedOnce = true;
            }

            return;
        }

        // ---- 计算剩余天数 ----
        int days = GetDaysLeft(out bool permanent);

        if (permanent)
        {
            HideBadge();

            if (logDebug && _lastDays != int.MaxValue)
                Console_Log("[LicenseStatus] 授权永久有效");

            _lastDays = int.MaxValue;
            return;
        }

        // ---- 状态变化时打日志 ----
        if (_lastDays != days || _lastLicensed != licensed)
        {
            _lastDays     = days;
            _lastLicensed = licensed;

            LogDays(days);
        }

        // ---- 角标 ----
        if (days < 0)
        {
            SetBadge("授权已过期", errorColor);
        }
        else if (days == 0)
        {
            SetBadge("授权今天到期", errorColor);
        }
        else if (days <= warnDays)
        {
            SetBadge($"授权剩余 {days} 天", days <= 7 ? errorColor : warnColor);
        }
        else
        {
            HideBadge();
        }
    }

    private void LogDays(int days)
    {
        if (!logDebug) return;

        var payload = Game_LicenseGate.Payload;
        string exp  = payload != null ? payload.exp : "?";
        string who  = payload != null && !string.IsNullOrEmpty(payload.id) ? payload.id : "未命名";

        if (days < 0)
        {
            Console_Warn($"[LicenseStatus] 授权已过期（{exp}），客户：{who}");
        }
        else if (days <= 7)
        {
            Console_Warn($"[LicenseStatus] 授权仅剩 {days} 天（{exp}），客户：{who} —— 请尽快续期");
        }
        else if (days <= warnDays)
        {
            Console_Warn($"[LicenseStatus] 授权剩余 {days} 天（{exp}），客户：{who}");
        }
        else if (logDebug)
        {
            Console_Log($"[LicenseStatus] 授权剩余 {days} 天（{exp}），客户：{who}");
        }
    }

    /// <summary>剩余天数（负数 = 已过期；permanent = 永久授权）</summary>
    public int GetDaysLeft(out bool permanent)
    {
        permanent = false;

        var payload = Game_LicenseGate.Payload;

        if (payload == null || string.IsNullOrEmpty(payload.exp))
        {
            permanent = true;
            return int.MaxValue;
        }

        if (!DateTime.TryParseExact(payload.exp.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None,
                out DateTime expire))
        {
            // 解析失败：当作永久，避免误报
            permanent = true;
            return int.MaxValue;
        }

        return (expire.Date - DateTime.UtcNow.Date).Days;
    }

    // ==================== 角标 ====================
    private void SetBadge(string text, Color color)
    {
        if (_badgeText == null) return;

        _badgeText.text  = text;
        _badgeText.color = color;

        // ★ 是否真的显示由 showBadge 决定
        if (_badgeGroup != null)
            _badgeGroup.alpha = showBadge ? 1f : 0f;
    }

    private void HideBadge()
    {
        if (_badgeGroup != null) _badgeGroup.alpha = 0f;
    }

    // ==================== 详情面板 ====================
    private void TogglePanel() => TogglePanel(_panelRoot == null || !_panelRoot.activeSelf);

    private void TogglePanel(bool show)
    {
        if (_panelRoot == null) return;

        _panelRoot.SetActive(show);

        if (show) RebuildPanelText();
    }

    private void RebuildPanelText()
    {
        if (_panelText == null) return;

        var payload = Game_LicenseGate.Payload;
        var sb = new StringBuilder();

        sb.AppendLine("授权信息");
        sb.AppendLine();

        if (!Game_LicenseGate.IsLicensed)
        {
            sb.AppendLine($"状态：<color=#FF6060>未授权</color>");
            sb.AppendLine();
            sb.AppendLine($"原因：{Game_License.StatusText(Game_LicenseGate.Status)}");
            sb.AppendLine(Game_LicenseGate.LastMessage);
        }
        else
        {
            int days = GetDaysLeft(out bool permanent);

            string statusText;
            string statusColor;

            if (permanent)      { statusText = "永久有效";          statusColor = "#5CFFA0"; }
            else if (days < 0)  { statusText = $"已过期 {-days} 天"; statusColor = "#FF6060"; }
            else if (days == 0) { statusText = "今天到期";          statusColor = "#FF6060"; }
            else if (days <= 7) { statusText = $"剩余 {days} 天";    statusColor = "#FF6060"; }
            else if (days <= warnDays) { statusText = $"剩余 {days} 天"; statusColor = "#FFC040"; }
            else                { statusText = $"剩余 {days} 天";    statusColor = "#5CFFA0"; }

            sb.AppendLine($"状态：<color={statusColor}>{statusText}</color>");
            sb.AppendLine();
            sb.AppendLine($"客户：{(string.IsNullOrEmpty(payload.id) ? "（未填写）" : payload.id)}");
            sb.AppendLine($"到期：{(permanent ? "永久" : payload.exp)}");
            sb.AppendLine();
            sb.AppendLine($"设备指纹：");
            sb.AppendLine($"<color=#9ADFFFFF>{Game_DeviceFingerprint.Get()}</color>");
            sb.AppendLine();
            sb.AppendLine($"授权文件：");
            sb.AppendLine($"<color=#9ADFFFFF>{Game_LicenseGate.Instance?.ActivePath ?? "?"}</color>");
        }

        sb.AppendLine();
        sb.AppendLine($"<color=#888888>按 {toggleKey} 关闭</color>");

        _panelText.text = sb.ToString();
    }

    // ==================== UI 生成 ====================
    private void BuildUI()
    {
        var canvasGo = new GameObject("LicenseStatusCanvas",
            typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

        canvasGo.transform.SetParent(transform, false);

        _canvas = canvasGo.GetComponent<Canvas>();
        _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 200;          // 低于 Game_ScreenFade(32000)，转场时会被盖住

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight  = 0.5f;

        BuildBadge(canvasGo.transform);
        BuildPanel(canvasGo.transform);
    }

    private void BuildBadge(Transform parent)
    {
        var go = new GameObject("Badge", typeof(RectTransform), typeof(Text), typeof(CanvasGroup));
        go.transform.SetParent(parent, false);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin        = new Vector2(1f, 0f);       // 右下角
        rt.anchorMax        = new Vector2(1f, 0f);
        rt.pivot            = new Vector2(1f, 0f);
        rt.anchoredPosition = badgeMargin;
        rt.sizeDelta        = new Vector2(320f, 40f);

        _badgeText = go.GetComponent<Text>();
        _badgeText.font               = _font;
        _badgeText.fontSize           = badgeFontSize;
        _badgeText.color              = normalColor;
        _badgeText.alignment          = TextAnchor.LowerRight;
        _badgeText.horizontalOverflow = HorizontalWrapMode.Overflow;
        _badgeText.verticalOverflow   = VerticalWrapMode.Overflow;
        _badgeText.supportRichText    = false;
        _badgeText.text               = "";
        _badgeText.raycastTarget      = false;

        _badgeGroup = go.GetComponent<CanvasGroup>();
        _badgeGroup.alpha          = 0f;      // 默认隐藏
        _badgeGroup.blocksRaycasts = false;
        _badgeGroup.interactable   = false;
    }

    private void BuildPanel(Transform parent)
    {
        // ---- 根（默认关）----
        _panelRoot = new GameObject("PanelRoot", typeof(RectTransform));
        _panelRoot.transform.SetParent(parent, false);

        var rootRt = _panelRoot.GetComponent<RectTransform>();
        rootRt.anchorMin = Vector2.zero;
        rootRt.anchorMax = Vector2.one;
        rootRt.offsetMin = Vector2.zero;
        rootRt.offsetMax = Vector2.zero;

        // ---- 半透明遮罩 ----
        var bg = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(_panelRoot.transform, false);

        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        var bgImg = bg.GetComponent<Image>();
        bgImg.color         = new Color(0f, 0f, 0f, 0.72f);
        bgImg.raycastTarget = true;

        // ---- 面板 ----
        var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(_panelRoot.transform, false);

        var pRt = panel.GetComponent<RectTransform>();
        pRt.anchorMin        = new Vector2(0.5f, 0.5f);
        pRt.anchorMax        = new Vector2(0.5f, 0.5f);
        pRt.pivot            = new Vector2(0.5f, 0.5f);
        pRt.anchoredPosition = Vector2.zero;
        pRt.sizeDelta        = new Vector2(900f, 640f);

        var pImg = panel.GetComponent<Image>();
        pImg.color = panelBg;

        // ---- 文本 ----
        var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textGo.transform.SetParent(panel.transform, false);

        var tRt = textGo.GetComponent<RectTransform>();
        tRt.anchorMin = Vector2.zero;
        tRt.anchorMax = Vector2.one;
        tRt.offsetMin = new Vector2(40f, 32f);
        tRt.offsetMax = new Vector2(-40f, -32f);

        _panelText = textGo.GetComponent<Text>();
        _panelText.font               = _font;
        _panelText.fontSize           = 22;
        _panelText.color              = normalColor;
        _panelText.alignment          = TextAnchor.UpperLeft;
        _panelText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _panelText.verticalOverflow   = VerticalWrapMode.Overflow;
        _panelText.supportRichText    = true;
        _panelText.lineSpacing        = 1.25f;
        _panelText.raycastTarget      = false;

        _panelRoot.SetActive(false);
    }

    private Font LoadBuiltinFont()
    {
        Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        if (f == null)
            f = Resources.GetBuiltinResource<Font>("Arial.ttf");

        if (f == null)
            Debug.LogWarning("[LicenseStatus] 找不到内置字体，文字可能不显示");

        return f;
    }

    // ==================== 日志包装 ====================
    private static void Console_Log(string msg)  => Debug.Log(msg);
    private static void Console_Warn(string msg) => Debug.LogWarning(msg);
}
