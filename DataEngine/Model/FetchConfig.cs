using System.Text.Json;

namespace DataEngine.Model;

public class FetchConfig
{
    public int QueryNumber { get; set; }
    public JsonElement InputParameters { get; set; }
    public int Count { get; set; } = 10;
    public int PageNumber { get; set; } = 1;
    public bool IncludeReferenceLabels { get; set; } = false;

    // Server-side sorting
    public bool EnableServerSideSorting { get; set; } = false;
    public string? SortField { get; set; }
    public string SortDirection { get; set; } = "asc";

    // Server-side filtering
    public bool EnableServerSideFiltering { get; set; } = false;
    public List<FilterCondition>? FilterConditions { get; set; }

    public string? FetchTimezone { get; set; }
    public string? SearchText { get; set; }

    // Direct query execution
    public string? QueryText { get; set; }
    public bool EnableDirectQueryExecution { get; set; } = true;
    public bool EnableCaching { get; set; } = false;
}

public class FilterCondition
{
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Supported: eq | neq | gt | lt | gte | lte | contains | startswith | endswith
    /// </summary>
    public string Operator { get; set; } = "eq";
    public object? Value { get; set; }
}

public class FetchResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
    public int TotalCount { get; set; }
}
