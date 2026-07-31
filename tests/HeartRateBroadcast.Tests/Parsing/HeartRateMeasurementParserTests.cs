using HeartRateBroadcast.App.Services.Parsing;

namespace HeartRateBroadcast.Tests.Parsing;

public class HeartRateMeasurementParserTests
{
    [Fact] public void ParsesEightBitValue() { Assert.True(HeartRateMeasurementParser.TryParse([0x00, 72], out ushort bpm)); Assert.Equal((ushort)72, bpm); }
    [Fact] public void ParsesSixteenBitValue() { Assert.True(HeartRateMeasurementParser.TryParse([0x01, 0x2C, 0x01], out ushort bpm)); Assert.Equal((ushort)300, bpm); }
    [Fact] public void RejectsTruncatedValue() => Assert.False(HeartRateMeasurementParser.TryParse([0x01, 0x2C], out _));
}
