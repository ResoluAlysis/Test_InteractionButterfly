using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 外部素材读取系统。
///
/// 路径规则（编辑器与构建后统一）：
///   编辑器： Application.dataPath = &lt;项目&gt;/Assets      → 上一级 = &lt;项目&gt;/
///   构建后： Application.dataPath = &lt;游戏根&gt;/xxx_Data   → 上一级 = &lt;游戏根&gt;/
///   所以 basePath = Application.dataPath + "/../"
///
/// 素材不用打进包，直接放这个目录即可热替换。
/// 所有读取都是异步的（协程），不会阻塞主线程。
/// </summary>
public static class Game_ExternalAssets
{
    /// <summary>素材根目录（exe 同级 / 项目根）</summary>
    public static string BasePath =>
        Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    /// <summary>是否使用 file:// 前缀（Android 之外的平台需要）</summary>
    private static bool NeedFilePrefix =>
        Application.platform != RuntimePlatform.Android;

    /// <summary>把相对路径拼成绝对路径</summary>
    public static string ResolvePath(string relativePath)
    {
        return Path.GetFullPath(Path.Combine(BasePath, relativePath));
    }

    /// <summary>拼出可用的 URL</summary>
    public static string ResolveUrl(string relativePath)
    {
        string full = ResolvePath(relativePath).Replace('\\', '/');
        return NeedFilePrefix ? "file://" + full : full;
    }

    /// <summary>文件是否存在</summary>
    public static bool Exists(string relativePath)
    {
        return File.Exists(ResolvePath(relativePath));
    }

    // ==================== 读文本 ====================
    public static IEnumerator LoadText(string relativePath, Action<string> onDone)
    {
        if (!Exists(relativePath))
        {
            Debug.LogWarning($"[ExternalAssets] 文件不存在: {relativePath}");
            onDone?.Invoke(null);
            yield break;
        }

        using var req = UnityWebRequest.Get(ResolveUrl(relativePath));
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[ExternalAssets] 读取失败 {relativePath}: {req.error}");
            onDone?.Invoke(null);
            yield break;
        }

        onDone?.Invoke(req.downloadHandler.text);
    }

    // ==================== 读音频 ====================
    /// <summary>
    /// 读音频。stream=true 适合长音乐（边播边读，省内存）；false 适合短音效。
    /// </summary>
    public static IEnumerator LoadAudio(string relativePath, bool stream, Action<AudioClip> onDone)
    {
        if (!Exists(relativePath))
        {
            Debug.LogWarning($"[ExternalAssets] 音频不存在: {relativePath}");
            onDone?.Invoke(null);
            yield break;
        }

        AudioType type = GetAudioType(relativePath);

        using var req = UnityWebRequestMultimedia.GetAudioClip(ResolveUrl(relativePath), type);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[ExternalAssets] 音频读取失败 {relativePath}: {req.error}");
            onDone?.Invoke(null);
            yield break;
        }

        AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
        if (clip == null)
        {
            Debug.LogWarning($"[ExternalAssets] 音频解码失败: {relativePath}（格式不支持？）");
            onDone?.Invoke(null);
            yield break;
        }

        clip.name = Path.GetFileName(relativePath);
        onDone?.Invoke(clip);
    }

    private static AudioType GetAudioType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        switch (ext)
        {
            case ".mp3":  return AudioType.MPEG;
            case ".wav":  return AudioType.WAV;
            case ".ogg":  return AudioType.OGGVORBIS;
            case ".aiff":
            case ".aif":  return AudioType.AIFF;
            default:
                Debug.LogWarning($"[ExternalAssets] 未知音频扩展名 {ext}，按 MPEG 处理");
                return AudioType.MPEG;
        }
    }

    // ==================== 读图片 ====================
    public static IEnumerator LoadTexture(string relativePath, Action<Texture2D> onDone)
    {
        if (!Exists(relativePath))
        {
            Debug.LogWarning($"[ExternalAssets] 图片不存在: {relativePath}");
            onDone?.Invoke(null);
            yield break;
        }

        using var req = UnityWebRequestTexture.GetTexture(ResolveUrl(relativePath));
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[ExternalAssets] 图片读取失败 {relativePath}: {req.error}");
            onDone?.Invoke(null);
            yield break;
        }

        Texture2D tex = DownloadHandlerTexture.GetContent(req);
        tex.name = Path.GetFileName(relativePath);
        onDone?.Invoke(tex);
    }
    
        
    // ==================== 读图片为 Sprite ====================
    /// <summary>
    /// 读图片并创建 Sprite（供 UI Image 使用）。
    /// pixelsPerUnit 是 UI 无关的占位值，UI 只看 sprite.rect；给 100 即可。
    /// </summary>
    public static IEnumerator LoadSprite(string relativePath, Action<Sprite> onDone,
        float pixelsPerUnit = 100f)
    {
        Texture2D tex = null;
        yield return LoadTexture(relativePath, t => tex = t);

        if (tex == null)
        {
            onDone?.Invoke(null);
            yield break;
        }

        // ★ 关键：UI Image 的 Tiled 模式依赖纹理 Repeat
        tex.wrapMode   = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;

        // 运行时创建的 Sprite 用 Simple 即可；Tiled 由 Image.type 控制
        var sprite = Sprite.Create(
            tex,
            new Rect(0f, 0f, tex.width, tex.height),
            new Vector2(0.5f, 0.5f),        // pivot 居中
            pixelsPerUnit,
            0,                              // extrude
            SpriteMeshType.FullRect);       // ★ FullRect：Tiled 模式必须用它

        sprite.name = System.IO.Path.GetFileNameWithoutExtension(relativePath);

        onDone?.Invoke(sprite);
    }

    // ==================== 便捷：按候选列表依次尝试 ====================
    /// <summary>依次尝试多个候选路径，用第一个存在的</summary>
    public static string FirstExisting(params string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
            if (!string.IsNullOrEmpty(candidates[i]) && Exists(candidates[i]))
                return candidates[i];

        return null;
    }
}