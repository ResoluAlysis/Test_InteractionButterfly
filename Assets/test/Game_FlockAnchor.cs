using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 集群共享参考点。每只蝴蝶的路点 = 本锚点 Center + 自己的随机偏移，
/// 所以锚点漂移会让整群一起"流动"，而个体之间仍然散开。
/// </summary>
public class Game_FlockAnchor : MonoBehaviour
{
    // ==================== 漂移（大盒） ====================
    [Header("集群漂移范围（大盒）")]
    public Vector2 driftBox = new Vector2(25f, 15f);

    [Header("漂移节奏")]
    public float retargetInterval = 10f;
    [Tooltip("趋向新中心的移动速度。越小越慢，越大越像迁徙")]
    public float moveSpeed = 0.5f;

    // ==================== 个体散布（小盒） ====================
    [Header("个体散布范围（小盒）")]
    public Vector2 spawnBox = new Vector2(6f, 4f);

    [Header("被触摸推开")]
    [Tooltip("锚点被推开的距离")]
    public float nudgeDistance = 5f;
    [Tooltip("推开后推迟重采样的秒数")]
    public float nudgeDelay = 5f;

    /// <summary>集群当前的共享中心</summary>
    public Vector3 Center { get; private set; }

    private Vector3 _anchorBase;
    private Vector3 _goal;
    private float   _timer;
    private float   _resampleDelay;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        _anchorBase = transform.position;
        Center = _anchorBase;
        PickNewGoal();
    }

    private void Update()
    {
        Center = Vector3.MoveTowards(Center, _goal, moveSpeed * Time.deltaTime);

        if (_resampleDelay > 0f)
        {
            _resampleDelay -= Time.deltaTime;
            return;
        }

        _timer += Time.deltaTime;
        if (_timer < retargetInterval) return;

        _timer = 0f;
        PickNewGoal();
    }

    private void PickNewGoal()
    {
        _goal = _anchorBase + new Vector3(
            Random.Range(-driftBox.x, driftBox.x),
            Random.Range(-driftBox.y, driftBox.y),
            0f);
    }

    // ==================== 对外接口 ====================
    /// <summary>在锚点周围抽一个点（个体游荡用）</summary>
    public Vector2 GetPointAround(float bx, float by)
    {
        return new Vector2(
            Center.x + Random.Range(-bx, bx),
            Center.y + Random.Range(-by, by));
    }

    /// <summary>在锚点周围按面积均匀撒点（生成用）</summary>
    public Vector2 GetSpawnPoint(float radius)
    {
        float r = radius * Mathf.Sqrt(Random.value);   // sqrt 保证面积均匀
        float a = Random.value * Mathf.PI * 2f;
        return new Vector2(
            Center.x + Mathf.Cos(a) * r,
            Center.y + Mathf.Sin(a) * r);
    }

    /// <summary>被触摸时把整群推开</summary>
    public void Nudge(Vector2 source)
    {
        Vector2 away = (Vector2)Center - source;
        if (away.sqrMagnitude < 1e-4f) away = Random.insideUnitCircle.normalized;

        Vector3 delta = (Vector3)(away.normalized * nudgeDistance);
        Center += delta;
        _goal  += delta;

        _resampleDelay = nudgeDelay;
    }

    // ==================== 调试 ====================
    private void OnDrawGizmosSelected()
    {
        Vector3 c = Application.isPlaying ? Center : transform.position;

        // 大盒：漂移范围
        Gizmos.color = new Color(0f, 1f, 1f, 0.25f);
        Gizmos.DrawWireCube(transform.position,
            new Vector3(driftBox.x * 2f, driftBox.y * 2f, 0f));

        // 小盒：个体散布范围（居中在 Center）
        Gizmos.color = new Color(1f, 0.6f, 0f, 0.6f);
        Gizmos.DrawWireCube(c,
            new Vector3(spawnBox.x * 2f, spawnBox.y * 2f, 0f));   // ← 原来误用了 driftBox

        // 当前中心 → 目标
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(c, 0.3f);
        if (Application.isPlaying) Gizmos.DrawLine(c, _goal);
    }
}