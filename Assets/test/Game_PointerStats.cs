using UnityEngine;

/// <summary>
/// 指针输入统计：确认雷达驱动到底给了什么。
/// 每秒打印一次：touchCount、每个触点的位置、鼠标位置、左右分布。
///
/// 用途：判断我们是不是漏掉了 touch 分支。
/// </summary>
public class Game_PointerStats : MonoBehaviour
{
    [Header("分界（屏幕 x 比例，0.5 = 正中）")]
    [Range(0.1f, 0.9f)] public float splitRatio = 0.5f;

    [Header("调试")]
    public bool enable = true;

    private int _leftFrames;
    private int _rightFrames;
    private int _maxTouchCount;
    private int _frameCounter;
    private float _timer;

    private void Update()
    {
        if (!enable) return;

        _frameCounter++;

        if (Input.touchCount > _maxTouchCount)
            _maxTouchCount = Input.touchCount;

        // ---- 鼠标位置左右分布 ----
        if (Input.touchCount == 0)
        {
            if (Input.mousePosition.x < Screen.width * splitRatio) _leftFrames++;
            else                                                    _rightFrames++;
        }

        // ---- 每秒输出 ----
        _timer += Time.unscaledDeltaTime;

        if (_timer >= 1f)
        {
            _timer = 0f;
            Report();
        }
    }

    private void Report()
    {
        var sb = new System.Text.StringBuilder();

        sb.Append($"[PointerStats] touchCount={Input.touchCount} maxTouch={_maxTouchCount} ");
        sb.Append($"| mouse帧 左={_leftFrames} 右={_rightFrames} ");
        sb.Append($"| mousePos={Input.mousePosition}");

        Debug.Log(sb.ToString());

        // ---- 每个触点的详情 ----
        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch t = Input.GetTouch(i);

            Debug.Log($"[PointerStats]   touch[{i}] id={t.fingerId} {t.phase} " +
                      $"pos={t.position} delta={t.deltaPosition}");
        }

        _leftFrames = 0;
        _rightFrames = 0;
        _maxTouchCount = Input.touchCount;
        _frameCounter = 0;
    }
}