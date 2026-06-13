namespace DataEngine.Model;

public sealed class TableMetadata
{
    public string TableName { get; set; } = string.Empty;
    public List<ColumnMetadata> Columns { get; set; } = new();
    public TimeSpan? Ttl { get; set; }
    public DateTimeOffset CachedAt { get; set; }
}
