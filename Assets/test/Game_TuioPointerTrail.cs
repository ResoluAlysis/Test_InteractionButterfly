using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 直接读 TUIO 的指针粒子生成器。
///
/// 为什么需要它：
///   雷达「鼠标移动」模式下，平台程序会把多个探测点压缩成一个 Windows 光标，
///   顺序轮流设置 → 停留久的那个位置生成更多粒子 → 永远无法公平。
///   TUIO 是原始的、多点的、归一化坐标，完全绕开这层合并。
///
/// 工作方式：
///   后台线程收 UDP 3333（含组播 224.0.0.1，可与平台程序共存）
///   → 解析 TUIO 1.1 的 /set 消息
///   → 每个 id 维护独立位置和生成计时
///   → 每个 id 按 spawnInterval 独立生成粒子
///
/// 关键：只信 /set（位置更新），不信 /alive（会间歇清空）。
///       触点用 keepAliveSeconds 容忍短暂的数据中断。
///
/// ⚠️ 和 Game_PointerTrail / Game_PointerTrailFaithful 冲突，请禁用它们。
/// </summary>
public class Game_TuioPointerTrail : MonoBehaviour, IConfigurable
{
    // ==================== TUIO 连接 ====================
    [Header("TUIO 连接")]
    public int port = 3333;

    [Tooltip("组播地址。加入后可与平台程序同时收同一份数据")]
    public string multicastGroup = "224.0.0.1";

    public bool joinMulticast = true;

    // ==================== 坐标 ====================
    [Header("坐标")]
    public Camera cam;

    [Tooltip("粒子所在的 z 平面")]
    public float planeZ = 0f;

    [Tooltip("TUIO 的 y 轴向下（左上原点），Unity 视口坐标向上 → 需要翻转")]
    public bool flipY = true;

    // ==================== 触点保留 ====================
    [Header("触点保留")]
    [Tooltip("多久没收到该 id 的 set 就认为它消失（秒）。必须大于 alive 清空的间隔")]
    public float keepAliveSeconds = 0.5f;

    // ==================== 生成 ====================
    [Header("生成")]
    public GameObject[] effects;

    [Tooltip("每个触点每秒生成多少个。0.05 = 20 个/秒")]
    public float spawnInterval = 0.05f;

    [Tooltip("粒子的 collectDelay，和 prefab 的 Start Lifetime 对齐")]
    public float lifetime = 3f;

    [Range(0f, 0.6f)] public float lifetimeJitter = 0.2f;

    [Tooltip("生成位置的随机抖动，让粒子散开一点")]
    public float positionJitter = 0.08f;

    public Vector3 spawnEuler = new Vector3(-90f, 0f, 0f);

    // ==================== 外观 ====================
    [Header("外观")]
    [Tooltip("尺寸随机范围（乘在 prefab 原始尺寸上）")]
    public Vector2 scaleRange = new Vector2(0.7f, 1.3f);

    [Tooltip("整体缩放系数，叠在 scaleRange 之上。1 = 原始大小")]
    [Min(0.01f)] public float trailScale = 1f;

    [Header("兴趣点")]
    public bool reportToInterest = true;

    [Header("惊扰")]
    [Tooltip("勾上后把触点的移动线段交给 Game_FlockGestureInput 做扫掠惊扰")]
    public bool enableScare = true;

    [Tooltip("对应的手势组件。留空会自动找场景里的一个")]
    public Game_FlockGestureInput gestureInput;

    // ==================== 引用 ====================
    [Header("引用")]
    public Game_ObjectPool pool;

    // ==================== 调试 ====================
    [Header("调试")]
    public bool logDebug = false;

    [Tooltip("没有雷达时，用鼠标模拟一个触点（调试用）。开启后不启动 TUIO 接收器")]
    public bool simulateMouseForDebug = false;
    public bool showGizmos = true;

    // ==================== 内部类型 ====================
    private struct TuioEvent
    {
        public int   id;
        public float x;     // 归一化 0~1（左侧原点）
        public float y;     // 归一化 0~1（左上原点）
    }

    private class CursorState
    {
        public float x, y;
        public float lastSeen;      // Time.unscaledTime
        public float spawnTimer;
        public int   spawnCount;

        public Game_FlockInterest.PointerReport report;

        // ---- 惊扰 ----
        public Vector2 prevWorld;
        public bool    hasPrevWorld;
    }

    // ==================== 运行时 ====================
    private readonly ConcurrentQueue<TuioEvent> _queue = new ConcurrentQueue<TuioEvent>();
    private readonly Dictionary<int, CursorState> _cursors = new Dictionary<int, CursorState>();
    private readonly List<int> _idScratch = new List<int>(32);

    private UdpClient _udp;
    private Thread    _thread;
    private volatile bool _running;

    private float _statTimer;
    private int   _statEvents;
    private int   _statSpawn;
    private bool  _warnedBind;

    // ==================== 生命周期 ====================

    public void ApplyConfig(Game_Config cfg)
    {
        if (cfg == null) return;

        const string s = "PointerTrail";

        trailScale      = Mathf.Max(0.01f, cfg.GetFloat(s, "trailScale", trailScale));
        positionJitter  = Mathf.Max(0f, cfg.GetFloat(s, "positionJitter", positionJitter));
        spawnInterval   = Mathf.Max(0.005f, cfg.GetFloat(s, "spawnInterval", spawnInterval));
        lifetime        = Mathf.Max(0.05f, cfg.GetFloat(s, "lifetime", lifetime));
        lifetimeJitter  = Mathf.Clamp(cfg.GetFloat(s, "lifetimeJitter", lifetimeJitter), 0f, 0.6f);
        keepAliveSeconds = Mathf.Max(0.05f, cfg.GetFloat(s, "keepAliveSeconds", keepAliveSeconds));

        reportToInterest = cfg.GetBool(s, "reportToInterest", reportToInterest);
        enableScare      = cfg.GetBool(s, "enableScare", enableScare);

        // 尺寸范围（允许写反，内部纠正）
        float lo = cfg.GetFloat(s, "scaleMin", scaleRange.x);
        float hi = cfg.GetFloat(s, "scaleMax", scaleRange.y);

        scaleRange = new Vector2(
            Mathf.Max(0.01f, Mathf.Min(lo, hi)),
            Mathf.Max(0.01f, Mathf.Max(lo, hi)));

        // ★ 端口/组播也可配：现场换设备时改 ini 即可，不用重新打包。
        //   （Inspector 里的值只在打包前有效，ini 在 exe 同级随时可改）
        int  newPort      = cfg.GetInt(s, "port", port);
        bool newMulticast = cfg.GetBool(s, "joinMulticast", joinMulticast);

        if (newPort != port || newMulticast != joinMulticast)
        {
            port          = newPort;
            joinMulticast = newMulticast;

            // 已经在监听 → 重启接收器（Start 阶段 _running 还是 false，不会重复启动）
            if (_running)
            {
                Debug.Log($"[TuioTrail] 端口/组播变更 → 重启接收器 ({port}, 组播={joinMulticast})");

                RestartReceiverIfNeeded();
            }
        }

        // 鼠标模拟（调试）
        bool newSimulate = cfg.GetBool(s, "simulateMouseForDebug", simulateMouseForDebug);

        if (newSimulate != simulateMouseForDebug)
        {
            simulateMouseForDebug = newSimulate;
            RestartReceiverIfNeeded();
        }

        // ★ 池里的粒子是「快照」：只在生成那一刻读过参数。
        //   热重载后把已经在场的也刷新一遍，否则要等 lifetime 秒才看得到变化。
        RefreshLiveParticles();
    }

    /// <summary>运行中切换输入源（TUIO ↔ 鼠标模拟）时启停接收器</summary>
    private void RestartReceiverIfNeeded()
    {
        bool wasRunning = _running;

        if (wasRunning) StopReceiver();

        if (simulateMouseForDebug)
        {
            if (wasRunning)
                Debug.Log("[TuioTrail] 切换为鼠标模拟模式：已停止 TUIO 接收");
        }
        else
        {
            StartReceiver();
        }
    }
    
    private void Start()
    {
        if (cam == null) cam = Camera.main;

        if (pool == null)
        {
            var mgr = Game_FlockManager.Instance;

            if (mgr != null && mgr.pool != null) pool = mgr.pool;
            else pool = FindObjectOfType<Game_ObjectPool>();
        }

        if (pool == null)
            Debug.LogWarning("[TuioTrail] 找不到 Game_ObjectPool，会退化为 Instantiate/Destroy");

        if (effects == null || effects.Length == 0)
            Debug.LogWarning("[TuioTrail] effects 数组为空，不会生成粒子");

        if (enableScare && gestureInput == null)
        {
            gestureInput = FindObjectOfType<Game_FlockGestureInput>();

            if (gestureInput == null)
            {
                Debug.LogWarning("[TuioTrail] 找不到 Game_FlockGestureInput，惊扰不会生效");
            }
            else if (!gestureInput.useExternalInput)
            {
                Debug.LogWarning("[TuioTrail] Game_FlockGestureInput 的 Use External Input 没勾上，" +
                                 "它仍会读鼠标 → 建议勾上");
            }

        }        
        
        // ★ 从外置配置读取（Game_Config 有 DefaultExecutionOrder(-1000)，此时已就绪）
        ApplyConfig(Game_Config.Instance);

        if (simulateMouseForDebug)
            Debug.Log("[TuioTrail] 鼠标模拟模式（调试用）：不启动 TUIO 接收器，用 Input.mousePosition 当触点");
        else
            StartReceiver();
    }

    private void OnDisable()
    {
        StopReceiver();
    }

    private void OnDestroy()
    {
        StopReceiver();
    }

    private void OnApplicationQuit()
    {
        StopReceiver();
    }

    // ==================== UDP 线程 ====================
    private void StartReceiver()
    {
        if (_running) return;

        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            if (joinMulticast)
            {
                try
                {
                    _udp.JoinMulticastGroup(IPAddress.Parse(multicastGroup));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[TuioTrail] 加入组播 {multicastGroup} 失败：{e.Message}（可能仍能收单播）");
                }
            }
        }
        catch (Exception e)
        {
            if (!_warnedBind)
            {
                _warnedBind = true;
                Debug.LogError($"[TuioTrail] 绑定 UDP {port} 失败：{e.Message}\n" +
                               "（端口被独占 → 需要让平台程序把 TUIO 转发到另一个端口）");
            }

            try { _udp?.Close(); } catch { }
            _udp = null;
            return;
        }

        _running = true;

        _thread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "TuioReceiver",
        };

        _thread.Start();

        Debug.Log($"[TuioTrail] 监听 UDP {port}，组播={joinMulticast}");
    }

    private void StopReceiver()
    {
        if (!_running && _udp == null) return;

        _running = false;

        try { _udp?.Close(); } catch { }

        if (_thread != null)
        {
            try { _thread.Join(300); } catch { }
            _thread = null;
        }

        _udp = null;
    }

    private void ReceiveLoop()
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            byte[] data;

            try
            {
                data = _udp.Receive(ref remote);
            }
            catch (SocketException)
            {
                if (!_running) break;
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                if (!_running) break;
                continue;
            }

            try
            {
                ParsePacket(data, data.Length);
            }
            catch (Exception)
            {
                // 单条消息解析失败不影响后续
            }
        }
    }

    // ==================== TUIO 1.1 解析（只关心 set）====================
    private void ParsePacket(byte[] data, int len)
    {
        if (len >= 8 && Matches(data, 0, "#bundle"))
        {
            int pos = 16;   // "#bundle\0" + 8 字节 timetag

            while (pos + 4 <= len)
            {
                int size = ReadInt32(data, pos);
                pos += 4;

                if (size <= 0 || pos + size > len) break;

                ParseMessage(data, pos, size);
                pos += size;
            }

            return;
        }

        ParseMessage(data, 0, len);
    }

    private void ParseMessage(byte[] data, int offset, int len)
    {
        int pos = offset;
        int end = offset + len;

        string address = ReadString(data, ref pos, end);

        if (string.IsNullOrEmpty(address) || address[0] != '/') return;

        string typetag = ReadString(data, ref pos, end);

        if (string.IsNullOrEmpty(typetag) || typetag[0] != ',') return;

        string tags = typetag.Substring(1);

        // TUIO 1.0：地址带命令；TUIO 1.1：命令是第一个字符串参数
        string command;

        if (address.EndsWith("/alive"))     command = "alive";
        else if (address.EndsWith("/set"))  command = "set";
        else if (address.EndsWith("/fseq")) command = "fseq";
        else
        {
            if (tags.Length == 0 || tags[0] != 's') return;

            command = ReadString(data, ref pos, end);
            tags    = tags.Substring(1);
        }

        // ★ 只处理 set：alive 会间歇清空，不能作为存在性判断
        if (command != "set") return;

        int   p      = pos;
        int   id     = 0;
        float x      = 0f;
        float y      = 0f;
        int   fIndex = 0;

        for (int i = 0; i < tags.Length; i++)
        {
            switch (tags[i])
            {
                case 'i':
                    id = ReadInt32(data, p);
                    p += 4;
                    break;

                case 'f':
                    float f = ReadFloat(data, p);
                    p += 4;

                    if (fIndex == 0)      x = f;
                    else if (fIndex == 1) y = f;

                    fIndex++;
                    break;

                case 's':
                    ReadString(data, ref p, end);
                    break;

                default:
                    p += 4;
                    break;
            }
        }

        _queue.Enqueue(new TuioEvent { id = id, x = x, y = y });
    }

    // ==================== 鼠标模拟 ====================
    /// <summary>虚拟触点的 id。用负数避免和真实 TUIO 的 id（0,1,2...）冲突</summary>
    private const int SimulatedCursorId = -1;

    /// <summary>
    /// 调试用：把鼠标当成一个虚拟触点写进状态表。
    /// 下游逻辑（生成 / POI / 惊扰 / 缩放）和真实 TUIO 触点完全一致。
    /// </summary>
    private void FeedSimulatedMouse(float now)
    {
        Vector3 mp = Input.mousePosition;

        float nx = Mathf.Clamp01(mp.x / Mathf.Max(1f, Screen.width));

        // Unity 屏幕 y 向上（0 = 底）
        float ny = Mathf.Clamp01(mp.y / Mathf.Max(1f, Screen.height));

        // 转成 TUIO 语义（0 = 顶），让 WorldFromTuio 的 flipY 翻转后正好回到原位
        if (flipY) ny = 1f - ny;

        if (!_cursors.TryGetValue(SimulatedCursorId, out CursorState st))
        {
            st = new CursorState();
            _cursors[SimulatedCursorId] = st;
        }

        st.x        = nx;
        st.y        = ny;
        st.lastSeen = now;
    }

    // ==================== 主线程 ====================
    private void Update()
    {
        float now = Time.unscaledTime;
        float dt  = Time.unscaledDeltaTime;

        // ---------- 0. 鼠标模拟（调试用）----------
        if (simulateMouseForDebug)
            FeedSimulatedMouse(now);

        // ---------- 1. 消费队列 ----------
        while (_queue.TryDequeue(out TuioEvent ev))
        {
            _statEvents++;

            if (!_cursors.TryGetValue(ev.id, out CursorState st))
            {
                st = new CursorState();
                _cursors[ev.id] = st;
            }

            st.x        = ev.x;
            st.y        = ev.y;
            st.lastSeen = now;
        }

        // ---------- 2. 每个触点独立生成 ----------
        _idScratch.Clear();
        _idScratch.AddRange(_cursors.Keys);

        float interval = Mathf.Max(0.005f, spawnInterval);

        for (int i = 0; i < _idScratch.Count; i++)
        {
            int id = _idScratch[i];

            if (!_cursors.TryGetValue(id, out CursorState st)) continue;

            // 超时 → 认为触点消失
            if (now - st.lastSeen > keepAliveSeconds)
            {
                // ★ 该触点产生的 POI 立刻解散。
                //   否则 Report 不再被调用，POI 会残留 maxHoldLifetime（默认 8 秒），
                //   视觉上就是"蝴蝶聚在原地不动 8 秒"。
                if (st.report != null && st.report.poiCreated &&
                    Game_FlockInterest.Instance != null)
                {
                    Game_FlockInterest.Instance.RemovePointAt(st.report.stillWorld, 3f);
                }

                st.report?.Reset();

                _cursors.Remove(id);
                continue;
            }

            // ---- 触点世界坐标（每帧算一次，生成 / POI / 惊扰共用）----
            Vector3 w3    = WorldFromTuio(st.x, st.y);
            Vector2 world = new Vector2(w3.x, w3.y);

            // ---- 惊扰：该触点自己的「上一帧 → 本帧」线段 ----
            if (enableScare && gestureInput != null)
            {
                if (st.hasPrevWorld)
                    gestureInput.SweepExternal(st.prevWorld, world);

                st.prevWorld    = world;
                st.hasPrevWorld = true;
            }

            // ---- 兴趣点上报 ----
            // id 可能被雷达复用；位置突变时重置静止检测，避免在旧位置误建 POI
            if (reportToInterest)
            {
                var interest = Game_FlockInterest.Instance;

                if (interest != null)
                {
                    if (st.report == null) st.report = new Game_FlockInterest.PointerReport();

                    if (st.report.hasStillPoint &&
                        (world - st.report.stillWorld).sqrMagnitude > 100f)
                    {
                        st.report.Reset();
                    }

                    st.report.active = true;
                    st.report.held   = true;
                    st.report.world  = world;

                    interest.Report(st.report);
                }
            }

            // 独立计时生成
            st.spawnTimer += dt;

            int guard = 0;

            while (st.spawnTimer >= interval && guard++ < 10)
            {
                st.spawnTimer -= interval;

                SpawnAt(st.x, st.y);

                st.spawnCount++;
                _statSpawn++;
            }
        }

        
        // ---------- 3. 调试统计 ----------
        if (logDebug)
        {
            _statTimer += dt;

            if (_statTimer >= 1f)
            {
                _statTimer = 0f;

                var sb = new StringBuilder();
                sb.Append($"[TuioTrail] {(simulateMouseForDebug ? "【鼠标模拟】" : "")}" +
                          $"触点 {_cursors.Count} 个 | 事件 {_statEvents}/s | 生成 {_statSpawn}/s");

                foreach (var kv in _cursors)
                {
                    sb.Append($" | id={kv.Key}({kv.Value.x:F2},{kv.Value.y:F2})×{kv.Value.spawnCount}");
                    kv.Value.spawnCount = 0;
                }

                Debug.Log(sb.ToString());

                _statEvents = 0;
                _statSpawn  = 0;
            }
        }
    }

    // ==================== 坐标 ====================
    /// <summary>TUIO 归一化坐标（左上原点）→ 世界坐标（planeZ 平面）</summary>
    private Vector3 WorldFromTuio(float nx, float ny)
    {
        if (cam == null) return Vector3.zero;

        // TUIO 归一化 → Unity 视口（y 轴翻转）
        float vy = flipY ? (1f - ny) : ny;

        // 视口 → 世界（在 planeZ 平面上）
        float   dist  = planeZ - cam.transform.position.z;
        Vector3 world = cam.ViewportToWorldPoint(new Vector3(nx, vy, dist));

        world.z = planeZ;
        return world;
    }

    // ==================== 生成 ====================
    private void SpawnAt(float nx, float ny)
    {
        if (effects == null || effects.Length == 0) return;
        if (cam == null) return;

        Vector3 world = WorldFromTuio(nx, ny);

        // 抖动
        if (positionJitter > 0f)
        {
            Vector2 j = Random.insideUnitCircle * positionJitter;
            world.x += j.x;
            world.y += j.y;
        }

        GameObject prefab = effects[Random.Range(0, effects.Length)];
        if (!prefab) return;

        Quaternion rot = Quaternion.Euler(spawnEuler);

        GameObject go;

        if (pool) go = pool.Acquire(prefab, world, rot);
        else              go = Instantiate(prefab, world, rot);

        if (!go) return;

        // ---- 缩放（叠在 prefab 原始尺寸上）----
        float   s         = Random.Range(scaleRange.x, scaleRange.y) * trailScale;
        Vector3 baseScale = GetBaseScale(prefab);

        go.transform.localScale = baseScale * s;

        // 记账：热重载时能刷新已经在场的实例
        TrackLive(go, baseScale);

        float life = lifetime * (1f + Random.Range(-lifetimeJitter, lifetimeJitter));
        life = Mathf.Max(0.05f, life);

        if (pool) pool.Release(go, life);
        else              Destroy(go, life);
    }

    // ==================== 缩放 ====================
    private readonly Dictionary<GameObject, Vector3> _baseScale = new Dictionary<GameObject, Vector3>();

    /// <summary>
    /// prefab 资产的原始 localScale。
    /// 池化复用时实例的 scale 已被上次的随机值覆盖，所以基准必须从 prefab 资产取。
    /// </summary>
    private Vector3 GetBaseScale(GameObject prefab)
    {
        if (!prefab) return Vector3.one;

        if (_baseScale.TryGetValue(prefab, out Vector3 s)) return s;

        s = prefab.transform.localScale;

        if (s == Vector3.zero) s = Vector3.one;

        _baseScale[prefab] = s;
        return s;
    }

    // ==================== 在场粒子记账 ====================
    // 池里的粒子是「快照」：只在生成那一刻读过参数。
    // 热重载后要把已经在场的实例也刷新一遍，否则要等 lifetime 秒才能看到变化。
    private struct LiveParticle
    {
        public GameObject go;
        public Vector3    baseScale;
    }

    private readonly List<LiveParticle> _live = new List<LiveParticle>();

    /// <summary>超过这个数量就先清理一次，避免列表无限增长</summary>
    private const int LivePruneThreshold = 600;

    private void TrackLive(GameObject go, Vector3 baseScale)
    {
        if (_live.Count >= LivePruneThreshold) PruneLive();

        _live.Add(new LiveParticle { go = go, baseScale = baseScale });
    }

    /// <summary>移除已被池回收（SetActive(false)）或已销毁的记录</summary>
    private void PruneLive()
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            GameObject g = _live[i].go;

            if (g == null || !g.activeSelf) _live.RemoveAt(i);
        }
    }

    /// <summary>配置热重载后，把已经在场的粒子按新参数重设尺寸</summary>
    private void RefreshLiveParticles()
    {
        if (_live.Count == 0) return;

        int refreshed = 0;

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            LiveParticle lp = _live[i];

            if (lp.go == null || !lp.go.activeSelf)
            {
                _live.RemoveAt(i);
                continue;
            }

            // 重新 roll 一次尺寸：粒子本来就是随机大小，重分配不会破坏视觉
            float s = Random.Range(scaleRange.x, scaleRange.y) * trailScale;

            lp.go.transform.localScale = lp.baseScale * s;
            refreshed++;
        }

        if (logDebug)
            Debug.Log($"[TuioTrail] 配置重载：刷新了 {refreshed} 个在场粒子");
    }

    // ==================== 二进制读取 ====================
    private static bool Matches(byte[] data, int offset, string s)
    {
        if (offset + s.Length > data.Length) return false;

        for (int i = 0; i < s.Length; i++)
            if (data[offset + i] != s[i]) return false;

        return true;
    }

    /// <summary>OSC 字符串：读到 \0，然后对齐到 4 字节</summary>
    private static string ReadString(byte[] data, ref int pos, int end)
    {
        int start = pos;

        while (pos < end && data[pos] != 0) pos++;

        string s = Encoding.UTF8.GetString(data, start, pos - start);

        pos++;

        while (pos % 4 != 0 && pos < end) pos++;

        return s;
    }

    /// <summary>OSC 用大端序</summary>
    private static int ReadInt32(byte[] data, int pos)
    {
        return (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
    }

    private static float ReadFloat(byte[] data, int pos)
    {
        return BitConverter.Int32BitsToSingle(ReadInt32(data, pos));
    }

    // ==================== 调试 ====================
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!showGizmos || !Application.isPlaying) return;
        if (cam == null) return;

        float dist = planeZ - cam.transform.position.z;

        foreach (var kv in _cursors)
        {
            float   vy    = flipY ? (1f - kv.Value.y) : kv.Value.y;
            Vector3 world = cam.ViewportToWorldPoint(new Vector3(kv.Value.x, vy, dist));
            world.z = planeZ;

            // 触点中心
            Gizmos.color = new Color(0.2f, 1f, 0.6f, 0.95f);
            Gizmos.DrawWireSphere(world, 0.3f);

            // 抖动范围
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.5f);
            Gizmos.DrawWireSphere(world, positionJitter);

            UnityEditor.Handles.color = Color.white;
            UnityEditor.Handles.Label(world + Vector3.up * 0.7f, $"id={kv.Key}");
        }
    }
#endif
}
