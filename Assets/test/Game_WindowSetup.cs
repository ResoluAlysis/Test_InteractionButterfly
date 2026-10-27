using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// 窗口配置：无边框 / 置顶 / 自定义位置与尺寸。
/// 仅在 Windows 独立程序生效（编辑器与其它平台直接跳过）。
///
/// 对应目标工程的 BorderConfig + NotBorder，但修正了：
///   - 用 FindWindow(productName) 精确定位自己的窗口，而不是 GetForegroundWindow
///   - 用标准 WS_POPUP 样式 + SWP_FRAMECHANGED
///   - 退出时恢复原样式
/// </summary>
public class Game_WindowSetup : MonoBehaviour, IConfigurable
{
    [Header("启用")] [Tooltip("启动时应用窗口设置")] public bool enableOnPlay = true;

    [Tooltip("退出时恢复原始窗口样式")] public bool restoreOnQuit = true;

    [Header("样式")] public bool borderless = true;
    public bool topmost = false;

    [Header("位置与尺寸")] [Header("位置与尺寸")] [Tooltip("关掉则不用下面的 width/height")]
    public bool useCustomSize = true;

    [Tooltip("useCustomSize 关掉时：勾上则自适应主显示器（无边框全屏），否则保持当前窗口大小")]
    public bool autoFitScreen = true;

    public int posX = 0;
    public int posY = 0;
    public int width = 3840;
    public int height = 1080;

    [Header("安全")] [Tooltip("勾上：尺寸超过显示器时自动裁到屏幕内。大屏拼接装置请关掉")]
    public bool clampToScreen = false;

    [Header("调试")] public bool logDebug = true;

    [Header("热重载")] [Tooltip("按 F5 重载配置时是否同时重新应用窗口设置")]
    public bool reapplyOnHotReload = true;

    private bool _applying;

    // ==================== Win32 ====================
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern System.IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern System.IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(System.IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(System.IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr after,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
#endif

    // ==================== 运行时 ====================
    private System.IntPtr _hwnd = System.IntPtr.Zero;
    private int _origStyle;
    private int _origExStyle;
    private bool _styleSaved;

    private void Start()
    {
        ApplyConfig(Game_Config.Instance);

        // ★ 订阅热重载
        var cfg = Game_Config.Instance;
        if (cfg != null) cfg.Reloaded += HandleConfigReloaded;

        if (enableOnPlay) Apply();
    }

    private void OnApplicationQuit()
    {
        if (restoreOnQuit) Restore();
    }

    private void OnDestroy()
    {
        var cfg = Game_Config.Instance;
        if (cfg != null) cfg.Reloaded -= HandleConfigReloaded;

        if (restoreOnQuit) Restore();
    }

    // ==================== 配置 ====================
    public void ApplyConfig(Game_Config cfg)
    {
        if (cfg == null) return;

        const string S = "Window";

        borderless = cfg.GetBool(S, "borderless", borderless);
        topmost = cfg.GetBool(S, "topmost", topmost);
        useCustomSize = cfg.GetBool(S, "useCustomSize", useCustomSize);
        posX = cfg.GetInt(S, "posX", posX);
        posY = cfg.GetInt(S, "posY", posY);
        width = cfg.GetInt(S, "width", width);
        height = cfg.GetInt(S, "height", height);
        clampToScreen = cfg.GetBool(S, "clampToScreen", clampToScreen);
    }

    /// <summary>配置热重载后重新应用窗口设置</summary>
    private void HandleConfigReloaded()
    {
        if (!reapplyOnHotReload) return;

        if (logDebug) Debug.Log("[Window] 配置已重载 → 重新应用窗口设置");

        Apply();
    }

    // ==================== 应用 ====================
    public void Apply()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (Application.isEditor)
        {
            if (logDebug) Debug.Log("[Window] 编辑器内跳过窗口操作（避免改动 Editor 窗口）");
            return;
        }

        if (_applying)
        {
            if (logDebug) Debug.Log("[Window] 上一次应用尚未完成，本次忽略");
            return;
        }

        StartCoroutine(ApplyRoutine());
#else
        if (logDebug) Debug.Log("[Window] 非 Windows 平台，跳过");
#endif
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN

    private static readonly System.IntPtr HWND_TOPMOST = new System.IntPtr(-1);
    private static readonly System.IntPtr HWND_NOTOPMOST = new System.IntPtr(-2);

    private const uint SWP_NOACTIVATE = 0x0010;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private IEnumerator ApplyRoutine()
    {
        _applying = true;

        try
        {
            bool fitScreen = !useCustomSize && autoFitScreen;

            // ==================== 1. 设分辨率 ====================
            if (fitScreen)
            {
                int w = Display.main.systemWidth;
                int h = Display.main.systemHeight;

                // 先退出全屏，清掉残留状态
                if (Screen.fullScreenMode != FullScreenMode.Windowed)
                {
                    Screen.fullScreenMode = FullScreenMode.Windowed;
                    yield return null;
                }

                Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);

                if (logDebug) Debug.Log($"[Window] SetResolution({w}, {h}, FullScreenWindow)");

                yield return new WaitForSeconds(0.3f);
            }
            else if (useCustomSize)
            {
                int w = width, h = height;

                if (clampToScreen)
                {
                    int sw = GetSystemMetrics(SM_CXSCREEN);
                    int sh = GetSystemMetrics(SM_CYSCREEN);
                    if (w > sw) w = sw;
                    if (h > sh) h = sh;
                }

                if (Screen.fullScreenMode != FullScreenMode.Windowed)
                {
                    Screen.fullScreenMode = FullScreenMode.Windowed;
                    yield return null;
                }

                Screen.SetResolution(w, h, FullScreenMode.Windowed);

                if (logDebug) Debug.Log($"[Window] SetResolution({w}, {h}, Windowed)");

                yield return new WaitForSeconds(0.3f);
            }
            else if (logDebug)
            {
                Debug.Log($"[Window] 保持当前 {Screen.width}x{Screen.height}（不改分辨率）");
            }

            // ==================== 2. 窗口句柄 ====================
            _hwnd = FindWindow(null, Application.productName);
            if (_hwnd == System.IntPtr.Zero) _hwnd = GetActiveWindow();

            if (_hwnd == System.IntPtr.Zero)
            {
                Debug.LogError("[Window] 拿不到窗口句柄，样式设置放弃");
                yield break;
            }

            // ==================== 3. 保存原样式 ====================
            if (!_styleSaved)
            {
                _origStyle = GetWindowLong(_hwnd, GWL_STYLE);
                _origExStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
                _styleSaved = true;

                if (logDebug)
                    Debug.Log($"[Window] 原样式 style=0x{_origStyle:X8} exStyle=0x{_origExStyle:X8}");
            }

            // ==================== 4 & 5. 样式与位置 ====================
            if (fitScreen)
            {
                // ★ 全屏：只处理 topmost，绝不碰 WS_STYLE、绝不动尺寸
                SetWindowPos(_hwnd,
                    topmost ? HWND_TOPMOST : HWND_NOTOPMOST,
                    0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                if (logDebug)
                    Debug.Log($"[Window] FullScreenWindow：样式/尺寸交给 Unity，topmost={topmost}");
            }
            else
            {
                int style = _origStyle;
                int exStyle = _origExStyle;

                if (borderless) style = WS_POPUP | WS_VISIBLE;

                if (topmost) exStyle |= WS_EX_TOPMOST;
                else exStyle &= ~WS_EX_TOPMOST;

                SetWindowLong(_hwnd, GWL_STYLE, style);
                SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);

                if (useCustomSize)
                {
                    int x = posX, y = posY, w = width, h = height;

                    if (clampToScreen)
                    {
                        int sw = GetSystemMetrics(SM_CXSCREEN);
                        int sh = GetSystemMetrics(SM_CYSCREEN);
                        if (w > sw) w = sw;
                        if (h > sh) h = sh;
                    }

                    bool ok = SetWindowPos(_hwnd, System.IntPtr.Zero, x, y, w, h,
                        SWP_FRAMECHANGED | SWP_SHOWWINDOW);

                    if (logDebug)
                        Debug.Log(ok
                            ? $"[Window] SetWindowPos({x}, {y}, {w}, {h})"
                            : "[Window] SetWindowPos 返回 false");
                }
                else
                {
                    SetWindowPos(_hwnd, System.IntPtr.Zero, 0, 0, 0, 0,
                        SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE);

                    if (logDebug) Debug.Log("[Window] 保持当前尺寸，仅刷新边框");
                }
            }

            yield return null;

            if (logDebug)
                Debug.Log($"[Window] 完成。Screen={Screen.width}x{Screen.height}，" +
                          $"mode={Screen.fullScreenMode}，" +
                          $"display={Display.main.systemWidth}x{Display.main.systemHeight}");
        }
        finally
        {
            _applying = false;
        }
    }
#endif
#endif

    // ==================== 恢复 ====================
    public void Restore()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (Application.isEditor) return;
        if (!_styleSaved || _hwnd == System.IntPtr.Zero) return;

        SetWindowLong(_hwnd, GWL_STYLE, _origStyle);
        SetWindowLong(_hwnd, GWL_EXSTYLE, _origExStyle);
        SetWindowPos(_hwnd, System.IntPtr.Zero, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE);

        _styleSaved = false;

        if (logDebug) Debug.Log("[Window] 已恢复原窗口样式");
#endif
    }

    // ==================== 调试 ====================
    private void OnDrawGizmosSelected()
    {
        if (!useCustomSize) return;

        Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.6f);
        // 只是示意：在场景原点画一个对应屏幕比例的框
        Gizmos.DrawWireCube(Vector3.zero,
            new Vector3(width / 100f, height / 100f, 0f));
    }
}