using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 蝴蝶拖尾：按飞行状态切换拖尾对象。
///
/// 不做硬开关 —— 切换时先 Stop(StopEmitting)，让已发射的粒子自然飞完，
/// 等残粒子消失后才关闭物体，避免尾巴突然消失。
///
/// 本组件不修改任何粒子参数（发射率/颜色/形状都由你在 Prefab 上调）。
/// </summary>
public class Game_FlockTrail : MonoBehaviour, IGamePoolable
{
    /// <summary>拖尾状态。与 Game_FlockChild_Test.FlyState 对应，另加一个 Flee。</summary>
    public enum TrailState { Cruise, Glide, Dash, Flee }

    [Serializable]
    public class StateTrail
    {
        public TrailState state = TrailState.Cruise;

        [InspectorName("子物体名")]
        [Tooltip("优先使用它：在本物体及子物体里按名字查找")]
        public string childObjectName = "";

        [InspectorName("直接引用（可选）")]
        [Tooltip("childObjectName 留空时使用")]
        public GameObject trailObject;

        [InspectorName("消退最长等待（秒）")]
        [Tooltip("切走后最多保持多久让残粒子飞完。0 = 用粒子自身 lifetime 推算。建议略大于 Start Lifetime")]
        public float fadeOutMaxWait = 0f;
    }

    // ==================== 引用 ====================
    [Header("引用（留空自动向父级查找）")]
    public Game_FlockChild_Test agent;

    // ==================== 映射 ====================
    [Header("状态 → 拖尾 映射")]
    public List<StateTrail> bindings = new List<StateTrail>();

    [Header("受惊")]
    [Tooltip("勾上：受惊时切到 Flee 的拖尾（覆盖当前状态）")]
    public bool useFleeOverride = true;

    [Header("消退")]
    [Tooltip("兜底等待上限（秒）。防止某个粒子的 lifetime 设得过大导致物体一直不关闭")]
    public float fadeOutHardCap = 5f;

    // ==================== 运行时 ====================
    private class Entry
    {
        public GameObject go;
        public ParticleSystem[] systems;
        public Coroutine fadeRoutine;
        public float maxLifetime;
    }

    private readonly Dictionary<TrailState, Entry> _map = new Dictionary<TrailState, Entry>();
    private TrailState _current = (TrailState)(-1);
    private bool _ready;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        if (agent == null) agent = GetComponentInParent<Game_FlockChild_Test>();

        if (agent == null)
        {
            Debug.LogWarning($"[FlockTrail] {name} 找不到 Game_FlockChild_Test，组件已禁用");
            enabled = false;
            return;
        }

        BuildMap();
    }

    private void BuildMap()
    {
        _map.Clear();

        for (int i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            if (b == null) continue;

            var go = Resolve(b);
            if (go == null) continue;

            if (_map.ContainsKey(b.state))
            {
                Debug.LogWarning($"[FlockTrail] {name} 状态 {b.state} 配了多个，只有第一个生效");
                continue;
            }

            var systems = go.GetComponentsInChildren<ParticleSystem>(true);
            if (systems.Length == 0)
                Debug.LogWarning($"[FlockTrail] {name} 拖尾 \"{go.name}\" 上没有任何 ParticleSystem");

            // 推算最大 lifetime，用于消退等待
            float maxLife = 0.1f;
            for (int s = 0; s < systems.Length; s++)
            {
                float l = systems[s].main.startLifetime.constantMax;
                if (l > maxLife) maxLife = l;
            }

            _map.Add(b.state, new Entry
            {
                go = go,
                systems = systems,
                maxLifetime = b.fadeOutMaxWait > 0f ? b.fadeOutMaxWait : maxLife,
            });
        }

        _ready = true;
        _current = (TrailState)(-1);
    }

    private GameObject Resolve(StateTrail b)
    {
        if (!string.IsNullOrEmpty(b.childObjectName))
        {
            var all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == transform) continue;
                if (all[i].name == b.childObjectName) return all[i].gameObject;
            }

            Debug.LogWarning($"[FlockTrail] {name} 找不到子物体 \"{b.childObjectName}\"（状态 {b.state}）");
            return null;
        }

        if (b.trailObject != null) return b.trailObject;

        Debug.LogWarning($"[FlockTrail] {name} 状态 {b.state} 未指定拖尾对象，已跳过");
        return null;
    }

    // ==================== 池化回调 ====================
    public void OnSpawned()
    {
        if (agent == null) return;

        BuildMap();

        // 全部先静默：清粒子 + 关闭
        foreach (var kv in _map) HardStop(kv.Value);

        Apply(ForceState(agent.State), true);
    }

    public void OnDespawned()
    {
        foreach (var kv in _map) HardStop(kv.Value);
        _current = (TrailState)(-1);
    }

    // ==================== 每帧 ====================
    private void Update()
    {
        if (!_ready || agent == null) return;
        Apply(ForceState(agent.State), false);
    }

    private TrailState ForceState(Game_FlockChild_Test.FlyState s)
    {
        if (useFleeOverride && agent.IsEscaping && _map.ContainsKey(TrailState.Flee))
            return TrailState.Flee;

        switch (s)
        {
            case Game_FlockChild_Test.FlyState.Glide: return TrailState.Glide;
            case Game_FlockChild_Test.FlyState.Dash:  return TrailState.Dash;
            default:                                  return TrailState.Cruise;
        }
    }

    // ==================== 核心：切换 ====================
    private void Apply(TrailState want, bool force)
    {
        if (!force && want == _current) return;
        _current = want;

        foreach (var kv in _map)
        {
            var e = kv.Value;
            if (e.go == null) continue;

            if (kv.Key == want) EnableTrail(e);
            else                FadeOutTrail(e);
        }
    }

    /// <summary>启用某个拖尾：打开物体 + 重新播放</summary>
    private void EnableTrail(Entry e)
    {
        // 正在消退中 → 取消消退，别把它关掉
        if (e.fadeRoutine != null)
        {
            StopCoroutine(e.fadeRoutine);
            e.fadeRoutine = null;
        }

        if (!e.go.activeSelf) e.go.SetActive(true);

        // SetActive(true) 不会自动恢复播放，必须显式 Play
        for (int i = 0; i < e.systems.Length; i++)
        {
            if (e.systems[i] == null) continue;
            if (!e.systems[i].isPlaying) e.systems[i].Play();
        }
    }

    /// <summary>淡出某个拖尾：停止发射，等残粒子飞完再关物体</summary>
    private void FadeOutTrail(Entry e)
    {
        // 物体已关 / 还没开过 → 无事可做
        if (!e.go.activeSelf) return;

        // 已经在消退 → 不重复启动
        if (e.fadeRoutine != null) return;

        // 关键：只停发射，不清粒子
        for (int i = 0; i < e.systems.Length; i++)
            e.systems[i]?.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        e.fadeRoutine = StartCoroutine(FadeOutRoutine(e));
    }

    private IEnumerator FadeOutRoutine(Entry e)
    {
        float waited = 0f;
        float timeout = Mathf.Min(e.maxLifetime + 0.3f, fadeOutHardCap);

        // 等残粒子自然消失
        while (waited < timeout)
        {
            if (e.go == null) yield break;

            bool stillAlive = false;
            for (int i = 0; i < e.systems.Length; i++)
            {
                var ps = e.systems[i];
                if (ps == null) continue;
                if (ps.isPlaying || ps.particleCount > 0) { stillAlive = true; break; }
            }

            if (!stillAlive) break;

            waited += Time.deltaTime;
            yield return null;
        }

        e.fadeRoutine = null;

        if (e.go != null && e.go.activeSelf) e.go.SetActive(false);
    }

    /// <summary>立即停止并清空（用于池化回收 / 重新生成）</summary>
    private void HardStop(Entry e)
    {
        if (e == null) return;

        if (e.fadeRoutine != null)
        {
            StopCoroutine(e.fadeRoutine);
            e.fadeRoutine = null;
        }

        for (int i = 0; i < e.systems.Length; i++)
            e.systems[i]?.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        if (e.go != null && e.go.activeSelf) e.go.SetActive(false);
    }

    // ==================== 外部接口 ====================
    public void ForceTrail(TrailState state)
    {
        if (_map.ContainsKey(state)) Apply(state, true);
    }

    public TrailState CurrentTrail => _current;
}