using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>池化对象实现这个接口，会被自动回调</summary>
public interface IGamePoolable
{
    void OnSpawned();     // 从池中取出、已 SetActive(true) 之后
    void OnDespawned();   // 归还之前、即将 SetActive(false) 之前
}

/// <summary>
/// 通用对象池：按 prefab 分桶，自动管理租借 / 归还 / 预生成。
///
/// 同一个物体上可以有多个 IGamePoolable 组件，全部都会被回调
/// （回调数组按实例缓存，避免每次 GetComponents 的 GC 分配）。
/// </summary>
public class Game_ObjectPool : MonoBehaviour
{
    [Header("上限")]
    [Tooltip("每个 prefab 最多缓存多少实例；超出后归还的对象直接销毁")]
    public int maxPerPrefab = 256;

    [Header("调试")]
    public bool logStats = false;

    private class Bucket
    {
        public GameObject prefab;
        public readonly Stack<GameObject> idle = new Stack<GameObject>();
        public int totalCreated;
        public int liveCount;

        /// <summary>实例ID → 该实例上的所有池化组件（缓存的数组，避免重复 GetComponents）</summary>
        public readonly Dictionary<int, IGamePoolable[]> poolableCache
            = new Dictionary<int, IGamePoolable[]>();
    }

    private readonly Dictionary<int, Bucket> _buckets = new Dictionary<int, Bucket>();  // prefabId → 桶
    private readonly Dictionary<int, Bucket> _owner   = new Dictionary<int, Bucket>();  // 实例Id → 所属桶

    private int _lastReportFrame = -1;

    // ==================== 取 ====================
    public GameObject Acquire(GameObject prefab, Vector3 pos, Quaternion rot)
    {
        if (prefab == null)
        {
            Debug.LogError("[ObjectPool] Acquire: prefab 为 null");
            return null;
        }

        var bucket = GetBucket(prefab);

        // 从空闲栈里取一个还活着的
        GameObject go = null;
        while (bucket.idle.Count > 0)
        {
            go = bucket.idle.Pop();
            if (go != null) break;
            go = null;                          // 被外部销毁了，继续找
        }

        // 栈空则新建
        bool justCreated = false;
        if (go == null)
        {
            go = Instantiate(prefab, transform);
            bucket.totalCreated++;
            justCreated = true;

            if (bucket.totalCreated > maxPerPrefab)
                Debug.LogWarning($"[ObjectPool] {prefab.name} 已创建 {bucket.totalCreated} 个，" +
                                 $"超过 maxPerPrefab({maxPerPrefab})。通常意味着有对象未归还。");
        }

        _owner[go.GetInstanceID()] = bucket;

        go.transform.SetParent(transform, false);
        go.transform.SetPositionAndRotation(pos, rot);
        go.SetActive(true);                     // 先激活（让 Awake/OnEnable 跑完），再通知
        bucket.liveCount++;

        // ★ 新创建时缓存回调数组，之后直接查表（无 GC）
        if (justCreated) CachePoolables(bucket, go);
        CallOnSpawned(bucket, go);

        if (logStats) ReportStats();
        return go;
    }

    /// <summary>泛型版本</summary>
    public T Acquire<T>(T prefab, Vector3 pos, Quaternion rot) where T : Component
    {
        var go = Acquire(prefab.gameObject, pos, rot);
        return go == null ? null : go.GetComponent<T>();
    }

    // ==================== 还 ====================
    public void Release(GameObject go)
    {
        if (go == null) return;

        int id = go.GetInstanceID();

        // ★ 先查桶，才能拿到缓存的回调数组
        _owner.TryGetValue(id, out var bucket);

        if (bucket != null) CallOnDespawned(bucket, go);
        else CallOnDespawnedUncached(go);        // 不是本池的对象，退化为直接查

        go.SetActive(false);

        if (bucket == null)
        {
            Destroy(go);                        // 不是本池管理的对象
            return;
        }

        if (bucket.liveCount > 0) bucket.liveCount--;

        if (bucket.idle.Count >= maxPerPrefab)
        {
            _owner.Remove(id);
            bucket.poolableCache.Remove(id);
            Destroy(go);
            return;
        }

        go.transform.SetParent(transform, false);
        bucket.idle.Push(go);
    }

    // ==================== 延迟归还 ====================
    /// <summary>
    /// 延迟 delay 秒后自动归还。用于"一次性特效"这类需要播完才回收的对象。
    /// 幂等：若对象在延迟期间已被手动归还，则不再重复归还。
    /// </summary>
    public void Release(GameObject go, float delay)
    {
        if (go == null) return;

        if (delay <= 0f) { Release(go); return; }

        StartCoroutine(ReleaseAfter(go, delay));
    }

    private IEnumerator ReleaseAfter(GameObject go, float delay)
    {
        yield return new WaitForSeconds(delay);

        // 期间可能已被回收（或整池被销毁）→ 跳过，避免 liveCount 被多减一次
        if (go == null || !go.activeSelf) yield break;

        Release(go);
    }

    // ==================== 预生成 ====================
    /// <summary>提前创建一批空闲实例，把运行中的 Instantiate 开销挪到加载阶段</summary>
    public void Prewarm(GameObject prefab, int count)
    {
        if (prefab == null || count <= 0) return;

        var bucket = GetBucket(prefab);

        for (int i = 0; i < count; i++)
        {
            if (bucket.totalCreated >= maxPerPrefab) break;

            var go = Instantiate(prefab, transform);
            go.name = prefab.name + "_pooled";
            go.SetActive(false);

            _owner[go.GetInstanceID()] = bucket;
            bucket.totalCreated++;

            CachePoolables(bucket, go);         // ★ 预生成时就把回调数组建好

            bucket.idle.Push(go);
        }

        if (logStats) ReportStats();
    }

    // ==================== 回调分发 ====================
    /// <summary>建立"实例 → 所有池化组件"的缓存</summary>
    private static void CachePoolables(Bucket bucket, GameObject go)
    {
        var arr = go.GetComponents<IGamePoolable>();
        bucket.poolableCache[go.GetInstanceID()] = arr;
    }

    private static void CallOnSpawned(Bucket bucket, GameObject go)
    {
        if (!bucket.poolableCache.TryGetValue(go.GetInstanceID(), out var arr) || arr == null)
        {
            // 缓存缺失（比如外部 Instantiate 的对象）→ 补建
            CachePoolables(bucket, go);
            bucket.poolableCache.TryGetValue(go.GetInstanceID(), out arr);
        }

        if (arr == null) return;

        for (int i = 0; i < arr.Length; i++)
            arr[i]?.OnSpawned();
    }

    private static void CallOnDespawned(Bucket bucket, GameObject go)
    {
        if (!bucket.poolableCache.TryGetValue(go.GetInstanceID(), out var arr) || arr == null)
        {
            CachePoolables(bucket, go);
            bucket.poolableCache.TryGetValue(go.GetInstanceID(), out arr);
        }

        if (arr == null) return;

        for (int i = 0; i < arr.Length; i++)
            arr[i]?.OnDespawned();
    }

    /// <summary>不属于本池的对象，退化为直接查询（会分配数组，但这种情况很少）</summary>
    private static void CallOnDespawnedUncached(GameObject go)
    {
        var arr = go.GetComponents<IGamePoolable>();
        for (int i = 0; i < arr.Length; i++)
            arr[i]?.OnDespawned();
    }

    // ==================== 查询 ====================
    public int GetLiveCount(GameObject prefab)
        => _buckets.TryGetValue(prefab.GetInstanceID(), out var b) ? b.liveCount : 0;

    public int GetIdleCount(GameObject prefab)
        => _buckets.TryGetValue(prefab.GetInstanceID(), out var b) ? b.idle.Count : 0;

    public void ReportStats()
    {
        if (Time.frameCount == _lastReportFrame) return;
        _lastReportFrame = Time.frameCount;

        foreach (var kv in _buckets)
        {
            var b = kv.Value;
            Debug.Log($"[ObjectPool] {b.prefab.name}: 借出={b.liveCount} 空闲={b.idle.Count} 总创建={b.totalCreated}");
        }
    }

    // ==================== 内部 ====================
    private Bucket GetBucket(GameObject prefab)
    {
        int id = prefab.GetInstanceID();
        if (!_buckets.TryGetValue(id, out var b))
        {
            b = new Bucket { prefab = prefab };
            _buckets.Add(id, b);
        }
        return b;
    }
}