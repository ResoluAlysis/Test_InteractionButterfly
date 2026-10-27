using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 蝴蝶外形提供者：配置可用的 prefab 及权重，供 Game_FlockManager 随机挑选。
///
/// 做成 ScriptableObject 的目的：美术换一套蝴蝶只需换一个资产，不改任何代码。
/// 通过菜单 Assets → Create → Flock → Butterfly Provider 创建。
/// </summary>
[CreateAssetMenu(fileName = "ButterflyProvider", menuName = "Flock/Butterfly Provider", order = 0)]
public class Game_ButterflyProvider : ScriptableObject
{
    public enum PickMode
    {
        Random,             // 纯随机（每次独立抽，比例可能偏离）
        WeightedNoRepeat,   // 按权重配额轮转（比例精确贴合权重）
    }

    [System.Serializable]
    public class Entry
    {
        [Tooltip("蝴蝶 prefab。必须挂 Game_FlockChild_Test")]
        public GameObject prefab;

        [Tooltip("相对权重。可以是任意正数，会归一化处理")]
        [Min(0f)] public float weight = 1f;

        [Tooltip("仅用于 Inspector 辨认（比如 '黄翅'、'蓝斑'）")]
        public string label = "";

        // 运行时统计
        [System.NonSerialized] public int spawnedCount;
        [System.NonSerialized] public float quota;      // 归一化配额（NoRepeat 模式用）
    }

    [Header("外形列表")]
    public List<Entry> entries = new List<Entry>();

    [Header("选择策略")]
    public PickMode mode = PickMode.WeightedNoRepeat;

    [Tooltip("NoRepeat 模式下，配额全部用完后是否自动重新开始一轮")]
    public bool restartRoundWhenExhausted = true;

    // ==================== 运行时缓存 ====================
    private bool  _cacheValid;
    private float _totalWeight;
    private int   _totalQuota;          // 一轮总共要发放多少个

    // ==================== 查询 ====================
    /// <summary>可用条目数（prefab 非空）</summary>
    public int ValidCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i] != null && entries[i].prefab != null) n++;
            return n;
        }
    }

    /// <summary>计算一轮的总数量（按权重比例，用于预生成分配）</summary>
    public int GetTotalQuota(int sampleCount)
    {
        EnsureCache();
        if (_totalWeight <= 0f) return 0;

        // 以 sampleCount 为基准，权重最大的那个占满
        int total = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e == null || e.prefab == null) continue;
            total += Mathf.Max(1, Mathf.RoundToInt(sampleCount * (e.weight / _totalWeight)));
        }
        return total;
    }

    // ==================== 挑选 ====================
    /// <summary>随机挑一个 prefab。全部无效时返回 null</summary>
    public GameObject Pick()
    {
        if (ValidCount == 0) return null;

        if (mode == PickMode.Random) return PickRandom();

        return PickWeightedNoRepeat();
    }

    /// <summary>通知：这个 prefab 被生成了一次（用于 NoRepeat 配额统计）</summary>
    public void ReportSpawned(GameObject prefab)
    {
        if (prefab == null) return;

        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i] == null || entries[i].prefab != prefab) continue;
            entries[i].spawnedCount++;
            return;
        }
    }

    /// <summary>重置配额统计（重新开始一轮）</summary>
    public void ResetRound()
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i] == null) continue;
            entries[i].spawnedCount = 0;
        }
    }

    // ==================== 内部 ====================
    private GameObject PickRandom()
    {
        // 按权重随机（Random 模式下也用权重，手感更可控）
        EnsureCache();
        if (_totalWeight <= 0f) return FirstValid();

        float r = Random.value * _totalWeight;
        float acc = 0f;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e == null || e.prefab == null || e.weight <= 0f) continue;

            acc += e.weight;
            if (r <= acc) return e.prefab;
        }

        return FirstValid();
    }

    private GameObject PickWeightedNoRepeat()
    {
        EnsureCache();
        if (_totalQuota <= 0) { ResetRound(); EnsureCache(); }

        // 收集"还没发满配额"的条目
        float remainWeight = 0f;
        int   remainCount  = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e == null || e.prefab == null || e.weight <= 0f) continue;

            if (e.quota <= 0f) continue;                 // 权重过小，不给配额
            if (e.spawnedCount >= Mathf.RoundToInt(e.quota)) continue;

            remainWeight += e.weight * (1f - e.spawnedCount / Mathf.Max(1f, e.quota));
            remainCount++;
        }

        // 全部发满 → 重新开一轮
        if (remainCount == 0)
        {
            if (!restartRoundWhenExhausted) return FirstValid();
            ResetRound();
            EnsureCache();
            return PickWeightedNoRepeat();
        }

        if (remainWeight <= 0f) return FirstValid();

        // 在剩余权重里抽
        float r = Random.value * remainWeight;
        float acc = 0f;
        GameObject last = null;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e == null || e.prefab == null || e.weight <= 0f) continue;
            if (e.quota <= 0f) continue;
            if (e.spawnedCount >= Mathf.RoundToInt(e.quota)) continue;

            last = e.prefab;
            acc += e.weight * (1f - e.spawnedCount / Mathf.Max(1f, e.quota));
            if (r <= acc) return e.prefab;
        }

        return last ?? FirstValid();
    }

    private GameObject FirstValid()
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i] != null && entries[i].prefab != null) return entries[i].prefab;

        return null;
    }

    private void EnsureCache()
    {
        if (_cacheValid && Application.isPlaying) return;

        _cacheValid  = true;
        _totalWeight = 0f;
        _totalQuota  = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e == null) continue;

            if (e.prefab == null || e.weight <= 0f)
            {
                e.quota = 0f;
                continue;
            }

            _totalWeight += e.weight;
        }

        // 计算配额：权重占比 × 100 份，至少 1 份
        const int QuotaUnit = 100;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e == null || e.prefab == null || e.weight <= 0f) continue;

            e.quota = Mathf.Max(1f, Mathf.Round(e.weight / _totalWeight * QuotaUnit));
            _totalQuota += Mathf.RoundToInt(e.quota);
        }
    }

    private void OnValidate()
    {
        _cacheValid = false;
    }
}