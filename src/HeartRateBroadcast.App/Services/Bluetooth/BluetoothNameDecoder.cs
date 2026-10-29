using System.Text;
using HeartRateBroadcast.App.Models;

namespace HeartRateBroadcast.App.Services.Bluetooth;

public static class BluetoothNameDecoder
{
    private static readonly UTF8Encoding Utf8ThrowOnInvalid = new(false, true);

    private static Encoding? _gbEncoding;
    private static Encoding GbEncoding
    {
        get
        {
            if (_gbEncoding is not null) return _gbEncoding;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _gbEncoding = Encoding.GetEncoding("GB18030");
            }
            catch
            {
                _gbEncoding = Encoding.UTF8;
            }
            return _gbEncoding;
        }
    }

    /// <summary>
    /// 从原始数据段（0x09 完整名称 / 0x08 简短名称）或 Windows 传入的 LocalName 中解码出正确的设备名称。
    /// 自动处理 UTF-8、GBK/GB18030 编码以及 Windows 蓝牙栈常见的 Latin-1 Mojibake 乱码。
    /// </summary>
    public static string Decode(IEnumerable<BluetoothDataSection>? sections, string? windowsLocalName)
    {
        // 1. 优先从广播原始数据段提取 Complete Local Name (0x09) 或 Shortened Local Name (0x08)
        if (sections is not null)
        {
            foreach (var section in sections)
            {
                if (section.Type is 0x09 or 0x08 && section.Data.Length > 0)
                {
                    string decoded = DecodeRawBytes(section.Data);
                    if (!string.IsNullOrWhiteSpace(decoded))
                    {
                        string cleaned = CleanString(decoded);
                        if (cleaned != "未知设备")
                            return cleaned;
                    }
                }
            }
        }

        // 2. 如果数据段中没有，或者解码为空，处理 Windows 提供的 LocalName（自动修复 Mojibake 乱码）
        if (!string.IsNullOrWhiteSpace(windowsLocalName))
        {
            string repaired = TryRepairMojibake(windowsLocalName);
            return CleanString(repaired);
        }

        return "未知设备";
    }

    /// <summary>
    /// 从广播原始字节解码设备名称（智能探测 UTF-8 与 GB18030/GBK）。
    /// </summary>
    public static string DecodeRawBytes(ReadOnlySpan<byte> bytes)
    {
        // 去除尾部 null 终结符
        while (bytes.Length > 0 && bytes[^1] == 0)
            bytes = bytes[..^1];

        if (bytes.IsEmpty) return string.Empty;

        // 尝试 UTF-8
        try
        {
            string utf8Text = Utf8ThrowOnInvalid.GetString(bytes);
            if (!utf8Text.Contains('\uFFFD'))
                return utf8Text;
        }
        catch (DecoderFallbackException) { }

        // 尝试 GB18030 / GBK
        try
        {
            string gbkText = GbEncoding.GetString(bytes);
            if (ContainsCjk(gbkText) || !gbkText.Contains('\uFFFD'))
                return gbkText;
        }
        catch { }

        // 回退 UTF-8 容错解码
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 检测并修复 Windows 蓝牙协议栈将 UTF-8 或 GBK 字节按 Latin-1/Windows-1252 错误解码造成的 Mojibake 乱码。
    /// 例如将 "å°ç±³æè¡" 还原为 "小米手环"。
    /// </summary>
    public static string TryRepairMojibake(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // 检查字符串中是否包含异常密集的扩展 Latin 字符（典型的 UTF-8 错解迹象）
        int highLatinCount = 0;
        foreach (char c in text)
        {
            if (c is >= '\u0080' and <= '\u00FF')
                highLatinCount++;
        }

        // 如果没有高位 Latin 字符，无需修复
        if (highLatinCount == 0) return text;

        // 尝试将每个字符的低 8 位提取为字节流
        byte[] bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c > '\u00FF')
            {
                // 如果已包含非 Latin-1 字符，说明可能不是纯粹的单字节错解
                return text;
            }
            bytes[i] = (byte)c;
        }

        // 尝试作为 UTF-8 解码
        try
        {
            string recovered = Utf8ThrowOnInvalid.GetString(bytes);
            if (ContainsCjk(recovered) && !recovered.Contains('\uFFFD'))
                return recovered;
        }
        catch (DecoderFallbackException) { }

        // 尝试作为 GB18030 解码
        try
        {
            string recovered = GbEncoding.GetString(bytes);
            if (ContainsCjk(recovered))
                return recovered;
        }
        catch { }

        return text;
    }

    private static bool ContainsCjk(string text)
    {
        foreach (char c in text)
        {
            // CJK 统一表意文字与全角标点
            if (c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF' or >= '\u3000' and <= '\u303F')
                return true;
        }
        return false;
    }

    private static string CleanString(string text)
    {
        // 过滤不可见控制符，保留常规字符
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsControl(c) || c is '\t' or ' ')
                sb.Append(c);
        }
        string result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "未知设备" : result;
    }
}

