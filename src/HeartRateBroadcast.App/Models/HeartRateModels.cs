namespace HeartRateBroadcast.App.Models;

public enum HeartRateSource { Advertisement, Gatt }
public enum HeartRateStatusKind { BluetoothUnavailable, Scanning, Waiting, Live, Stale, Error }
public enum BluetoothScanStatus { Idle, Starting, Scanning, Stopping, Error }

public sealed record BluetoothScanState(BluetoothScanStatus Status, string Message, string? ErrorCode = null)
{
    public bool IsScanning => Status is BluetoothScanStatus.Starting or BluetoothScanStatus.Scanning;
    public static BluetoothScanState Idle { get; } = new(BluetoothScanStatus.Idle, "扫描已停止");
}

public sealed record BluetoothDataSection(byte Type, byte[] Data);

public sealed record BluetoothPacket(
    ulong Address,
    string Name,
    short Rssi,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<Guid> ServiceUuids,
    IReadOnlyList<BluetoothDataSection> Sections,
    ushort? ManufacturerId,
    byte[] ManufacturerData);

public sealed record HeartRateSample(
    ushort BeatsPerMinute,
    ulong BluetoothAddress,
    DateTimeOffset ReceivedAt,
    HeartRateSource Source,
    string ParserName);

public sealed record HeartRateParseResult(bool Matched, HeartRateSample? Sample, string? Error)
{
    public static HeartRateParseResult NoMatch() => new(false, null, null);
    public static HeartRateParseResult Success(HeartRateSample sample) => new(true, sample, null);
    public static HeartRateParseResult Invalid(string error) => new(true, null, error);
}

public sealed record HeartRateDisplayState(
    HeartRateStatusKind Kind,
    ushort? BeatsPerMinute,
    string Message,
    DateTimeOffset? UpdatedAt,
    ulong? DeviceAddress)
{
    public static HeartRateDisplayState Scanning { get; } = new(HeartRateStatusKind.Scanning, null, "正在扫描", null, null);
}

public sealed record DiscoveredDevice(
    ulong Address,
    string Name,
    short Rssi,
    DateTimeOffset LastSeen,
    bool IsHeartRateCapable)
{
    public string AddressText => Address.ToString("X12");
}
