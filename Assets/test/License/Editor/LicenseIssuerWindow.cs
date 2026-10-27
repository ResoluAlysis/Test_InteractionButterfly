using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 授权签发工具（只在你自己的机器上用，绝不随客户端发布）。
///
/// 工作流：
///   ① 一次性：生成密钥对 → 私钥自己保管，公钥填进 Game_License.PublicKey
///   ② 客户把设备指纹发给你（客户端 Console 或 UI 显示）
///   ③ 输入指纹 + 到期日 + 客户标识 → 生成激活码
///   ④ 把激活码给客户，客户端放到 license.key（exe 同级）
///
/// 私钥文件丢了 → 所有已发出的激活码都无法再续签。务必备份。
/// </summary>
public class LicenseIssuerWindow : EditorWindow
{
    private string _privateKey     = "";
    private string _privateKeyPath = "";

    private string _fingerprint = "";
    private string _expireDate  = "";
    private string _customerId  = "";

    private string _resultCode    = "";
    private string _resultMessage = "";
    private string _publicKeyText = "";

    private Vector2 _scroll;

    // ==================== 菜单 ====================
    [MenuItem("Tools/License/签发工具")]
    private static void Open()
    {
        var w = GetWindow<LicenseIssuerWindow>("License Issuer");
        w.minSize = new Vector2(620, 560);
        w.Show();
    }

    // ==================== 界面 ====================
    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.Space(4);

        // ---------------- 1. 密钥 ----------------
        EditorGUILayout.LabelField("① 密钥", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        _privateKeyPath = EditorGUILayout.TextField("私钥文件", _privateKeyPath);

        if (GUILayout.Button("浏览", GUILayout.Width(56)))
        {
            string p = EditorUtility.OpenFilePanel("选择私钥文件", "", "txt");

            if (!string.IsNullOrEmpty(p))
            {
                _privateKeyPath = p;
                LoadPrivateKey();
            }
        }

        if (GUILayout.Button("生成新密钥对", GUILayout.Width(100)))
            GenerateKeyPair();

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox(
            "私钥只存在于本机，绝不放进客户端。客户端只需要公钥。\n" +
            "私钥丢失 → 已发出的激活码无法续签，请务必备份。",
            MessageType.Warning);

        if (string.IsNullOrEmpty(_privateKey))
        {
            EditorGUILayout.HelpBox("尚未加载私钥。点「浏览」载入，或点「生成新密钥对」。",
                                    MessageType.Info);
        }
        else
        {
            // 显示当前私钥对应的公钥，方便复制进代码
            EditorGUILayout.LabelField("公钥（填进 Game_License.PublicKey）");

            EditorGUILayout.BeginHorizontal();

            _publicKeyText = EditorGUILayout.TextArea(
                string.IsNullOrEmpty(_publicKeyText) ? RenderPublicKey() : _publicKeyText,
                GUILayout.Height(46));

            if (GUILayout.Button("复制", GUILayout.Width(56), GUILayout.Height(46)))
            {
                string pk = string.IsNullOrEmpty(_publicKeyText) ? RenderPublicKey() : _publicKeyText;

                if (!string.IsNullOrEmpty(pk))
                {
                    EditorGUIUtility.systemCopyBuffer = pk;
                    _resultMessage = "公钥已复制到剪贴板";
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(8);

        // ---------------- 2. 签发 ----------------
        EditorGUILayout.LabelField("② 签发激活码", EditorStyles.boldLabel);

        _fingerprint = EditorGUILayout.TextField("设备指纹（32 位 hex）", _fingerprint);
        _expireDate  = EditorGUILayout.TextField("到期日（yyyy-MM-dd，空 = 永久）", _expireDate);
        _customerId  = EditorGUILayout.TextField("客户标识（仅展示）", _customerId);

        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();

        GUI.enabled = !string.IsNullOrEmpty(_privateKey);

        if (GUILayout.Button("生成激活码", GUILayout.Height(28)))
            Issue();

        GUI.enabled = true;

        if (GUILayout.Button("验一下（用公钥自检）", GUILayout.Height(28)))
            SelfVerify();

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(8);

        // ---------------- 3. 结果 ----------------
        EditorGUILayout.LabelField("③ 结果", EditorStyles.boldLabel);

        if (!string.IsNullOrEmpty(_resultMessage))
            EditorGUILayout.HelpBox(_resultMessage, MessageType.None);

        if (!string.IsNullOrEmpty(_resultCode))
        {
            EditorGUILayout.LabelField("激活码（已复制到剪贴板）");

            _resultCode = EditorGUILayout.TextArea(_resultCode, GUILayout.Height(90));

            if (GUILayout.Button("再次复制"))
                EditorGUIUtility.systemCopyBuffer = _resultCode;
        }

        EditorGUILayout.Space(8);

        // ---------------- 4. 当前设备（调试用）----------------
        EditorGUILayout.LabelField("④ 当前这台机器的指纹（调试用）", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField(Game_DeviceFingerprint.Get());

        if (GUILayout.Button("填入上方", GUILayout.Width(80)))
            _fingerprint = Game_DeviceFingerprint.Get();

        if (GUILayout.Button("打印明细", GUILayout.Width(80)))
            Game_DeviceFingerprint.LogDetails();

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndScrollView();
    }

    // ==================== 密钥 ====================
    private void GenerateKeyPair()
    {
        try
        {
            using (var rsa = RSA.Create())
            {
                // .NET 默认 2048 位；小概率是别的值，强制一下
                if (rsa.KeySize != 2048)
                    rsa.KeySize = 2048;

                RSAParameters priv = rsa.ExportParameters(true);
                RSAParameters pub  = rsa.ExportParameters(false);

                string privText = Game_License.FormatPrivateKey(priv);
                string pubText  = Game_License.FormatPublicKey(pub);

                string path = EditorUtility.SaveFilePanel(
                    "保存私钥（请备份）", "", "license_private.txt", "txt");

                if (string.IsNullOrEmpty(path))
                    return;

                File.WriteAllText(path, privText, new UTF8Encoding(false));

                _privateKeyPath = path;
                _privateKey     = privText;
                _publicKeyText  = pubText;

                EditorGUIUtility.systemCopyBuffer = pubText;

                _resultMessage =
                    $"密钥对已生成（{rsa.KeySize} 位）\n" +
                    $"私钥：{path}\n" +
                    "公钥已复制到剪贴板 —— 粘贴进 Game_License.PublicKey";

                Debug.Log($"[License] 公钥（填入 Game_License.PublicKey）：\n{pubText}");
                Debug.Log($"[License] 私钥已保存：{path}");
            }
        }
        catch (Exception e)
        {
            _resultMessage = $"生成密钥对失败：{e.Message}";
            Debug.LogError($"[License] {e}");
        }
    }

    private void LoadPrivateKey()
    {
        try
        {
            _privateKey    = File.ReadAllText(_privateKeyPath, Encoding.UTF8).Trim();
            _publicKeyText = RenderPublicKey();

            // 自检：能不能解析
            using (var rsa = RSA.Create())
                rsa.ImportParameters(Game_License.ParsePrivateKey(_privateKey));

            _resultMessage = $"私钥已加载：{_privateKeyPath}";
        }
        catch (Exception e)
        {
            _privateKey    = "";
            _publicKeyText = "";
            _resultMessage = $"私钥加载失败：{e.Message}";
            Debug.LogError($"[License] {e}");
        }
    }

    /// <summary>从私钥推算公钥文本</summary>
    private string RenderPublicKey()
    {
        if (string.IsNullOrEmpty(_privateKey)) return "";

        try
        {
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(Game_License.ParsePrivateKey(_privateKey));

                RSAParameters pub = rsa.ExportParameters(false);
                return Game_License.FormatPublicKey(pub);
            }
        }
        catch
        {
            return "";
        }
    }

    // ==================== 签发 ====================
    private void Issue()
    {
        _resultCode = "";

        // ---- 校验输入 ----
        string fp = (_fingerprint ?? "").Trim().ToUpperInvariant();

        if (fp.Length != 32)
        {
            _resultMessage = $"设备指纹应为 32 位十六进制，当前 {fp.Length} 位";
            return;
        }

        foreach (char c in fp)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');

            if (!isHex)
            {
                _resultMessage = $"设备指纹含非十六进制字符：'{c}'";
                return;
            }
        }

        string exp = (_expireDate ?? "").Trim();

        if (!string.IsNullOrEmpty(exp))
        {
            if (!DateTime.TryParseExact(exp, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                _resultMessage = "到期日格式应严格为 yyyy-MM-dd（留空 = 永久）";
                return;
            }
        }

        // ---- 构造载荷 ----
        var payload = new LicensePayload
        {
            fp  = fp,
            exp = exp,
            id  = (_customerId ?? "").Trim(),
            ver = 1,
        };

        string json  = JsonUtility.ToJson(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        try
        {
            // ---- 签名 ----
            byte[] sig;

            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(Game_License.ParsePrivateKey(_privateKey));

                sig = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            _resultCode = $"{Game_License.CodePrefix}." +
                          $"{Convert.ToBase64String(bytes)}." +
                          $"{Convert.ToBase64String(sig)}";

            EditorGUIUtility.systemCopyBuffer = _resultCode;

            _resultMessage = $"签发成功\n指纹：{fp}\n到期：{(string.IsNullOrEmpty(exp) ? "永久" : exp)}\n" +
                             "激活码已复制到剪贴板";

            Debug.Log($"[License] 激活码：\n{_resultCode}");
            Debug.Log($"[License] 载荷：{json}");
        }
        catch (Exception e)
        {
            _resultMessage = $"签名失败：{e.Message}";
            Debug.LogError($"[License] {e}");
        }
    }

    // ==================== 自检 ====================
    private void SelfVerify()
    {
        if (string.IsNullOrEmpty(_resultCode))
        {
            _resultMessage = "还没有生成激活码";
            return;
        }

        if (string.IsNullOrEmpty(_privateKey))
        {
            _resultMessage = "没有私钥，无法推算公钥";
            return;
        }

        try
        {
            // 用私钥推算公钥，然后独立验一遍签名
            string pubText = RenderPublicKey();

            string[] parts = _resultCode.Split('.');
            if (parts.Length != 3) { _resultMessage = "激活码分段不对"; return; }

            byte[] payloadBytes = Convert.FromBase64String(parts[1]);
            byte[] sigBytes     = Convert.FromBase64String(parts[2]);

            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(Game_License.ParsePublicKey(pubText));

                bool ok = rsa.VerifyData(payloadBytes, sigBytes,
                                         HashAlgorithmName.SHA256,
                                         RSASignaturePadding.Pkcs1);

                _resultMessage = ok
                    ? "自检通过：签名有效（说明公钥/私钥配对正确）"
                    : "自检失败：签名无效 —— 检查密钥是否配错";
            }
        }
        catch (Exception e)
        {
            _resultMessage = $"自检异常：{e.Message}";
        }
    }
}
