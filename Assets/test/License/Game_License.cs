using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>激活码载荷（被 RSA 签名保护，客户端无法伪造）</summary>
[Serializable]
public class LicensePayload
{
    [Tooltip("绑定的设备指纹...（32 位 hex）")]
    public string fp;

    [Tooltip("到期日 yyyy-MM-dd。留空 = 永久")]
    public string exp;

    [Tooltip("客户标识，仅用于展示，不参与校验")]
    public string id;

    public int ver = 1;
}

public enum LicenseStatus
{
    Valid,
    Missing,
    BadFormat,
    BadSignature,
    FingerprintMismatch,
    Expired,
    ClockTampered,
}

/// <summary>
/// 离线激活码：RSA-2048 签名 + 设备指纹绑定。
///
/// 安全性来自一件事：客户端只有【公钥】，私钥只在你手上。
/// 所以任何人都无法伪造一个能通过验证的激活码——除非拿到你的私钥。
///
/// 激活码格式（三段，点分隔）：
///   GAME1.{payload 的 Base64}.{签名的 Base64}
///
/// payload 是 JSON：
///   {"fp":"A1B2...","exp":"2027-12-31","id":"客户名","ver":1}
///
/// 签名 = RSA-SHA256-PKCS1 over payload 的 UTF8 字节
/// </summary>
public static class Game_License
{
    // ==================== 需要你填的部分 ====================
    /// <summary>
    /// 客户端公钥。格式：MODULUS 的 Base64 + "|" + EXPONENT 的 Base64
    /// 用编辑器菜单 Tools/License/签发工具 生成密钥对后填到这里。
    /// 留空则所有激活码都会被判为签名无效。
    /// </summary>
    public const string PublicKey =
        "mxWbuc9IKbAkHzTjEFZd7IC+5rUWU301tgRs/CLIxvJB023QKlNsepXt0VJ71zz8hrGJ9c1RarNl8omTgjtsQ+OIn0nx/vMDojvIj2CS0GBSvxu0IDxE45d/rMQp11zSd1vAHu843m8STz6f9Bu0pOgJjNX8r7e384agX9RBnHE=|AQAB";

    // ==================== 常量 ====================
    public const string CodePrefix = "GAME1";

    private const string LastSeenKey = "License_LastSeenUtcTicks";

    // ==================== 主入口 ====================
    /// <summary>验证激活码</summary>
    public static LicenseStatus Validate(string code,
                                         out LicensePayload payload,
                                         out string message)
    {
        payload = null;
        message = "";

        // ---------- 1. 基本格式 ----------
        if (string.IsNullOrWhiteSpace(code))
        {
            message = "激活码为空";
            return LicenseStatus.Missing;
        }

        string[] parts = code.Trim().Split('.');

        if (parts.Length != 3 || parts[0] != CodePrefix)
        {
            message = $"格式错误：应为 {CodePrefix}.<载荷>.<签名>";
            return LicenseStatus.BadFormat;
        }

        byte[] payloadBytes;
        byte[] sigBytes;

        try
        {
            payloadBytes = Convert.FromBase64String(parts[1]);
            sigBytes     = Convert.FromBase64String(parts[2]);
        }
        catch
        {
            message = "Base64 解码失败（激活码可能被截断）";
            return LicenseStatus.BadFormat;
        }

        if (string.IsNullOrEmpty(PublicKey) || PublicKey.StartsWith("请用"))
        {
            message = "客户端未配置公钥：请把签发工具生成的公钥填进 Game_License.PublicKey";
            return LicenseStatus.BadSignature;
        }

        // ---------- 2. 验签 ----------
        try
        {
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(ParsePublicKey(PublicKey));

                bool ok = rsa.VerifyData(payloadBytes, sigBytes,
                                         HashAlgorithmName.SHA256,
                                         RSASignaturePadding.Pkcs1);

                if (!ok)
                {
                    message = "签名验证失败（激活码被篡改，或不是本软件签发的）";
                    return LicenseStatus.BadSignature;
                }
            }
        }
        catch (Exception e)
        {
            message = $"验签异常：{e.Message}";
            return LicenseStatus.BadSignature;
        }

        // ---------- 3. 解析载荷 ----------
        try
        {
            payload = JsonUtility.FromJson<LicensePayload>(
                Encoding.UTF8.GetString(payloadBytes));
        }
        catch
        {
            message = "载荷 JSON 解析失败";
            return LicenseStatus.BadFormat;
        }

        if (payload == null || string.IsNullOrEmpty(payload.fp))
        {
            message = "载荷缺少设备指纹 fp";
            return LicenseStatus.BadFormat;
        }

        // ---------- 4. 指纹比对 ----------
        string current = Game_DeviceFingerprint.Get();

        if (!string.Equals(payload.fp.Trim(), current, StringComparison.OrdinalIgnoreCase))
        {
            message = "设备指纹不匹配\n" +
                      $"授权给：{payload.fp}\n" +
                      $"当前设备：{current}";
            return LicenseStatus.FingerprintMismatch;
        }

        // ---------- 5. 时间防篡改 ----------
        if (IsClockRolledBack(out string clockMsg))
        {
            message = clockMsg;
            return LicenseStatus.ClockTampered;
        }

        // ---------- 6. 过期检查 ----------
        if (!string.IsNullOrEmpty(payload.exp))
        {
            if (!DateTime.TryParseExact(
                    payload.exp.Trim(), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime expire))
            {
                message = $"到期日格式错误：\"{payload.exp}\"（应为 yyyy-MM-dd）";
                return LicenseStatus.BadFormat;
            }

            if (DateTime.UtcNow.Date > expire.Date)
            {
                message = $"授权已过期（{payload.exp}）";
                return LicenseStatus.Expired;
            }
        }

        // ---------- 7. 通过 ----------
        message = string.IsNullOrEmpty(payload.id)
            ? "验证通过"
            : $"验证通过（{payload.id}）";

        return LicenseStatus.Valid;
    }

    /// <summary>从文件读取并验证</summary>
    public static LicenseStatus ValidateFile(string path,
                                             out LicensePayload payload,
                                             out string message)
    {
        payload = null;
        message = "";

        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            message = $"找不到授权文件：{path}";
            return LicenseStatus.Missing;
        }

        string code;

        try
        {
            code = System.IO.File.ReadAllText(path, Encoding.UTF8).Trim();
        }
        catch (Exception e)
        {
            message = $"读取授权文件失败：{e.Message}";
            return LicenseStatus.Missing;
        }

        return Validate(code, out payload, out message);
    }

    /// <summary>把状态转成人类可读的短标签（UI 用）</summary>
    public static string StatusText(LicenseStatus s)
    {
        switch (s)
        {
            case LicenseStatus.Valid:               return "已授权";
            case LicenseStatus.Missing:             return "未激活";
            case LicenseStatus.BadFormat:           return "激活码格式错误";
            case LicenseStatus.BadSignature:        return "签名无效";
            case LicenseStatus.FingerprintMismatch: return "设备不匹配";
            case LicenseStatus.Expired:             return "已过期";
            case LicenseStatus.ClockTampered:       return "系统时间异常";
            default:                                return s.ToString();
        }
    }

    // ==================== 密钥解析 ====================
    /// <summary>公钥格式：MODULUS|EXPONENT（各段 Base64）</summary>
    public static RSAParameters ParsePublicKey(string key)
    {
        // 去掉所有空白（复制时可能带换行/空格）
        key = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());
        
        string[] p = Split(key, 2, "MODULUS|EXPONENT");

        return new RSAParameters
        {
            Modulus  = Convert.FromBase64String(p[0]),
            Exponent = Convert.FromBase64String(p[1]),
        };
    }

    /// <summary>私钥格式：MODULUS|EXPONENT|P|Q|DP|DQ|INVERSEQ|D（各段 Base64）</summary>
    public static RSAParameters ParsePrivateKey(string key)
    {
        string[] p = Split(key, 8, "MODULUS|EXPONENT|P|Q|DP|DQ|INVERSEQ|D");

        return new RSAParameters
        {
            Modulus  = Convert.FromBase64String(p[0]),
            Exponent = Convert.FromBase64String(p[1]),
            P        = Convert.FromBase64String(p[2]),
            Q        = Convert.FromBase64String(p[3]),
            DP       = Convert.FromBase64String(p[4]),
            DQ       = Convert.FromBase64String(p[5]),
            InverseQ = Convert.FromBase64String(p[6]),
            D        = Convert.FromBase64String(p[7]),
        };
    }

    /// <summary>把 RSAParameters 序列化成我们的文本格式</summary>
    public static string FormatPublicKey(RSAParameters k)
        => $"{ToB64(k.Modulus)}|{ToB64(k.Exponent)}";

    public static string FormatPrivateKey(RSAParameters k)
        => $"{ToB64(k.Modulus)}|{ToB64(k.Exponent)}|{ToB64(k.P)}|{ToB64(k.Q)}|" +
           $"{ToB64(k.DP)}|{ToB64(k.DQ)}|{ToB64(k.InverseQ)}|{ToB64(k.D)}";

    private static string ToB64(byte[] b) => b == null ? "" : Convert.ToBase64String(b);

    private static string[] Split(string key, int need, string expect)
    {
        string[] p = (key ?? "").Trim().Split('|');

        if (p.Length < need)
            throw new FormatException($"密钥格式错误：需要 {need} 段（{expect}），实际 {p.Length} 段");

        return p;
    }

    // ==================== 时间防篡改 ====================
    /// <summary>
    /// 记录"见过的最晚 UTC 时间"。当前时间明显早于上次 → 说明系统时间被回拨。
    /// 允许 1 天宽容（时区、夏令时、NTP 校准）。
    /// </summary>
    private static bool IsClockRolledBack(out string message)
    {
        message = "";

        long now  = DateTime.UtcNow.Ticks;
        long last = 0L;

        string stored = PlayerPrefs.GetString(LastSeenKey, "");
        if (!string.IsNullOrEmpty(stored))
            long.TryParse(stored, out last);

        if (last > 0 && now < last - TimeSpan.TicksPerDay)
        {
            double hours = (last - now) / (double)TimeSpan.TicksPerHour;

            message = $"检测到系统时间被回拨约 {hours:F0} 小时，是在试图延长有效期吗...";
            return true;
        }

        // 只在时间前进时更新记录
        if (now > last)
        {
            PlayerPrefs.SetString(LastSeenKey, now.ToString());
            PlayerPrefs.Save();
        }

        return false;
    }
}
