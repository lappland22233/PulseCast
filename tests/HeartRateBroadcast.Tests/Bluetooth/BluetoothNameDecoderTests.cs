using System.Text;
using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.Bluetooth;

namespace HeartRateBroadcast.Tests.Bluetooth;

public class BluetoothNameDecoderTests
{
    static BluetoothNameDecoderTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void Decode_ExtractsUtf8ChineseNameFromCompleteNameSection()
    {
        byte[] utf8Bytes = Encoding.UTF8.GetBytes("小米手环 8 Pro\0");
        var sections = new BluetoothDataSection[]
        {
            new(0x01, [0x06]),
            new(0x09, utf8Bytes)
        };

        string name = BluetoothNameDecoder.Decode(sections, "WrongLocalName");
        Assert.Equal("小米手环 8 Pro", name);
    }

    [Fact]
    public void Decode_ExtractsGbkChineseNameFromSection_WhenDeviceUsesGbk()
    {
        byte[] gbkBytes = Encoding.GetEncoding("GB18030").GetBytes("迈金智能心率带\0");
        var sections = new BluetoothDataSection[]
        {
            new(0x08, gbkBytes)
        };

        string name = BluetoothNameDecoder.Decode(sections, null);
        Assert.Equal("迈金智能心率带", name);
    }

    [Fact]
    public void TryRepairMojibake_RestoresUtf8MislabeledAsLatin1()
    {
        string original = "华为运动手环";
        byte[] utf8 = Encoding.UTF8.GetBytes(original);
        // 模拟 Windows 蓝牙栈按 Latin-1 将每个字节映射为 char
        string mojibake = Encoding.GetEncoding("ISO-8859-1").GetString(utf8);

        // 确认生成的确是典型的乱码字符串
        Assert.NotEqual(original, mojibake);

        // 通过我们的解码器自动侦测并还原
        string restored = BluetoothNameDecoder.TryRepairMojibake(mojibake);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void TryRepairMojibake_RestoresGbkMislabeledAsLatin1()
    {
        string original = "高驰心率表";
        byte[] gbk = Encoding.GetEncoding("GB18030").GetBytes(original);
        string mojibake = Encoding.GetEncoding("ISO-8859-1").GetString(gbk);

        Assert.NotEqual(original, mojibake);

        string restored = BluetoothNameDecoder.TryRepairMojibake(mojibake);
        Assert.Equal(original, restored);
    }

    [Theory]
    [InlineData("Garmin HRM-Pro")]
    [InlineData("Polar H10 12345678")]
    [InlineData("Wahoo TICKR X")]
    [InlineData("Apple Watch")]
    public void Decode_PreservesPureAsciiNames(string asciiName)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(asciiName);
        var sections = new BluetoothDataSection[]
        {
            new(0x09, bytes)
        };

        string name = BluetoothNameDecoder.Decode(sections, asciiName);
        Assert.Equal(asciiName, name);
    }

    [Fact]
    public void Decode_ReturnsUnknown_WhenNoNameAvailable()
    {
        var sections = new BluetoothDataSection[]
        {
            new(0x01, [0x06])
        };

        string name = BluetoothNameDecoder.Decode(sections, "");
        Assert.Equal("未知设备", name);
    }
}

