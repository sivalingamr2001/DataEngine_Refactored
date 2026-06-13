using System.Collections.Concurrent;
using DataEngine.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataEngine.Cache;

/// <summary>
/// Thread-safe, in-process schema metadata cache backed by ConcurrentDictionary.
///
/// Lifetime: Singleton — one cache per application process.
///
/// TTL behaviour:
///   - If InMemorySchemaCacheOptions.DefaultTtl is set, every entry expires after that duration.
///   - Individual entries can override TTL via TableMetadata.Ttl.
///   - TTL is checked on Get — no background eviction thread required.
///
/// Usage:
///   services.TryAddSingleton&lt;ISchemaCache, InMemorySchemaCache&gt;();
/// </summary>
public sealed class InMemorySchemaCache(
    IOptions<InMemorySchemaCacheOptions> options,
    ILogger<InMemorySchemaCache> logger) : ISchemaCache
{
    private readonly ConcurrentDictionary<string, TableMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly InMemorySchemaCacheOptions _options = options.Value;
    private readonly ILogger<InMemorySchemaCache> _logger = logger;

    /// <inheritdoc/>
    public Task<TableMetadata?> GetAsync(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return Task.FromResult<TableMetadata?>(null);

        if (!_cache.TryGetValue(tableName, out var entry))
            return Task.FromResult<TableMetadata?>(null);

        // Honour per-entry TTL, falling back to global default
        var ttl = entry.Ttl ?? _options.DefaultTtl;
        if (ttl.HasValue && DateTimeOffset.UtcNow - entry.CachedAt > ttl.Value)
        {
            _cache.TryRemove(tableName, out _);
            _logger.LogDebug("[SchemaCache] TTL expired — evicted '{Table}'.", tableName);
            return Task.FromResult<TableMetadata?>(null);
        }

        _logger.LogDebug("[SchemaCache] Cache hit for '{Table}'.", tableName);
        return Task.FromResult<TableMetadata?>(entry);
    }

    /// <inheritdoc/>
    public Task SetAsync(string tableName, TableMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return Task.CompletedTask;

        metadata.CachedAt = DateTimeOffset.UtcNow;
        _cache[tableName] = metadata;

        _logger.LogDebug("[SchemaCache] Cached '{Table}' ({Count} columns).",
            tableName, metadata.Columns.Count);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task InvalidateAsync(string tableName)
    {
        if (_cache.TryRemove(tableName, out _))
            _logger.LogInformation("[SchemaCache] Invalidated '{Table}'.", tableName);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearAsync()
    {
        var count = _cache.Count;
        _cache.Clear();
        _logger.LogInformation("[SchemaCache] Full cache cleared ({Count} entries removed).", count);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Configuration options for <see cref="InMemorySchemaCache"/>.
/// Bind via IOptions pattern from appsettings.json under "DataEngine:SchemaCache".
/// </summary>
public sealed class InMemorySchemaCacheOptions
{
    /// <summary>
    /// Global TTL applied to all entries.
    /// Set to null for indefinite caching (default).
    /// Example: TimeSpan.FromMinutes(30)
    /// </summary>
    public TimeSpan? DefaultTtl { get; set; }
}
