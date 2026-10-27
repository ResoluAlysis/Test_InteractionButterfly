using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 外部背景资源播放器（从 exe 同级目录读）。
///
/// 目录约定（都在 BasePath = dataPath/../ 下）：
///   Video/bg.mp4   → 背景视频
///   Image/bg.png   → 背景图
///   Music/bg.mp3   → 背景音乐
///
/// 图片模式：以容器中心为基准，按"高度铺满"算出每张图的宽度，
/// 然后向左右复制足够多的份数填满屏幕。分辨率变化时自动重算。
/// </summary>
public class Game_ExternalBackground : MonoBehaviour, IConfigurable
{
    // ==================== 目标 ====================
    [Header("背景容器")]
    [Tooltip("一个空 RectTransform，拉伸铺满 Canvas。图片会作为它的子物体平铺")]
    public RectTransform container;

    [Header("背景音乐音源")]
    public AudioSource musicSource;

    [Header("背景视频播放器")]
    public UnityEngine.Video.VideoPlayer videoPlayer;

    // ==================== 开关 ====================
    [Header("启用项")]
    public bool useVideo = true;
    public bool useImage = true;
    public bool useMusic = true;

    // ==================== 路径 ====================
    [Header("相对路径（相对 exe 同级目录）")]
    public string videoDir = "Video";
    public string imageDir = "Image";
    public string musicDir = "Music";

    [Header("文件名（不含扩展名）")]
    public string videoName = "bg";
    public string imageName = "bg";
    public string musicName = "bg";

    // ==================== 图片填充 ====================
    [Header("图片填充")]
    [Header("图片默认颜色")]
    public Color imgColor = Color.white;
    
    [Tooltip("是否向左右复制填满。关掉则只居中显示一张")]
    public bool fillHorizontally = true;

    [Tooltip("单张图额外放大的比例。1 = 正好高度铺满")]
    [Range(0.5f, 3f)] public float scaleMul = 1f;

    [Tooltip("是否把容器左右各多留 1 张（防止滚动或边缘露白）")]
    public bool extraTileEachSide = true;
    
    [Header("视频音频")]
    [Tooltip("播放视频自带的音轨。关掉则视频静音")]
    public bool useVideoAudio = true;

    [Tooltip("视频音量（0~1）")]
    [Range(0f, 1f)] public float videoVolume = 0.8f;

    [Tooltip("视频有音频时自动静音背景音乐，避免两层声音叠在一起")]
    public bool muteMusicWhenVideoAudio = true;

    // ==================== 音乐 ====================
    [Header("音乐")]
    [Range(0f, 1f)] public float musicVolume = 0.6f;
    public bool musicLoop = true;
    [Tooltip("长音乐建议勾上")]
    public bool streamMusic = true;

    // ==================== 调试 ====================
    [Header("调试")]
    public bool logDebug = false;

    // ==================== 运行时 ====================
    private Image    _image;        // 容器上若有 Image 则为 3D/单图模式兜底
    private RawImage _rawImage;
    private Material _mat;
    private RawImage     _videoImage;      // 显示视频用的全屏 RawImage
    private bool _videoPlaying;

    private Sprite _bgSprite;
    private float  _bgAspect = 1f;

    private readonly List<Image> _tiles = new List<Image>();
    private int _lastW = -1;
    private int _lastH = -1;
    
    // 开关变化检测
    private bool _appliedUseVideo;
    private bool _appliedUseImage;
    private bool _appliedUseMusic;
    private bool _switchesReady;
    private bool  _appliedUseVideoAudio;
    private float _appliedVideoVolume = -1f;

    // ==================== 生命周期 ====================
    private void Awake()
    {
        if (container == null) container = transform as RectTransform;

        if (container == null)
        {
            Debug.LogError("[ExternalBG] 未指定 container（需要一个 RectTransform）");
            enabled = false;
            return;
        }

        // 容器自身的 Image / RawImage（单图兜底用，可选）
        _image    = container.GetComponent<Image>();
        _rawImage = container.GetComponent<RawImage>();

        var rend = container.GetComponent<Renderer>();
        if (rend != null) _mat = rend.material;
        
        // 首帧布局兜底
        Canvas.ForceUpdateCanvases();
        
        // ★ 初始化开关快照，避免 Update 里第一帧误判为"发生变化"
        _appliedUseVideo = useVideo;
        _appliedUseImage = useImage;
        _appliedUseMusic = useMusic;
        _appliedUseVideoAudio = useVideoAudio;
        _appliedVideoVolume   = videoVolume;
        _switchesReady = true;
    }

    private void Start()
    {
        if (logDebug)
            Debug.Log($"[ExternalBG] 素材根目录 = {Game_ExternalAssets.BasePath}");

        StartCoroutine(SetupRoutine());
        
        // ★ 在 Awake 之后、生成之前套用配置
        ApplyConfig(Game_Config.Instance);
    }

    public void ApplyConfig(Game_Config cfg)
    {
        if (!cfg) return;

        const string S = "Background";
        
        musicVolume = Mathf.Clamp01(cfg.GetFloat(S,"musicVolume", musicVolume));
        useVideo =  cfg.GetBool(S, "useVideo", useVideo);
        useImage =   cfg.GetBool(S, "useImage", useImage);
        useMusic =  cfg.GetBool(S, "useMusic", useMusic);
        useVideoAudio = cfg.GetBool(S, "useVideoAudio", useVideoAudio);
        muteMusicWhenVideoAudio = cfg.GetBool(S, "muteMusicWhenVideoAudio", muteMusicWhenVideoAudio);
    }

    private void OnDestroy()
    {
        if (_bgSprite != null)
        {
            if (_bgSprite.texture != null) Destroy(_bgSprite.texture);
            Destroy(_bgSprite);
            _bgSprite = null;
        }
    }

    private IEnumerator SetupRoutine()
    {
        yield return null;      // 等 UI 布局完成
        
        // ---- 画面：视频优先 ----
        bool videoOk = false;
        
        if (useVideo && videoPlayer != null)
        {
            yield return SetupVideoRoutine();      // ★ 改成协程，要等 prepare
            videoOk = _videoPlaying;
        }

        if (!videoOk && useImage)
            yield return SetupImageRoutine();

        // ---- 音乐独立 ----
        if (useMusic)
            yield return SetupMusicRoutine();
    }

    // ==================== 视频 ====================
    /// <summary>
    /// 加载并显示背景视频。
    /// 关键：VideoPlayer 必须用 RenderTexture 模式，并把 videoPlayer.texture
    /// 交给一个全屏 RawImage，否则屏幕上什么都看不到。
    /// </summary>
    private IEnumerator SetupVideoRoutine()
    {
        _videoPlaying = false;

        if (videoPlayer == null) yield break;

        string path = Game_ExternalAssets.FirstExisting(
            $"{videoDir}/{videoName}.mp4",
            $"{videoDir}/{videoName}.webm",
            $"{videoDir}/{videoName}.mov");

        if (path == null)
        {
            if (logDebug) Debug.Log("[ExternalBG] 没有背景视频");
            yield break;
        }

        // ---------- 1. 配置 VideoPlayer ----------
        videoPlayer.source        = UnityEngine.Video.VideoSource.Url;
        videoPlayer.renderMode    = UnityEngine.Video.VideoRenderMode.APIOnly;
        videoPlayer.url           = Game_ExternalAssets.ResolveUrl(path);
        videoPlayer.isLooping     = true;
        videoPlayer.playOnAwake   = false;
        videoPlayer.waitForFirstFrame = true;


        // ---------- 音频模式 ----------
        // ★ 始终用 Direct，靠 mute 控制开关。
        //   这样运行中切 useVideoAudio 不会触发重新 Prepare。
        videoPlayer.audioOutputMode = UnityEngine.Video.VideoAudioOutputMode.Direct;
        videoPlayer.controlledAudioTrackCount = 1;
        
        // ---------- 2. Prepare 并等就绪 ----------
        videoPlayer.Prepare();
        
        // ---------- 音频就绪后应用音量/静音 ----------
        ApplyVideoAudio();

        float timeout = 5f;
        while (!videoPlayer.isPrepared && timeout > 0f)
        {
            if (videoPlayer == null) yield break;
            timeout -= Time.deltaTime;
            yield return null;
        }

        if (!videoPlayer.isPrepared)
        {
            Debug.LogError($"[ExternalBG] 视频准备超时（文件可能损坏或缺少解码器）：{path}");
            yield break;
        }

        // ---------- 3. 显示到全屏 RawImage ----------
        EnsureVideoImage();
        _videoImage.texture = videoPlayer.texture;   // ★ 就是这一句之前缺的
        _videoImage.enabled = true;

        // 视频接管画面 → 隐藏图片 tile
        HideAllTiles();

        // ---------- 4. 播放 ----------
        videoPlayer.Play();
        _videoPlaying = true;
        
        ApplyVideoAudio();      // 再应用一次，兜底

        if (logDebug)
            Debug.Log($"[ExternalBG] 播放视频 {path}  " +
                      $"({videoPlayer.width}x{videoPlayer.height}, {videoPlayer.frameCount} 帧)");
    }
    
    /// <summary>
    /// 应用视频音频开关与音量。
    /// 用 mute 而不是改 audioOutputMode —— 后者会强制重新 Prepare，画面会重载。
    /// </summary>
    private void ApplyVideoAudio()
    {
        if (videoPlayer == null) return;

        bool hasTrack = videoPlayer.audioTrackCount > 0;

        if (hasTrack)
        {
            videoPlayer.SetDirectAudioMute(0, !useVideoAudio);
            videoPlayer.SetDirectAudioVolume(0, Mathf.Clamp01(videoVolume));
        }
        else if (useVideoAudio && logDebug)
        {
            Debug.Log("[ExternalBG] 视频没有音轨，useVideoAudio 无效");
        }

        // ---- 视频有声音 → 静音背景音乐 ----
        if (musicSource != null && muteMusicWhenVideoAudio)
        {
            bool videoHasSound = hasTrack && useVideoAudio && videoVolume > 0.001f;
            musicSource.mute = videoHasSound;
        }
    }
    
    /// <summary>确保有一个铺满容器的 RawImage 用来显示视频</summary>
    private void EnsureVideoImage()
    {
        if (_videoImage != null) return;

        var go = new GameObject("BgVideo", typeof(RectTransform), typeof(RawImage));
        go.transform.SetParent(container, false);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin        = Vector2.zero;
        rt.anchorMax        = Vector2.one;
        rt.offsetMin        = Vector2.zero;
        rt.offsetMax        = Vector2.zero;
        rt.pivot            = new Vector2(0.5f, 0.5f);

        _videoImage = go.GetComponent<RawImage>();
        _videoImage.raycastTarget = false;
        _videoImage.enabled = false;
    }

    private void HideVideoImage()
    {
        if (_videoImage != null) _videoImage.enabled = false;
        _videoPlaying = false;
    }

    // ==================== 图片 ====================
    private IEnumerator SetupImageRoutine()
    {
        string path = Game_ExternalAssets.FirstExisting(
            $"{imageDir}/{imageName}.jpg",
            $"{imageDir}/{imageName}.jpeg",
            $"{imageDir}/{imageName}.png");

        if (path == null)
        {
            if (logDebug) Debug.Log("[ExternalBG] 没有背景图片");

            if (_image)    _image.enabled    = false;
            if (_rawImage) _rawImage.enabled = false;
            HideAllTiles();
            yield break;
        }

        Texture2D tex = null;
        yield return Game_ExternalAssets.LoadTexture(path, t => tex = t);

        if (!tex) yield break;

        // 创建 Sprite（PPU 用 100，配合 Image.SetNativeSize 可让 1 像素 = 1 UI 单位）
        tex.wrapMode   = TextureWrapMode.Clamp;     // 平铺靠多份物体，不需要 Repeat
        tex.filterMode = FilterMode.Bilinear;
        
        _bgSprite = Sprite.Create(
            tex,
            new Rect(0f, 0f, tex.width, tex.height),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect);

        _bgSprite.name = tex.name;
        _bgAspect = (float)tex.width / Mathf.Max(1, tex.height);

        // 布局
        RebuildLayout();

        if (logDebug)
            Debug.Log($"[ExternalBG] 应用背景图 {path} ({tex.width}x{tex.height}) " +
                      $"宽高比={_bgAspect:F3} 平铺数={_tiles.Count}");
    }

    // ==================== 平铺布局 ====================
    private void HideAllTiles()
    {
        for (int i = 0; i < _tiles.Count; i++)
            if (_tiles[i]) _tiles[i].gameObject.SetActive(false);
    }

    /// <summary>核心：按容器尺寸重算平铺</summary>
    private void RebuildLayout()
    {
        if (!_bgSprite || !container) return;

        // ★★★ 图片不该显示时（被关掉 / 视频正在播），不要重建布局。
        //     否则会把所有 tile 重新 SetActive(true)，背景图就"自己冒出来"。
        if (!useImage || _videoPlaying)
        {
            HideAllTiles();
            return;
        }
        Rect cr = container.rect;
        float W = cr.width;
        float H = cr.height;

        if (W <= 1f || H <= 1f)
        {
            // 布局还没算出来，下一帧再试
            return;
        }

        // ---- 单张图的显示尺寸：高度铺满容器，宽度按图片宽高比 ----
        float tileH = H * scaleMul;
        float tileW = tileH * _bgAspect;

        // ---- 需要几张才能覆盖容器宽度 ----
        int tileCount = 1;
        if (fillHorizontally && tileW > 1f)
        {
            tileCount = Mathf.CeilToInt(W / tileW);
            if (extraTileEachSide) tileCount += 2;      // 左右各多一张
        }

        // ★★★ 强制奇数：保证屏幕正中永远是一整张图，而不是两张的接缝
        if (tileCount % 2 == 0) tileCount++;

        tileCount = Mathf.Max(1, tileCount);

        // ---- 保证有足够的 Image 物体 ----
        EnsureTileCount(tileCount);

        // ---- 居中排列 ----
        // 第 0 张放在最左，整体以容器中心对称
        float startX = -(tileCount - 1) * 0.5f * tileW;

        for (int i = 0; i < _tiles.Count; i++)
        {
            var img = _tiles[i];
            if (!img) continue;

            bool used = i < tileCount;
            img.gameObject.SetActive(used);
            if (!used) continue;

            var rt = img.rectTransform;

            // 锚点居中 + pivot 居中 → anchoredPosition 就是相对容器中心的偏移
            rt.anchorMin        = new Vector2(0.5f, 0.5f);
            rt.anchorMax        = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(startX + i * tileW, 0f);
            rt.sizeDelta        = new Vector2(tileW, tileH);
        }

        if (logDebug)
            Debug.Log($"[ExternalBG] 容器 {W:F0}x{H:F0}，单张 {tileW:F0}x{tileH:F0}，共 {tileCount} 张");
    }

    private void EnsureTileCount(int need)
    {
        while (_tiles.Count < need)
        {
            var go = new GameObject($"BgTile_{_tiles.Count}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(container, false);

            var img = go.GetComponent<Image>();
            img.sprite        = _bgSprite;
            img.type          = Image.Type.Simple;
            img.raycastTarget = false;                  // ★ 别挡触摸
            img.preserveAspect = false;                 // 尺寸由我们算，不需要它
            img.color = imgColor;                       //初始化颜色

            _tiles.Add(img);
        }

        // 已有的一定要更新 sprite（换图时）
        for (var i = 0; i < _tiles.Count; i++)
        {
            if (_tiles[i] && _tiles[i].sprite != _bgSprite)
                _tiles[i].sprite = _bgSprite;
        }
    }

    // ==================== 每帧 ====================
    private void Update()
    {
        if (Screen.width != _lastW || Screen.height != _lastH)
        {
            _lastW = Screen.width;
            _lastH = Screen.height;
            RebuildLayout();
        }
        
        if (!_switchesReady) return;

        // ★ 运行中切换开关
        if (_appliedUseVideo != useVideo)
        {
            _appliedUseVideo = useVideo;
            ApplyVideoSwitch();
        }

        if (_appliedUseImage != useImage)
        {
            _appliedUseImage = useImage;
            ApplyImageSwitch();
        }

        if (_appliedUseMusic != useMusic)
        {
            _appliedUseMusic = useMusic;
            ApplyMusicSwitch();
        }
        
        // 视频音频开关
        if (_appliedUseVideoAudio != useVideoAudio)
        {
            _appliedUseVideoAudio = useVideoAudio;
            ApplyVideoAudio();
        }

        // 视频音量
        if (!Mathf.Approximately(_appliedVideoVolume, videoVolume))
        {
            _appliedVideoVolume = videoVolume;
            ApplyVideoAudio();
        }
    }
    
    // ==================== 运行时开关 ====================

    /// <summary>图片开关：开 → 显示/重建；关 → 隐藏所有 tile</summary>
    private void ApplyImageSwitch()
    {
        if (!useImage)
        {
            HideAllTiles();
            if (_image != null)    _image.enabled    = false;
            if (_rawImage != null) _rawImage.enabled = false;
            return;
        }

        // 已经加载过 sprite → 只重建布局；没加载过 → 去加载
        if (_bgSprite != null)
        {
            RebuildLayout();
            if (_image != null) _image.enabled = true;
        }
        else
        {
            StartCoroutine(SetupImageRoutine());
        }
    }

    /// <summary>音乐开关：关 → 停止；开 → 有 clip 就播，没有就加载</summary>
    private void ApplyMusicSwitch()
    {
        if (musicSource == null) return;

        if (!useMusic)
        {
            musicSource.Stop();
            return;
        }

        if (musicSource.clip != null)
        {
            musicSource.loop   = musicLoop;
            musicSource.volume = musicVolume;
            musicSource.Play();
            ApplyVideoAudio();      // ★ 让视频音频状态重新决定是否静音音乐
        }
        else
        {
            StartCoroutine(SetupMusicRoutine());
        }
    }

    /// <summary>视频开关：关 → 停止并回落到图片；开 → 播放</summary>
    private void ApplyVideoSwitch()
    {
        if (videoPlayer == null) return;

        if (useVideo)
        {
            if (_videoPlaying)
            {
                videoPlayer.Play();
                if (_videoImage != null) _videoImage.enabled = true;
                HideAllTiles();
                ApplyVideoAudio();          // ← 重新应用音频状态
            }
            else
            {
                // 还没加载过 → 重新走一遍加载
                StartCoroutine(SetupVideoRoutine());
            }
        }
        else
        {
            videoPlayer.Stop();
            HideVideoImage();
            // ★ 视频停了 → 解除背景音乐的静音
            if (musicSource) musicSource.mute = false;

            // 关掉视频 → 回落到图片
            if (useImage) ApplyImageSwitch();
        }
    }

    // ==================== 音乐 ====================
    private IEnumerator SetupMusicRoutine()
    {
        if (!musicSource)
        {
            Debug.LogWarning("[ExternalBG] 未配置 musicSource，跳过背景音乐");
            yield break;
        }

        string path = Game_ExternalAssets.FirstExisting(
            $"{musicDir}/{musicName}.mp3",
            $"{musicDir}/{musicName}.wav",
            $"{musicDir}/{musicName}.ogg");

        if (path == null)
        {
            if (logDebug) Debug.Log("[ExternalBG] 没有背景音乐");
            yield break;
        }

        AudioClip clip = null;
        yield return Game_ExternalAssets.LoadAudio(path, streamMusic, c => clip = c);

        if (!clip) yield break;

        musicSource.clip   = clip;
        musicSource.loop   = musicLoop;
        musicSource.volume = musicVolume;
        musicSource.Play();

        if (logDebug) Debug.Log($"[ExternalBG] 播放音乐 {path} ({clip.length:F1}s)");
    }
}