using DataEngine.Model;

namespace DataEngine.Cache;

/// <summary>
/// Contract for an in-process schema metadata cache.
///
/// Used by ApplicationTableMetadataRepository to avoid repeated
/// INFORMATION_SCHEMA queries for the same table within a process lifetime.
///
/// All implementations must be thread-safe.
/// </summary>
public interface ISchemaCache
{
    /// <summary>
    /// Returns cached metadata for <paramref name="tableName"/>, or null if not present
    /// (or if the entry has expired).
    /// </summary>
    Task<TableMetadata?> GetAsync(string tableName);

    /// <summary>
    /// Stores or updates the metadata entry for <paramref name="tableName"/>.
    /// Thread-safe — concurrent callers will not corrupt the cache.
    /// </summary>
    Task SetAsync(string tableName, TableMetadata metadata);

    /// <summary>
    /// Removes the entry for <paramref name="tableName"/> so the next read
    /// forces a fresh INFORMATION_SCHEMA query.
    /// Useful when a DDL change (ALTER TABLE, etc.) is detected.
    /// </summary>
    Task InvalidateAsync(string tableName);

    /// <summary>Removes all entries from the cache.</summary>
    Task ClearAsync();
}
