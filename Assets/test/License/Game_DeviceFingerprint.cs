using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>
/// 设备指纹：把多个硬件/系统特征哈希成一个 32 位十六进制串。
///
/// 设计原则：
///   - 【零第三方依赖】不用 System.Management（.NET Standard 2.1 里没有）
///   - 【多源组合】单个来源可能变（换硬盘、重装系统），组合后更稳
///   - 【可抄写】32 位 hex，方便客户手工发给你
///
/// 采集来源：
///   1. SystemInfo.deviceUniqueIdentifier  （Unity 内置，硬件相关）
///   2. 系统盘卷序列号                      （P/Invoke，换硬盘才变）
///   3. CPU 核心数 + 内存大小               （弱，但作为兜底）
///
/// 注意：指纹不是"秘密"，它可以公开。安全性来自 RSA 私钥，不来自指纹。
/// </summary>
public static class Game_DeviceFingerprint
{
    private const string CacheKey = "DeviceFingerprint_v1";

    // ==================== P/Invoke ====================
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        IntPtr volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        IntPtr fileSystemNameBuffer, int fileSystemNameSize);

    // ==================== 公开 API ====================
    /// <summary>
    /// 取当前设备的指纹（32 位十六进制，大写）。
    /// 结果会缓存到 PlayerPrefs，避免每次启动都查硬件。
    /// </summary>
    public static string Get()
    {
        // 有缓存就用缓存（避免硬件查询开销）
        if (PlayerPrefs.HasKey(CacheKey))
        {
            string cached = PlayerPrefs.GetString(CacheKey);

            if (!string.IsNullOrEmpty(cached) && cached.Length == 32)
                return cached;
        }

        string fp = Compute();
        PlayerPrefs.SetString(CacheKey, fp);
        PlayerPrefs.Save();

        return fp;
    }

    /// <summary>强制重新计算（换硬件后调试用）</summary>
    public static string Recompute()
    {
        PlayerPrefs.DeleteKey(CacheKey);
        return Get();
    }

    /// <summary>清掉缓存</summary>
    public static void ClearCache()
    {
        PlayerPrefs.DeleteKey(CacheKey);
    }

    // ==================== 内部 ====================
    private static string Compute()
    {
        var raw = new StringBuilder();

        // ---- 1. Unity 的设备唯一标识 ----
        string uid = SystemInfo.deviceUniqueIdentifier;
        raw.Append(uid ?? "no-uid").Append('|');

        // ---- 2. 系统盘卷序列号 ----
        if (TryGetSystemVolumeSerial(out uint serial))
            raw.Append(serial.ToString("X8"));
        raw.Append('|');

        // ---- 3. 弱信息兜底 ----
        raw.Append(SystemInfo.processorCount).Append('|');
        raw.Append(SystemInfo.systemMemorySize).Append('|');
        raw.Append(SystemInfo.operatingSystem ?? "unknown");

        // ---- 4. SHA256 → 取前 16 字节 → 32 位 hex ----
        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToString()));

            var hex = new StringBuilder(32);
            for (int i = 0; i < 16; i++)
                hex.Append(hash[i].ToString("X2"));

            return hex.ToString();
        }
    }

    /// <summary>读系统盘（通常是 C:\）的卷序列号</summary>
    private static bool TryGetSystemVolumeSerial(out uint serial)
    {
        serial = 0;

        if (Application.platform != RuntimePlatform.WindowsPlayer &&
            Application.platform != RuntimePlatform.WindowsEditor)
            return false;

        try
        {
            string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            string root = string.IsNullOrEmpty(systemRoot)
                ? "C:\\"
                : System.IO.Path.GetPathRoot(systemRoot);

            if (string.IsNullOrEmpty(root)) root = "C:\\";

            return GetVolumeInformation(
                root,
                IntPtr.Zero, 0,
                out serial,
                out _, out _,
                IntPtr.Zero, 0);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Fingerprint] 读卷序列号失败：{e.Message}");
            return false;
        }
    }

    // ==================== 调试 ====================
    /// <summary>打印采集明细，方便排查"为什么指纹变了"</summary>
    public static void LogDetails()
    {
        TryGetSystemVolumeSerial(out uint serial);

        Debug.Log(
            "[Fingerprint] 明细：\n" +
            $"  deviceUniqueIdentifier = {SystemInfo.deviceUniqueIdentifier}\n" +
            $"  volumeSerial           = {serial:X8}\n" +
            $"  processorCount         = {SystemInfo.processorCount}\n" +
            $"  systemMemorySize       = {SystemInfo.systemMemorySize} MB\n" +
            $"  operatingSystem        = {SystemInfo.operatingSystem}\n" +
            $"  → 指纹                 = {Get()}");
    }
}
