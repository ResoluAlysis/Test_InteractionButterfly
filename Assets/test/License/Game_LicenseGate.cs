using System;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 授权门禁：常驻单例，负责
///   ① 读取并验证授权文件
///   ② 决定 Logo 之后进主场景还是激活场景
///   ③ 供激活场景调用，保存新激活码后转场
///
/// 授权文件的搜索顺序：
///   1. &lt;exe 同级&gt;/licenseFile
///   2. &lt;persistentDataPath&gt;/licenseFile   ← exe 目录无写权限时的兜底
///
/// 保存时优先写 exe 同级；没权限则写 persistentDataPath。
/// </summary>
[DefaultExecutionOrder(-900)]       // 早于 Game_SplashLogo
public class Game_LicenseGate : MonoBehaviour
{
    public static Game_LicenseGate Instance { get; private set; }

    // ==================== 配置 ====================
    [Header("授权文件")]
    [Tooltip("相对 exe 同级目录的路径（也可只写文件名）")]
    public string licenseFile = "license.key";

    [Header("场景")]
    [Tooltip("校验通过后进入的场景")]
    public string mainSceneName = "01_Main";

    [Tooltip("校验失败后进入的激活场景。留空则不转场")]
    public string activationSceneName = "02_Activation";

    [Header("行为")]
    [Tooltip("校验失败时是否阻止进入主场景")]
    public bool blockOnFailure = true;

    [Tooltip("开发用：跳过校验（打包前一定关掉）")]
    public bool skipCheck = false;

    [Header("调试")]
    public bool logDebug = true;

    // ==================== 状态 ====================
    public static bool           IsLicensed  { get; private set; }
    public static LicenseStatus  Status      { get; private set; } = LicenseStatus.Missing;
    public static string         LastMessage { get; private set; } = "";
    public static LicensePayload Payload     { get; private set; }

    // ==================== 生命周期 ====================
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;

        if (transform.parent != null)
        {
            Debug.LogWarning($"[LicenseGate] {name} 挂在子物体上，DontDestroyOnLoad 会失效。已自动提到根级。");
            transform.SetParent(null);
        }

        DontDestroyOnLoad(gameObject);

        // 启动时先校验一次，方便其它场景读状态
        Check();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ==================== 路径 ====================
    /// <summary>所有候选授权文件路径（按优先级）</summary>
    public string[] GetCandidatePaths()
    {
        return new[]
        {
            Game_ExternalAssets.ResolvePath(licenseFile),
            Path.Combine(Application.persistentDataPath, Path.GetFileName(licenseFile)),
        };
    }

    /// <summary>实际生效的授权文件路径（都找不到时返回首选路径）</summary>
    public string ActivePath
    {
        get
        {
            string[] paths = GetCandidatePaths();

            for (int i = 0; i < paths.Length; i++)
                if (File.Exists(paths[i])) return paths[i];

            return paths[0];
        }
    }

    // ==================== 校验 ====================
    /// <summary>校验授权，更新静态状态并返回结果</summary>
    public bool Check()
    {
        if (skipCheck)
        {
            IsLicensed  = true;
            Status      = LicenseStatus.Valid;
            LastMessage = "已跳过校验（skipCheck = true）";

            if (logDebug) Debug.Log($"[LicenseGate] {LastMessage}");
            return true;
        }

        string[] paths = GetCandidatePaths();

        LicenseStatus  bestStatus  = LicenseStatus.Missing;
        LicensePayload bestPayload = null;
        string         bestMessage = "找不到授权文件...";
        string         bestPath    = paths[0];

        for (int i = 0; i < paths.Length; i++)
        {
            if (!File.Exists(paths[i])) continue;

            var st = Game_License.ValidateFile(paths[i], out var payload, out string message);

            if (st == LicenseStatus.Valid)
            {
                IsLicensed  = true;
                Status      = st;
                Payload     = payload;
                LastMessage = message;

                if (logDebug)
                    Debug.Log($"[LicenseGate] 校验通过：{paths[i]}\n" +
                              $"  客户：{payload.id}\n" +
                              $"  到期：{(string.IsNullOrEmpty(payload.exp) ? "永久" : payload.exp)}");

                return true;
            }

            // 记录有信息的那次失败
            if (st != LicenseStatus.Missing)
            {
                bestStatus  = st;
                bestPayload = payload;
                bestMessage = message;
                bestPath    = paths[i];
            }
        }

        IsLicensed  = false;
        Status      = bestStatus;
        Payload     = bestPayload;
        LastMessage = bestMessage;

        Debug.LogError(
            $"[LicenseGate] 授权校验失败：{Game_License.StatusText(Status)}\n" +
            $"  {LastMessage}\n" +
            $"  查找路径：{string.Join(" | ", paths)}\n" +
            $"  设备指纹：{Game_DeviceFingerprint.Get()}");

        return false;
    }

    // ==================== 保存 ====================
    /// <summary>
    /// 保存激活码并重新校验。
    /// 优先写 exe 同级；无权限则写 persistentDataPath。
    /// </summary>
    public bool SaveAndValidate(string code, out LicenseStatus status, out string message)
    {
        status  = LicenseStatus.BadFormat;
        message = "";

        if (string.IsNullOrWhiteSpace(code))
        {
            status  = LicenseStatus.Missing;
            message = "激活码为空...";
            return false;
        }

        // 先验签，避免把垃圾写进文件
        status = Game_License.Validate(code.Trim(), out var payload, out message);

        if (status != LicenseStatus.Valid)
        {
            Debug.LogWarning($"[LicenseGate] 激活失败：{Game_License.StatusText(status)}\n{message}");
            return false;
        }

        // ---- 写文件 ----
        string writtenPath = null;

        foreach (string path in GetCandidatePaths())
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, code.Trim(), new System.Text.UTF8Encoding(false));
                writtenPath = path;
                break;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LicenseGate] 写入失败：{path}\n{e.Message}");
            }
        }

        if (writtenPath == null)
        {
            // 写不进去：仍然放行本次运行，但明确告知重启会失效
            IsLicensed  = true;
            Status      = LicenseStatus.Valid;
            Payload     = payload;
            LastMessage = message;

            message += "\n\n⚠️ 密钥无法写入磁盘, 激活仅本次运行有效, 重启后需重新激活... (权限不足, 可尝试以管理员模式运行游戏)";

            Debug.LogWarning($"[LicenseGate] {message}");
            return true;
        }

        // ---- 重读校验，确保落盘内容能读回 ----
        bool ok = Check();

        message = ok
            ? $"激活成功 (已保存到 {writtenPath})"
            : $"激活码已写入 {writtenPath},但读回校验失败：{LastMessage}...";

        return ok;
    }

    // ==================== 转场 ====================
    /// <summary>Logo 播完后调用：通过进主场景，失败进激活场景</summary>
    public void ProceedAfterSplash()
    {
        if (skipCheck || Check())
        {
            GoTo(mainSceneName);
            return;
        }

        if (!blockOnFailure)
        {
            Debug.LogWarning("[LicenseGate] blockOnFailure = false，仍然进入主场景");
            GoTo(mainSceneName);
            return;
        }

        if (string.IsNullOrEmpty(activationSceneName))
        {
            Debug.LogError("[LicenseGate] 未配置激活场景（activationSceneName 为空），无法继续");
            return;
        }

        GoTo(activationSceneName);
    }

    /// <summary>激活场景里激活成功后调用</summary>
    public void GoToMain() => GoTo(mainSceneName);

    private void GoTo(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogError("[LicenseGate] 场景名为空");
            return;
        }

        if (logDebug) Debug.Log($"[LicenseGate] 转场 → {sceneName}");

        var fade = Game_ScreenFade.Instance;

        if (fade != null)
        {
            fade.TransitionTo(sceneName, 0.4f, 0.4f);
            return;
        }

        SceneManager.LoadSceneAsync(sceneName);
    }

    // ==================== 静态入口 ====================
    /// <summary>供 Game_SplashLogo 调用。返回 false 表示场景里没有 Gate。</summary>
    public static bool TryProceedAfterSplash()
    {
        if (Instance == null) return false;

        Instance.ProceedAfterSplash();
        return true;
    }
}
