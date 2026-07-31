using System.IO;
using System.Text.Json;
using HeartRateBroadcast.App.Models;

namespace HeartRateBroadcast.App.Services.Settings;

public interface ISettingsStore
{
    string? LoadWarning { get; }
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private bool _disposed;

    public string? LoadWarning { get; private set; }

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeartRateBroadcast", "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        LoadWarning = null;
        if (!File.Exists(_path)) return new AppSettings();

        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (JsonException)
        {
            await BackupDamagedFileAsync(cancellationToken).ConfigureAwait(false);
            LoadWarning = "设置文件已损坏，已备份并恢复默认设置。";
            return new AppSettings();
        }
        catch (IOException)
        {
            LoadWarning = "无法读取设置文件，已临时使用默认设置。";
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            LoadWarning = "没有权限读取设置文件，已临时使用默认设置。";
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var snapshot = settings.Snapshot();
        snapshot.Normalize();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _path, true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _saveGate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _saveGate.Release();
    }

    private async Task BackupDamagedFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            string backup = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak";
            await using var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            await using var target = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 备份失败不应阻止应用以默认设置启动。
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveGate.Dispose();
    }
}
