using UnityEngine;

/// <summary>
/// 可见性判断工具。每帧只取一次相机数据，供大量对象复用。
/// </summary>
public static class Game_FlockVisibility
{
    private static Camera  _cam;
    private static int     _camFrame = -1;
    private static float   _halfH;
    private static float   _halfW;
    private static Vector3 _camPos;

    private static void Refresh()
    {
        if (_camFrame == Time.frameCount) return;
        _camFrame = Time.frameCount;

        if (_cam == null) _cam = Camera.main;
        if (_cam == null)
        {
            // 没有相机时视为全部可见，避免把对象全部剔除
            _halfH = _halfW = float.MaxValue;
            _camPos = Vector3.zero;
            return;
        }

        _halfH = _cam.orthographicSize;
        _halfW = _halfH * _cam.aspect;
        _camPos = _cam.transform.position;
    }

    /// <summary>正交相机下的 AABB 可见性判断</summary>
    public static bool IsVisible(Vector3 worldPos, float padding)
    {
        Refresh();

        float dx = Mathf.Abs(worldPos.x - _camPos.x);
        if (dx > _halfW + padding) return false;

        float dy = Mathf.Abs(worldPos.y - _camPos.y);
        return dy <= _halfH + padding;
    }
}