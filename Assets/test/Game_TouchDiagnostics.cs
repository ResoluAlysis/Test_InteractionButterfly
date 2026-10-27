using System.Text;
using UnityEngine;

/// <summary>
/// 触摸/指针诊断：在屏幕上实时显示 Input.touches 的原始状态。
/// 用来确认雷达驱动到底报告了什么。
///
/// 用法：挂在任意物体上，运行后看左上角。
///      按 toggleKey 开关。
/// </summary>
public class Game_TouchDiagnostics : MonoBehaviour
{
    [Header("开关")]
    public bool show = true;
    public KeyCode toggleKey = KeyCode.F8;

    [Header("位置")]
    public int x = 10;
    public int y = 10;
    public int fontSize = 22;

    [Header("历史统计")]
    [Tooltip("统计最近 N 帧出现过的不同 fingerId 数量（判断 id 是否稳定）")]
    public int historyFrames = 60;

    private GUIStyle _style;
    private readonly StringBuilder _sb = new StringBuilder(2048);

    // 历史统计
    private int[] _idFrames = new int[16];      // 每个 id 出现的帧数
    private int   _frameCounter;
    private int   _distinctIdsThisWindow;
    private float _windowStartTime;
    private int   _maxTouchCount;
    private int   _spawnEstimate;

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey)) show = !show;

        _frameCounter++;

        for (int i = 0; i < Input.touchCount; i++)
        {
            int id = Input.GetTouch(i).fingerId;
            if (id >= 0 && id < _idFrames.Length) _idFrames[id]++;
        }

        if (Input.touchCount > _maxTouchCount) _maxTouchCount = Input.touchCount;

        // 每 1 秒重置窗口统计
        if (Time.unscaledTime - _windowStartTime >= 1f)
        {
            _windowStartTime = Time.unscaledTime;

            int distinct = 0;
            for (int i = 0; i < _idFrames.Length; i++)
            {
                if (_idFrames[i] > 0) distinct++;
                _idFrames[i] = 0;
            }

            _distinctIdsThisWindow = distinct;
            _frameCounter = 0;
        }
    }

    private void OnGUI()
    {
        if (!show) return;

        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                richText = true,
                wordWrap = false,
            };
            _style.normal.textColor = Color.white;
        }

        _sb.Length = 0;

        _sb.AppendLine($"<b>touchCount = {Input.touchCount}</b>   (1秒内最大 = {_maxTouchCount})");
        _sb.AppendLine($"1秒内出现过的 fingerId 个数 = {_distinctIdsThisWindow}");
        _sb.AppendLine($"simulateMouseWithTouches = {Input.simulateMouseWithTouches}");
        _sb.AppendLine($"mousePresent = {Input.mousePresent}");
        _sb.AppendLine($"mousePosition = {Input.mousePosition}");
        _sb.AppendLine($"screen = {Screen.width}x{Screen.height}");
        _sb.AppendLine();

        if (Input.touchCount == 0)
        {
            _sb.AppendLine("<color=#FFAA00>（没有触摸点）</color>");
        }

        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch t = Input.GetTouch(i);

            string color = t.phase == TouchPhase.Began   ? "#5CFFA0" :
                           t.phase == TouchPhase.Moved   ? "#FFFFFF" :
                           t.phase == TouchPhase.Stationary ? "#9ADFFF" :
                           t.phase == TouchPhase.Ended   ? "#FF6060" : "#FF9060";

            _sb.AppendLine($"<color={color}>[{i}] id={t.fingerId}  {t.phase}</color>");
            _sb.AppendLine($"     pos={t.position}  delta={t.deltaPosition}  dt={t.deltaTime:F3}");
        }

        // 背景
        GUIContent content = new GUIContent(_sb.ToString());
        Vector2 size = _style.CalcSize(content);

        GUI.color = new Color(0f, 0f, 0f, 0.72f);
        GUI.DrawTexture(new Rect(x - 8, y - 8, size.x + 16, size.y + 16), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Label(new Rect(x, y, size.x + 20, size.y + 20), _sb.ToString(), _style);
    }
}