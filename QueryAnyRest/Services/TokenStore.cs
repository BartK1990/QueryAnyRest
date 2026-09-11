using System.Collections.Concurrent;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// Holds the live access/refresh token pair per configuration and persists it,
/// because a rotated refresh token is single-use: losing it means a new password grant.
/// </summary>
public sealed class TokenStore
{
    private readonly AppOptions _options;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ConcurrentDictionary<string, TokenState>? _states;

    public TokenStore(AppOptions options) => _options = options;

    public async Task<TokenState?> GetAsync(string configurationId, CancellationToken ct = default)
    {
        var states = await EnsureLoadedAsync(ct);
        return states.TryGetValue(configurationId, out var state) ? state : null;
    }

    public async Task SetAsync(TokenState state, CancellationToken ct = default)
    {
        var states = await EnsureLoadedAsync(ct);
        states[state.ConfigurationId] = state;
        await PersistAsync(states, ct);
    }

    public async Task ClearAsync(string configurationId, CancellationToken ct = default)
    {
        var states = await EnsureLoadedAsync(ct);
        states.TryRemove(configurationId, out _);
        await PersistAsync(states, ct);
    }

    private async Task<ConcurrentDictionary<string, TokenState>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_states is not null) return _states;

        await _writeGate.WaitAsync(ct);
        try
        {
            if (_states is null)
            {
                var list = await JsonFileStore.ReadAsync(_options.TokenFile, () => new List<TokenState>(), ct);
                _states = new ConcurrentDictionary<string, TokenState>(
                    list.Where(s => !string.IsNullOrEmpty(s.ConfigurationId))
                        .ToDictionary(s => s.ConfigurationId, s => s));
            }
        }
        finally
        {
            _writeGate.Release();
        }

        return _states;
    }

    private async Task PersistAsync(ConcurrentDictionary<string, TokenState> states, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await JsonFileStore.WriteAsync(_options.TokenFile, states.Values.ToList(), ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
