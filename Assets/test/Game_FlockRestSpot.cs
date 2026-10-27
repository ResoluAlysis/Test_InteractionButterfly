using UnityEngine;

/// <summary>
/// 栖枝点：手工摆放在花 / 树枝上。
///
/// 运行时向 Game_FlockInterest 注册一个永久 POI：
///   游荡半径极小 → 蝴蝶在花上悬停
///   扇翅频率降低 → 视觉上"停下来休息"
///
/// 受惊时蝴蝶自然飞走（IsEscaping 优先于 POI），无需额外处理。
/// 蝴蝶飞出屏幕被回收时，OnDespawned 会自动从跟随列表移除，不会出错。
/// </summary>
public class Game_FlockRestSpot : MonoBehaviour
{
    [Header("悬停")]
    [Tooltip("悬停半径。越小越像停住，建议 0.2~0.5")]
    public float hoverRadius = 0.3f;

    [Tooltip("扇翅频率倍率。1 = 正常，0.4 = 慢扇（看起来在休息）")]
    [Range(0.1f, 1f)] public float beatMul = 0.4f;

    [Header("吸引")]
    [Tooltip("吸引半径。要和相邻栖枝拉开距离，否则会抢蝴蝶")]
    public float attractRadius = 5f;

    [Header("容量")]
    [Tooltip("同时最多停几只。0 = 不限")]
    public int maxOccupants = 2;

    [Header("摆放校验")]
    [Tooltip("Start 时检查是否和已注册的栖枝范围重叠，重叠会警告")]
    public bool warnOnOverlap = true;
    
    [Header("定时轮换")]
    [Tooltip("最多停多久（秒）。到点后蝴蝶主动飞走。0 = 不轮换（会一直停着）")]
    public float restDuration = 8f;

    [Header("调试")]
    public bool showGizmos = true;

    private Game_FlockInterest.Point _point;

    private void Start()
    {
        var interest = Game_FlockInterest.Instance;
        if (interest == null)
        {
            Debug.LogWarning($"[RestSpot] {name} 找不到 Game_FlockInterest，栖枝不生效");
            enabled = false;
            return;
        }

        if (warnOnOverlap && interest.IsOverlapping(transform.position, attractRadius))
            Debug.LogWarning($"[RestSpot] {name} @ {transform.position} 与已有栖枝范围重叠，" +
                             "会互相抢蝴蝶。建议拉开距离或调小 attractRadius");

        _point = interest.AddRestSpot(
            transform.position,
            hoverRadius,
            beatMul,
            attractRadius,
            maxOccupants,
            restDuration);
    }

    private void OnDestroy()
    {
        if (_point != null && Game_FlockInterest.Instance != null)
            Game_FlockInterest.Instance.RemovePoint(_point);
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!showGizmos) return;

        // 吸引范围（青，半透明）
        Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.25f);
        Gizmos.DrawWireSphere(transform.position, attractRadius);

        // 悬停范围（绿）
        Gizmos.color = new Color(0.2f, 1f, 0.6f, 0.9f);
        Gizmos.DrawWireSphere(transform.position, hoverRadius);

        // 中心点
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, 0.35f);
        
        UnityEditor.Handles.color = Color.white;
        UnityEditor.Handles.Label(
            (Vector2)transform.position + Vector2.up * 0.9f,
            $"停 {restDuration:F0}s / 容 {maxOccupants}");
        
    }
#endif
}