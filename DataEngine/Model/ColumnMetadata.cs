namespace DataEngine.Model;

public class ColumnMetadata
{
    public string ColumnName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsAutoIncrement { get; set; }
    public long? MaxCharacterLength { get; set; }
    public bool HasDefaultValue { get; set; }
    public string? DefaultValue { get; set; }
}
