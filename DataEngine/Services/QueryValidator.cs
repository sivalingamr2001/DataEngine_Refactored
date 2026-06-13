using DataEngine.Abstractions;
using DataEngine.Model;

namespace DataEngine.Services;

/// <summary>
/// Validates FetchConfig before query execution.
/// Pure synchronous validation — no database calls.
/// Logic unchanged from original; comments improved.
/// </summary>
public class QueryValidator : IQueryValidator
{
    private static readonly HashSet<string> AllowedOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "neq", "gt", "lt", "gte", "lte", "contains", "startswith", "endswith"
    };

    public (bool IsValid, string FailureReason) ValidateQueryConfig(FetchConfig config)
    {
        // QueryText is required for all fetch operations in the current implementation.
        if (string.IsNullOrWhiteSpace(config.QueryText))
            return (false, "QueryText is required for query execution.");

        if (config.Count <= 0)
            return (false, "Count must be greater than zero.");

        if (config.PageNumber <= 0)
            return (false, "PageNumber must be greater than zero.");

        // Direct execution requires QueryText to be supplied
        if (config.EnableDirectQueryExecution && string.IsNullOrWhiteSpace(config.QueryText))
            return (false, "Direct query execution enabled but QueryText is null or empty.");

        // Validate each filter condition when server-side filtering is active
        if (config.EnableServerSideFiltering && config.FilterConditions != null)
        {
            foreach (var filter in config.FilterConditions)
            {
                if (string.IsNullOrWhiteSpace(filter.Field))
                    return (false, "Filter field target cannot be null or blank.");

                if (!AllowedOperators.Contains(filter.Operator))
                    return (false, $"Unsupported filter operator: '{filter.Operator}'. " +
                                   $"Allowed: {string.Join(", ", AllowedOperators)}");
            }
        }

        // Sort requires a field name
        if (config.EnableServerSideSorting && string.IsNullOrWhiteSpace(config.SortField))
            return (false, "Server-side sorting is enabled but SortField is blank.");

        return (true, string.Empty);
    }
}
