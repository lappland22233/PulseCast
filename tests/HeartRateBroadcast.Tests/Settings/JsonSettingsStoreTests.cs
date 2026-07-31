using System.Text.Json;
using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.Settings;

namespace HeartRateBroadcast.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PulseCastTests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public async Task ConcurrentSaves_ProduceValidNormalizedJson()
    {
        using var store = new JsonSettingsStore(SettingsPath);
        var settings = new AppSettings { DataTimeoutSeconds = 999, MinimumBpm = -1, MaximumBpm = 999 };

        await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
        {
            settings.SelectedDeviceAddress = (ulong)index;
            return store.SaveAsync(settings);
        }));

        await using var stream = File.OpenRead(SettingsPath);
        var saved = await JsonSerializer.DeserializeAsync<AppSettings>(stream);
        Assert.NotNull(saved);
        Assert.InRange(saved.DataTimeoutSeconds, 3, 60);
        Assert.InRange(saved.MinimumBpm, 1, 299);
        Assert.InRange(saved.MaximumBpm, saved.MinimumBpm, 300);
    }

    [Fact]
    public async Task LoadCorruptJson_BacksUpAndReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, "{ invalid json");
        using var store = new JsonSettingsStore(SettingsPath);

        AppSettings settings = await store.LoadAsync();

        Assert.Equal(8, settings.DataTimeoutSeconds);
        Assert.NotNull(store.LoadWarning);
        Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*.bak"));
    }

    [Fact]
    public async Task Load_NormalizesInvalidOverlayValues()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, """{"DataTimeoutSeconds":0,"MinimumBpm":280,"MaximumBpm":20,"Overlay":{"Opacity":2,"Scale":9,"NumberFont":"invalid"}}""");
        using var store = new JsonSettingsStore(SettingsPath);

        AppSettings settings = await store.LoadAsync();

        Assert.Equal(3, settings.DataTimeoutSeconds);
        Assert.Equal(280, settings.MinimumBpm);
        Assert.Equal(280, settings.MaximumBpm);
        Assert.Equal(1, settings.Overlay.Opacity);
        Assert.Equal("等宽", settings.Overlay.NumberFont);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
