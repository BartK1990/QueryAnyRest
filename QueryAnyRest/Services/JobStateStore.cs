using System.Collections.Concurrent;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>Last-run and next-due bookkeeping, kept across restarts.</summary>
public sealed class JobStateStore
{
    private readonly AppOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConcurrentDictionary<string, JobState>? _states;

    public JobStateStore(AppOptions options) => _options = options;

    public async Task<JobState> GetAsync(string configurationId, CancellationToken ct = default)
    {
        var states = await EnsureLoadedAsync(ct);
        return states.GetOrAdd(configurationId, id => new JobState { ConfigurationId = id });
    }

    public async Task<IReadOnlyList<JobState>> GetAllAsync(CancellationToken ct = default)
    {
        var states = await EnsureLoadedAsync(ct);
        return states.Values.ToList();
    }

    public async Task SetAsync(JobState state, CancellationToken ct = default)
    {
        var states = await EnsureLoadedAsync(ct);
        states[state.ConfigurationId] = state;

        await _gate.WaitAsync(ct);
        try
        {
            await JsonFileStore.WriteAsync(_options.JobStateFile, states.Values.ToList(), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ConcurrentDictionary<string, JobState>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_states is not null) return _states;

        await _gate.WaitAsync(ct);
        try
        {
            if (_states is null)
            {
                var list = await JsonFileStore.ReadAsync(_options.JobStateFile, () => new List<JobState>(), ct);
                _states = new ConcurrentDictionary<string, JobState>(
                    list.Where(s => !string.IsNullOrEmpty(s.ConfigurationId))
                        .ToDictionary(s => s.ConfigurationId, s => s));
            }
        }
        finally
        {
            _gate.Release();
        }

        return _states;
    }
}
