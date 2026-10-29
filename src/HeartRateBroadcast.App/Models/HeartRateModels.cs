using CommunityToolkit.Mvvm.ComponentModel;

namespace HeartRateBroadcast.App.Models;

public enum HeartRateSource { Advertisement, Gatt }
public enum HeartRateStatusKind { BluetoothUnavailable, Scanning, Waiting, Live, Stale, Error }
public enum BluetoothScanStatus { Idle, Starting, Scanning, Stopping, Error }

public enum HeartRateDeviceCategory
{
    Broadcasting,      // 正在广播心率（免连接）
    GattService,       // 声明了心率服务 0x180D
    CandidateName,     // 运动/手环/心率品牌候选设备
    GenericBle         // 普通未知/无关 BLE 设备
}

public static class HeartRateDeviceClassifier
{
    private static readonly string[] Keywords =
    [
        "HEART", "HRM", "HR", "PULSE", "CARDIO", "CHEST", "ARMBAND", "STRAP",
        "BAND", "WATCH", "FIT",
        "GARMIN", "POLAR", "COROS", "SUUNTO", "WAHOO", "MAGENE", "COOSPO",
        "FITBIT", "HUAWEI", "HONOR", "XIAOMI", "AMAZFIT", "WHOOP", "DECATHLON",
        "IGPSPORT", "XOSS", "TICKR", "SPO2", "HEALTH"
    ];

    public static bool MatchesHeartRateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "未知设备") return false;
        var upper = name.ToUpperInvariant();
        return Keywords.Any(k => upper.Contains(k, StringComparison.Ordinal));
    }
}

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

public sealed partial class DiscoveredDevice : ObservableObject
{
    public ulong Address { get; }
    public string AddressText => Address.ToString("X12");
    [ObservableProperty] private string name;
    [ObservableProperty] private short rssi;
    [ObservableProperty] private DateTimeOffset lastSeen;
    [ObservableProperty] private bool isHeartRateCapable;
    [ObservableProperty] private bool isHeartRateBroadcasting;
    [ObservableProperty] private bool hasHeartRateService;

    public bool IsNamedDevice => !string.IsNullOrWhiteSpace(Name) && Name != "未知设备";
    public bool IsCandidateName => HeartRateDeviceClassifier.MatchesHeartRateName(Name);
    public bool IsHeartRateCandidate => IsHeartRateBroadcasting || HasHeartRateService || IsCandidateName || IsHeartRateCapable;

    public int SignalBars => Rssi switch
    {
        >= -60 => 4,
        >= -72 => 3,
        >= -84 => 2,
        >= -94 => 1,
        _ => 0,
    };

    public HeartRateDeviceCategory Category =>
        IsHeartRateBroadcasting ? HeartRateDeviceCategory.Broadcasting :
        HasHeartRateService ? HeartRateDeviceCategory.GattService :
        (IsCandidateName || IsHeartRateCapable) ? HeartRateDeviceCategory.CandidateName :
        HeartRateDeviceCategory.GenericBle;

    public string CategoryBadgeText => Category switch
    {
        HeartRateDeviceCategory.Broadcasting => "广播心率",
        HeartRateDeviceCategory.GattService => "GATT心率",
        HeartRateDeviceCategory.CandidateName => "心率/手环",
        _ => "其他BLE",
    };

    public string SignalStrengthDescription => Rssi switch
    {
        >= -60 => $"极佳 · {Rssi} dBm",
        >= -72 => $"良好 · {Rssi} dBm",
        >= -84 => $"一般 · {Rssi} dBm",
        _ => $"较弱 · {Rssi} dBm",
    };

    public DiscoveredDevice(
        ulong address,
        string name,
        short rssi,
        DateTimeOffset lastSeen,
        bool isHeartRateCapable,
        bool isHeartRateBroadcasting = false,
        bool hasHeartRateService = false)
    {
        Address = address;
        this.name = name;
        this.rssi = rssi;
        this.lastSeen = lastSeen;
        this.isHeartRateCapable = isHeartRateCapable;
        this.isHeartRateBroadcasting = isHeartRateBroadcasting;
        this.hasHeartRateService = hasHeartRateService;
    }

    public void Update(
        short newRssi,
        DateTimeOffset seen,
        string? newName,
        bool capable,
        bool broadcasting = false,
        bool hasGattService = false)
    {
        Rssi = newRssi;
        LastSeen = seen;
        if (!string.IsNullOrWhiteSpace(newName) && newName != "未知设备")
            Name = newName;
        if (capable)
            IsHeartRateCapable = true;
        if (broadcasting)
            IsHeartRateBroadcasting = true;
        if (hasGattService)
            HasHeartRateService = true;

        OnPropertyChanged(nameof(SignalBars));
        OnPropertyChanged(nameof(SignalStrengthDescription));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(CategoryBadgeText));
        OnPropertyChanged(nameof(IsNamedDevice));
        OnPropertyChanged(nameof(IsCandidateName));
        OnPropertyChanged(nameof(IsHeartRateCandidate));
    }
}
