using System.Security.Cryptography;
using System.Xml.Linq;

namespace YiouCamera.Core;

/// <summary>
/// 配对码（Pincode）来源。协议 §8.5：
/// 1) 优先读厂商持久化文件 <c>%USERPROFILE%\Documents\YOFiles\YOCamSet.xml</c> 的 &lt;PinCode&gt;；
/// 2) 其次用调用方配置值；
/// 3) 兜底随机 8 位数字（仅本次进程有效，需配合二维码/手工配对，🟥 待真机验证设备是否校验）。
/// </summary>
public static class YocamPinCode
{
    /// <summary>厂商配置默认路径（实测本机 PinCode=92271885）。</summary>
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "YOFiles", "YOCamSet.xml");

    public static bool IsValid(string? pin)
    {
        if (pin is not { Length: 8 }) return false;
        foreach (char c in pin)
            if (c is < '0' or > '9') return false;
        return true;
    }

    /// <summary>从 YOCamSet.xml 读 PinCode。文件不存在/格式不对返回 false。</summary>
    public static bool TryReadFromConfig(out string pinCode, string? configPath = null)
    {
        pinCode = string.Empty;
        try
        {
            string path = configPath ?? DefaultConfigPath;
            if (!File.Exists(path)) return false;
            var doc = XDocument.Load(path);
            string? value = doc.Root?.Element("PinCode")?.Value?.Trim();
            if (!IsValid(value)) return false;
            pinCode = value!;
            return true;
        }
        catch (Exception)
        {
            // 解析失败（文件被占用/损坏）走兜底，不抛给上层
            return false;
        }
    }

    /// <summary>按优先级解析配对码：配置文件 → 配置值 → 随机。永不抛异常、永不返回空串。</summary>
    public static string Resolve(string? configuredPinCode = null, string? configPath = null, Action<string>? log = null)
    {
        if (TryReadFromConfig(out var fromFile, configPath))
        {
            log?.Invoke($"[pincode] 使用 YOCamSet.xml 配对码：{Mask(fromFile)}");
            return fromFile;
        }

        if (IsValid(configuredPinCode))
        {
            log?.Invoke($"[pincode] 使用配置配对码：{Mask(configuredPinCode!)}");
            return configuredPinCode!;
        }

        string random = GenerateRandom();
        log?.Invoke($"[pincode] 未找到 YOCamSet.xml/配置值，生成随机配对码：{random}（🟥 设备是否接受待真机验证）");
        return random;
    }

    /// <summary>生成 8 位十进制随机配对码（与厂商 Random.Next(10000000, 99999999) 同格式）。</summary>
    public static string GenerateRandom()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        uint v = BitConverter.ToUInt32(bytes);
        int n = 10_000_000 + (int)(v % 90_000_000u);
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Mask(string pin) => pin.Length <= 2 ? "**" : "**" + pin[^2..];
}
