using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>The recent run list shown on the History page, capped and persisted.</summary>
public sealed class RunHistory
{
    private readonly AppOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<RunRecord>? _records;

    public RunHistory(AppOptions options) => _options = options;

    public event Action? Changed;

    public async Task<IReadOnlyList<RunRecord>> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _records ??= await JsonFileStore.ReadAsync(_options.RunHistoryFile, () => new List<RunRecord>(), ct);
            return _records.OrderByDescending(r => r.StartedUtc).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(RunRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _records ??= await JsonFileStore.ReadAsync(_options.RunHistoryFile, () => new List<RunRecord>(), ct);
            _records.Add(record);

            var limit = Math.Clamp(_options.RunHistoryLimit, 10, 5000);
            if (_records.Count > limit)
                _records = _records.OrderByDescending(r => r.StartedUtc).Take(limit).ToList();

            await JsonFileStore.WriteAsync(_options.RunHistoryFile, _records, ct);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _records = new List<RunRecord>();
            await JsonFileStore.WriteAsync(_options.RunHistoryFile, _records, ct);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }
}
