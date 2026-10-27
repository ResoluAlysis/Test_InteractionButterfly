using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 激活场景的 UI：显示设备指纹 + 输入激活码 + 状态提示。
///
/// 【两种模式】
///   ① 预制体模式：Resources/ActivationUI（或手动拖 uiPrefab）存在时，直接实例化它。
///      布局、配色、字号全部在预制体里改，代码不再生成任何控件。
///   ② 代码兜底：没有预制体时，用代码生成一套（零搭建）。
///
/// 【制作预制体的流程】
///   1. Play 进入激活场景（此时走代码兜底，UI 已生成）
///   2. 菜单 Tools → License → 把激活UI保存为预制体
///   3. 停止 Play → 打开 Assets/Resources/ActivationUI.prefab 美化
///   4. 再次运行，自动使用预制体
///
/// 【重要】美化时不要改控件名字（代码按名字绑定）：
///   Title / Hint / FpLabel / FpRow / FpField / CopyBtn
///   LicLabel / LicenseField / BtnRow / SubmitBtn / QuitBtn / PasteBtn / Message
///
/// 场景：02_Activation
///   └── (空物体) → 挂 Game_ActivationUI
/// </summary>
public class Game_ActivationUI : MonoBehaviour
{
    // ==================== 预制体 ====================
    [Header("UI 预制体")]
    [Tooltip("留空则自动从 Resources 加载下面这个名字")]
    public GameObject uiPrefab;

    [Tooltip("Resources 下的预制体名（不含扩展名）")]
    public string uiPrefabResourceName = "ActivationUI";

    [Tooltip("用预制体时，把它的 Canvas 缩放模式改成参考分辨率（关掉则完全用预制体自己的设置）")]
    public bool forceCanvasScaler = false;

    // ==================== 外观（仅代码兜底时生效）====================
    [Header("外观（仅代码生成时生效）")]
    public Color backgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
    public Color panelColor       = new Color(0.10f, 0.11f, 0.14f, 1f);
    public Color accentColor      = new Color(0.25f, 0.95f, 0.95f, 1f);
    public Color errorColor       = new Color(1f, 0.35f, 0.35f, 1f);
    public Color okColor          = new Color(0.35f, 1f, 0.55f, 1f);
    public Color dimColor         = new Color(1f, 1f, 1f, 0.45f);

    [Header("布局（仅代码生成时生效）")]
    public Vector2 panelSize = new Vector2(960f, 620f);
    public int titleFontSize = 34;

    // ==================== 行为 ====================
    [Header("成功延迟")]
    [Tooltip("激活成功后延迟多久再进主场景（秒）。0 = 立刻切换")]
    public float successDelaySeconds = 2f;

    [Tooltip("延迟期间在消息里显示倒计时")]
    public bool showCountdown = true;

    [Tooltip("延迟期间禁用「激活」按钮，防止重复点击")]
    public bool lockSubmitDuringDelay = true;

    [Tooltip("延迟期间按任意键或点击可立即进入")]
    public bool allowSkipDelay = true;

    [Header("行为")]
    [Tooltip("激活成功后自动进入主场景")]
    public bool autoProceedOnSuccess = true;

    [Tooltip("允许按 Esc 退出")]
    public bool allowEscToQuit = true;

    [Header("调试")]
    public bool logDebug = true;

    // ==================== 运行时 ====================
    private Font _font;

    private InputField _licenseField;
    private InputField _fingerprintField;
    private Text       _messageText;
    private Button     _submitButton;

    private bool _busy;
    private bool _proceeding;      // 已确定要转场，忽略后续点击
    private bool _usingPrefab;

    /// <summary>当前是否在用预制体（调试用）</summary>
    public bool UsingPrefab => _usingPrefab;

    // ==================== 生命周期 ====================
    private void Start()
    {
        _font = LoadBuiltinFont();

        // ★ 激活界面需要鼠标操作 → 强制显示光标（覆盖 showCursor 的设定）
        Game_Config.Instance?.ForceCursorVisible();

        GameObject prefab = ResolvePrefab();

        if (prefab != null)
        {
            _usingPrefab = true;
            InstantiatePrefabUI(prefab);

            if (logDebug) Debug.Log($"[Activation] 使用预制体 UI：{prefab.name}");
        }
        else
        {
            _usingPrefab = false;
            BuildUI();

            if (logDebug)
                Debug.Log("[Activation] 没有预制体，使用代码生成的 UI\n" +
                          "（想美化：菜单 Tools → License → 把激活UI保存为预制体）");
        }

        RefreshStatus();
    }

    private void Update()
    {
        if (allowEscToQuit && Input.GetKeyDown(KeyCode.Escape))
            Quit();
    }

    private void OnDestroy()
    {
        // 离开激活场景 → 把光标可见性交还给 Game_Config 的配置
        Game_Config.Instance?.ReleaseCursor();
    }

    // ==================== 预制体 ====================
    private GameObject ResolvePrefab()
    {
        if (uiPrefab != null) return uiPrefab;

        if (!string.IsNullOrEmpty(uiPrefabResourceName))
        {
            GameObject p = Resources.Load<GameObject>(uiPrefabResourceName);

            if (p != null) return p;
        }

        return null;
    }

    private void InstantiatePrefabUI(GameObject prefab)
    {
        GameObject canvasGo = Instantiate(prefab, transform);
        canvasGo.name = "ActivationCanvas";

        if (forceCanvasScaler)
        {
            var scaler = canvasGo.GetComponent<CanvasScaler>();

            if (scaler != null)
            {
                scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight  = 0.5f;
            }
        }

        BindFromPrefab(canvasGo);
    }

    /// <summary>从预制体实例里按名字找回控件并绑定事件</summary>
    private void BindFromPrefab(GameObject root)
    {
        // ---- 指纹 ----
        _fingerprintField = FindComp<InputField>(root, "FpField");

        if (_fingerprintField != null)
        {
            _fingerprintField.readOnly = true;
            _fingerprintField.text     = Game_DeviceFingerprint.Get();
        }
        else
        {
            Debug.LogWarning("[Activation] 预制体里找不到 InputField \"FpField\"");
        }

        // ---- 激活码输入 ----
        _licenseField = FindComp<InputField>(root, "LicenseField");

        if (_licenseField == null)
            Debug.LogWarning("[Activation] 预制体里找不到 InputField \"LicenseField\"");

        // ---- 状态文本 ----
        _messageText = FindComp<Text>(root, "Message");

        if (_messageText == null)
            Debug.LogWarning("[Activation] 预制体里找不到 Text \"Message\"");

        // ---- 按钮 ----
        _submitButton = FindComp<Button>(root, "SubmitBtn");

        if (_submitButton != null) _submitButton.onClick.AddListener(OnSubmit);
        else Debug.LogWarning("[Activation] 预制体里找不到 Button \"SubmitBtn\"");

        var copyBtn = FindComp<Button>(root, "CopyBtn");
        if (copyBtn != null) copyBtn.onClick.AddListener(OnCopyFingerprint);

        var pasteBtn = FindComp<Button>(root, "PasteBtn");
        if (pasteBtn != null) pasteBtn.onClick.AddListener(OnPaste);

        var quitBtn = FindComp<Button>(root, "QuitBtn");
        if (quitBtn != null) quitBtn.onClick.AddListener(Quit);
    }

    private static T FindComp<T>(GameObject root, string name) where T : Component
    {
        if (root == null) return null;

        T[] all = root.GetComponentsInChildren<T>(true);

        for (int i = 0; i < all.Length; i++)
            if (all[i].name == name) return all[i];

        return null;
    }

    // ==================== 字体 ====================
    private Font LoadBuiltinFont()
    {
        // Unity 2022 里 Arial.ttf 被改名为 LegacyRuntime.ttf
        Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        if (f == null)
            f = Resources.GetBuiltinResource<Font>("Arial.ttf");

        if (f == null)
            Debug.LogWarning("[Activation] 找不到内置字体，文字可能不显示");

        return f;
    }

    // ==================== 代码生成 UI ====================
    private void BuildUI()
    {
        // ---- Canvas ----
        var canvasGo = new GameObject("ActivationCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight  = 0.5f;

        // ---- 全屏背景 ----
        var bg = CreateImage(canvasGo.transform, "Background", backgroundColor);
        Stretch(bg.rectTransform);

        // ---- 居中面板 ----
        var panel = CreateImage(canvasGo.transform, "Panel", panelColor);

        var panelRt = panel.rectTransform;
        panelRt.anchorMin        = new Vector2(0.5f, 0.5f);
        panelRt.anchorMax        = new Vector2(0.5f, 0.5f);
        panelRt.pivot            = new Vector2(0.5f, 0.5f);
        panelRt.anchoredPosition = Vector2.zero;
        panelRt.sizeDelta        = panelSize;

        var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding                = new RectOffset(48, 48, 40, 40);
        layout.spacing                = 14f;
        layout.childControlWidth      = true;
        layout.childControlHeight     = false;
        layout.childForceExpandWidth  = true;
        layout.childForceExpandHeight = false;
        layout.childAlignment         = TextAnchor.UpperLeft;

        // ---- 标题 ----
        CreateLabel(panel.transform, "Title", "本设备未授权", titleFontSize, accentColor, 44f);

        CreateLabel(panel.transform, "Hint",
            "请把下面的设备指纹发送给供应商，获取激活码...",
            17, dimColor, 56f);

        // ---- 指纹行 ----
        CreateLabel(panel.transform, "FpLabel", "设备指纹", 18, Color.white, 26f);

        var fpRow = CreateRow(panel.transform, "FpRow", 44f);

        var fpField = CreateInputField(fpRow.transform, "FpField", false, 20);
        fpField.readOnly = true;
        fpField.text     = Game_DeviceFingerprint.Get();
        _fingerprintField = fpField;

        var copyBtn = CreateButton(fpRow.transform, "CopyBtn", "复 制", 90f);
        copyBtn.onClick.AddListener(OnCopyFingerprint);

        // ---- 激活码 ----
        CreateLabel(panel.transform, "LicLabel", "激活码", 18, Color.white, 26f);

        _licenseField = CreateInputField(panel.transform, "LicenseField", true, 16);
        _licenseField.text = "";

        var le = _licenseField.gameObject.GetComponent<LayoutElement>();
        if (le != null) le.preferredHeight = 130f;

        // ---- 按钮行 ----
        var btnRow = CreateRow(panel.transform, "BtnRow", 52f);

        _submitButton = CreateButton(btnRow.transform, "SubmitBtn", "激 活", 160f);
        _submitButton.onClick.AddListener(OnSubmit);

        var pasteBtn = CreateButton(btnRow.transform, "PasteBtn", "从 剪 贴 板 粘 贴", 180f);
        pasteBtn.onClick.AddListener(OnPaste);

        var quitBtn = CreateButton(btnRow.transform, "QuitBtn", "退 出", 120f);
        quitBtn.onClick.AddListener(Quit);

        // ---- 状态消息 ----
        _messageText = CreateLabel(panel.transform, "Message", "", 17, dimColor, 80f);
        _messageText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _messageText.verticalOverflow   = VerticalWrapMode.Overflow;
    }

    // ==================== 交互 ====================
    private void OnCopyFingerprint()
    {
        string fp = Game_DeviceFingerprint.Get();
        GUIUtility.systemCopyBuffer = fp;

        SetMessage($"指纹已复制：{fp}", false);
    }

    private void OnPaste()
    {
        string clip = GUIUtility.systemCopyBuffer;

        if (string.IsNullOrEmpty(clip))
        {
            SetMessage("剪贴板是空的...", true);
            return;
        }

        if (_licenseField != null)
            _licenseField.text = clip.Trim();

        SetMessage("已从剪贴板粘贴", false);
    }

    private void OnSubmit()
    {
        if (_busy || _proceeding) return;

        string code = _licenseField != null ? _licenseField.text.Trim() : "";

        if (string.IsNullOrEmpty(code))
        {
            SetMessage("请先粘贴激活码...", true);
            return;
        }

        var gate = Game_LicenseGate.Instance;

        if (gate == null)
        {
            // 没有 Gate：只做本地验证，不保存（方便单独测试激活场景）
            var st = Game_License.Validate(code, out _, out string msg);

            if (st == LicenseStatus.Valid)
                SetMessage($"验证通过，但场景里没有 Game_LicenseGate，无法保存。\n{msg}", false);
            else
                SetMessage($"{Game_License.StatusText(st)}\n{msg}", true);

            return;
        }

        _busy = true;
        SetMessage("正在验证...", false);

        bool ok = gate.SaveAndValidate(code, out LicenseStatus status, out string message);

        _busy = false;

        if (ok)
        {
            SetMessage(message, false);

            if (logDebug) Debug.Log($"[Activation] {message}");

            if (!autoProceedOnSuccess) return;

            if (successDelaySeconds > 0f)
                StartCoroutine(DelayedProceed(gate, message));
            else
                gate.GoToMain();
        }
        else
        {
            SetMessage($"{Game_License.StatusText(status)}\n{message}", true);

            if (logDebug) Debug.LogWarning($"[Activation] 激活失败：{status} / {message}");
        }
    }

    /// <summary>激活成功后延迟转场，让用户看清成功提示</summary>
    private IEnumerator DelayedProceed(Game_LicenseGate gate, string baseMessage)
    {
        _proceeding = true;

        if (lockSubmitDuringDelay && _submitButton != null)
            _submitButton.interactable = false;

        float remain = successDelaySeconds;

        while (remain > 0f)
        {
            if (showCountdown)
                SetMessage($"{baseMessage}\n\n{Mathf.CeilToInt(remain)} 秒后进入主场景…", false);

            // 任意键 / 触摸 / 点击 可跳过等待
            if (allowSkipDelay &&
                (Input.anyKeyDown || Input.touchCount > 0 || Input.GetMouseButtonDown(0)))
            {
                break;
            }

            remain -= Time.unscaledDeltaTime;
            yield return null;
        }

        SetMessage(baseMessage, false);

        if (logDebug) Debug.Log("[Activation] 延迟结束，进入主场景");

        gate?.GoToMain();
    }

    private void Quit()
    {
        Debug.Log("[Activation] 退出程序");
        Application.Quit();

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    // ==================== 状态显示 ====================
    private void RefreshStatus()
    {
        var gate = Game_LicenseGate.Instance;

        if (gate != null && Game_LicenseGate.IsLicensed)
        {
            SetMessage($"当前已授权：{Game_LicenseGate.LastMessage}", false);
            return;
        }

        if (gate != null && !string.IsNullOrEmpty(Game_LicenseGate.LastMessage))
        {
            SetMessage($"上次校验：{Game_License.StatusText(Game_LicenseGate.Status)}\n" +
                       Game_LicenseGate.LastMessage, true);
            return;
        }

        SetMessage("请粘贴激活码后点击「激活」", false);
    }

    private void SetMessage(string text, bool isError)
    {
        if (_messageText == null) return;

        _messageText.text  = text ?? "";
        _messageText.color = isError ? errorColor : okColor;
    }

    // ==================== UI 构造工具 ====================
    private static RectTransform Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return rt;
    }

    private static Image CreateImage(Transform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);

        var img = go.GetComponent<Image>();
        img.color = color;

        return img;
    }

    private Text CreateLabel(Transform parent, string name, string content,
                             int fontSize, Color color, float height)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = height;

        var text = go.GetComponent<Text>();
        text.font               = _font;
        text.fontSize           = fontSize;
        text.color              = color;
        text.text               = content;
        text.alignment          = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow   = VerticalWrapMode.Overflow;
        text.supportRichText    = false;

        return text;
    }

    private RectTransform CreateRow(Transform parent, string name, float height)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = height;

        var layout = go.AddComponent<HorizontalLayoutGroup>();
        layout.spacing                = 10f;
        layout.childControlWidth      = true;
        layout.childControlHeight     = true;
        layout.childForceExpandWidth  = false;
        layout.childForceExpandHeight = true;
        layout.childAlignment         = TextAnchor.MiddleLeft;

        return go.GetComponent<RectTransform>();
    }

    private InputField CreateInputField(Transform parent, string name, bool multiline, int fontSize)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
        go.transform.SetParent(parent, false);

        var bg = go.GetComponent<Image>();
        bg.color = new Color(0.04f, 0.05f, 0.06f, 1f);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = multiline ? 130f : 44f;
        le.flexibleWidth   = 1f;

        // ---- 文本 ----
        var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textGo.transform.SetParent(go.transform, false);

        var textRt = Stretch(textGo.GetComponent<RectTransform>());
        textRt.offsetMin = new Vector2(10f, 6f);
        textRt.offsetMax = new Vector2(-10f, -6f);

        var text = textGo.GetComponent<Text>();
        text.font               = _font;
        text.fontSize           = fontSize;
        text.color              = Color.white;
        text.supportRichText    = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow   = VerticalWrapMode.Truncate;
        text.alignment          = multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;

        // ---- 占位符 ----
        var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
        phGo.transform.SetParent(go.transform, false);

        var phRt = Stretch(phGo.GetComponent<RectTransform>());
        phRt.offsetMin = new Vector2(10f, 6f);
        phRt.offsetMax = new Vector2(-10f, -6f);

        var ph = phGo.GetComponent<Text>();
        ph.font               = _font;
        ph.fontSize           = fontSize;
        ph.color              = new Color(1f, 1f, 1f, 0.28f);
        ph.text               = multiline ? "在这里粘贴激活码…" : "";
        ph.alignment          = multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
        ph.supportRichText    = false;
        ph.horizontalOverflow = HorizontalWrapMode.Wrap;

        // ---- InputField ----
        var input = go.GetComponent<InputField>();
        input.targetGraphic  = bg;
        input.textComponent  = text;
        input.placeholder    = ph;

        input.lineType = multiline ? InputField.LineType.MultiLineNewline
                                   : InputField.LineType.SingleLine;

        input.customCaretColor = true;
        input.caretColor       = accentColor;
        input.selectionColor   = new Color(0.25f, 0.95f, 0.95f, 0.35f);

        return input;
    }

    private Button CreateButton(Transform parent, string name, string label, float width)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth  = width;
        le.preferredHeight = 52f;
        le.flexibleWidth   = 0f;

        var bg = go.GetComponent<Image>();
        bg.color = new Color(0.16f, 0.18f, 0.22f, 1f);

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textGo.transform.SetParent(go.transform, false);

        Stretch(textGo.GetComponent<RectTransform>());

        var text = textGo.GetComponent<Text>();
        text.font            = _font;
        text.fontSize        = 20;
        text.color           = Color.white;
        text.text            = label;
        text.alignment       = TextAnchor.MiddleCenter;
        text.supportRichText = false;

        var btn = go.GetComponent<Button>();
        btn.targetGraphic = bg;

        return btn;
    }
}
