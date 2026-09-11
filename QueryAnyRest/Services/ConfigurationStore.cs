using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

public sealed class ConfigurationFile
{
    public int Version { get; set; } = 1;
    public List<ApiConfiguration> Configurations { get; set; } = new();
}

/// <summary>
/// The saved list of endpoint definitions. One JSON file, loaded once and
/// written back whenever something changes.
/// </summary>
public sealed class ConfigurationStore
{
    private readonly AppOptions _options;
    private readonly ILogger<ConfigurationStore> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ConfigurationFile? _file;

    public ConfigurationStore(AppOptions options, ILogger<ConfigurationStore> log)
    {
        _options = options;
        _log = log;
    }

    /// <summary>Raised after any change so open pages can refresh.</summary>
    public event Action? Changed;

    public string FilePath => _options.ConfigurationFile;

    public async Task<IReadOnlyList<ApiConfiguration>> GetAllAsync(CancellationToken ct = default)
    {
        var file = await LoadAsync(ct);
        return file.Configurations
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ApiConfiguration?> GetAsync(string id, CancellationToken ct = default)
    {
        var file = await LoadAsync(ct);
        return file.Configurations.FirstOrDefault(c => c.Id == id)?.Clone();
    }

    public async Task SaveAsync(ApiConfiguration configuration, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var file = await LoadUnlockedAsync(ct);
            configuration.ModifiedUtc = DateTimeOffset.UtcNow;

            // Store a copy: the editor keeps mutating the instance it passed in, and
            // unsaved edits must not reach the scheduler.
            var stored = configuration.Clone();

            var index = file.Configurations.FindIndex(c => c.Id == configuration.Id);
            if (index >= 0) file.Configurations[index] = stored;
            else file.Configurations.Add(stored);

            await JsonFileStore.WriteAsync(_options.ConfigurationFile, file, ct);
            _log.LogInformation("Saved configuration {Name} ({Id})", configuration.Name, configuration.Id);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var file = await LoadUnlockedAsync(ct);
            file.Configurations.RemoveAll(c => c.Id == id);
            await JsonFileStore.WriteAsync(_options.ConfigurationFile, file, ct);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async Task<ApiConfiguration> DuplicateAsync(string id, CancellationToken ct = default)
    {
        var source = await GetAsync(id, ct) ?? throw new InvalidOperationException("Configuration not found.");
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = source.Name + " (copy)";
        copy.Schedule.Enabled = false;
        copy.CreatedUtc = DateTimeOffset.UtcNow;
        await SaveAsync(copy, ct);
        return copy;
    }

    /// <summary>Replaces the whole file, used by the import screen.</summary>
    public async Task ReplaceAllAsync(IEnumerable<ApiConfiguration> configurations, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var file = new ConfigurationFile { Configurations = configurations.Select(c => c.Clone()).ToList() };
            await JsonFileStore.WriteAsync(_options.ConfigurationFile, file, ct);
            _file = file;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    private async Task<ConfigurationFile> LoadAsync(CancellationToken ct)
    {
        if (_file is not null) return _file;

        await _gate.WaitAsync(ct);
        try
        {
            return await LoadUnlockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ConfigurationFile> LoadUnlockedAsync(CancellationToken ct)
    {
        _file ??= await JsonFileStore.ReadAsync(_options.ConfigurationFile, () => new ConfigurationFile(), ct);
        return _file;
    }
}
