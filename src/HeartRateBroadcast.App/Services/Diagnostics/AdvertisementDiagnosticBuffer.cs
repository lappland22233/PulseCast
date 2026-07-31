using System.Security.Cryptography;
using HeartRateBroadcast.App.Models;

namespace HeartRateBroadcast.App.Services.Diagnostics;

public sealed record AdvertisementDiagnostic(DateTimeOffset Time, string Device, short Rssi, string Services, string Payload);

public sealed class AdvertisementDiagnosticBuffer
{
    private readonly object _gate = new();
    private readonly Queue<AdvertisementDiagnostic> _items;
    private readonly Dictionary<ulong, DateTimeOffset> _lastRecorded = [];
    private readonly bool _enabled;
    private readonly int _capacity;
    private readonly TimeSpan _minimumInterval;

    public AdvertisementDiagnosticBuffer(AppSettings settings, int capacity = 100, TimeSpan? minimumInterval = null)
    {
        _enabled = settings.DiagnosticMode;
        _capacity = Math.Clamp(capacity, 1, 500);
        _minimumInterval = minimumInterval ?? TimeSpan.FromSeconds(1);
        _items = new Queue<AdvertisementDiagnostic>(_capacity);
    }

    public IReadOnlyList<AdvertisementDiagnostic> Snapshot()
    {
        lock (_gate) return _items.Reverse().ToArray();
    }

    public void Add(BluetoothPacket packet)
    {
        if (!_enabled) return;
        lock (_gate)
        {
            if (_lastRecorded.TryGetValue(packet.Address, out var last) && packet.ReceivedAt - last < _minimumInterval) return;
            _lastRecorded[packet.Address] = packet.ReceivedAt;

            string payload = string.Join(" | ", packet.Sections.Take(8).Select(section =>
            {
                ReadOnlySpan<byte> data = section.Data.AsSpan(0, Math.Min(section.Data.Length, 32));
                return $"{section.Type:X2}:{Convert.ToHexString(data)}";
            }));
            string device = $"设备-{Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(packet.Address))).Substring(0, 8)}";
            var item = new AdvertisementDiagnostic(packet.ReceivedAt, device, packet.Rssi, string.Join(",", packet.ServiceUuids.Take(8)), payload);
            while (_items.Count >= _capacity) _items.Dequeue();
            _items.Enqueue(item);

            if (_lastRecorded.Count > _capacity * 2)
            {
                foreach (var address in _lastRecorded.OrderBy(pair => pair.Value).Take(_lastRecorded.Count - _capacity).Select(pair => pair.Key).ToArray())
                    _lastRecorded.Remove(address);
            }
        }
    }
}
