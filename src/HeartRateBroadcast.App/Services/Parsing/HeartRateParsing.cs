using HeartRateBroadcast.App.Models;

namespace HeartRateBroadcast.App.Services.Parsing;

public interface IHeartRatePacketParser
{
    string Name { get; }
    HeartRateParseResult TryParse(BluetoothPacket packet);
}

public static class HeartRateMeasurementParser
{
    public static bool TryParse(ReadOnlySpan<byte> value, out ushort bpm)
    {
        bpm = 0;
        if (value.Length < 2) return false;
        bool sixteenBit = (value[0] & 0x01) != 0;
        if (sixteenBit)
        {
            if (value.Length < 3) return false;
            bpm = (ushort)(value[1] | value[2] << 8);
        }
        else bpm = value[1];
        return bpm > 0;
    }
}

public sealed class StandardHeartRateAdvertisementParser : IHeartRatePacketParser
{
    private const byte ServiceData16BitType = 0x16;
    public string Name => "Bluetooth SIG Heart Rate";

    public HeartRateParseResult TryParse(BluetoothPacket packet)
    {
        foreach (var section in packet.Sections.Where(s => s.Type == ServiceData16BitType))
        {
            if (section.Data.Length < 4 || section.Data[0] != 0x0D || section.Data[1] != 0x18) continue;
            if (!HeartRateMeasurementParser.TryParse(section.Data.AsSpan(2), out ushort bpm))
                return HeartRateParseResult.Invalid("标准心率服务数据长度无效");
            return HeartRateParseResult.Success(new(bpm, packet.Address, packet.ReceivedAt, HeartRateSource.Advertisement, Name));
        }
        return HeartRateParseResult.NoMatch();
    }
}

public sealed class HeartRateParserPipeline(IEnumerable<IHeartRatePacketParser> parsers)
{
    private readonly IHeartRatePacketParser[] _parsers = parsers.ToArray();
    public HeartRateParseResult Parse(BluetoothPacket packet)
    {
        foreach (var parser in _parsers)
        {
            try
            {
                var result = parser.TryParse(packet);
                if (result.Matched) return result;
            }
            catch (Exception ex) { return HeartRateParseResult.Invalid($"{parser.Name}: {ex.Message}"); }
        }
        return HeartRateParseResult.NoMatch();
    }
}
