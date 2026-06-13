using DataEngine.Abstractions;
using DataEngine.DbProviders.Sqlite;
using DataEngine.Model;
using Microsoft.Data.Sqlite;

const string DatabaseFile = "sqlite_test.db";
const string ConnectionString = "Data Source=" + DatabaseFile;

await CreateTableAsync();
IDynamicEngine engine = new SqliteDynamicEngine(ConnectionString);

var insertRequest = new TransactionRequest
{
    TransactionId = Guid.NewGuid().ToString("N"),
    TransactionEntityName = "Users",
    // RenProps: Child table records (INSERT/UPDATE)
    RenProps = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Users"] = new()
        {
            new() { ["Name"] = "Alice", ["Age"] = 30 },
            new() { ["Name"] = "Bob", ["Age"] = 42 }
        }
    }
};

var insertResult = await engine.TransactionProcess(insertRequest);
Console.WriteLine($"Insert succeeded: {insertResult.Success}, Message: {insertResult.Message}");

Console.WriteLine("After inserts:");
await PrintUsersAsync();

var updateRequest = new TransactionRequest
{
    TransactionId = Guid.NewGuid().ToString("N"),
    TransactionEntityName = "Users",
    RenProps = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Users"] = new()
        {
            new() { ["Id"] = 1, ["Name"] = "Alice", ["Age"] = 31 }
        }
    }
};

var updateResult = await engine.TransactionProcess(updateRequest);
Console.WriteLine($"Update succeeded: {updateResult.Success}, Message: {updateResult.Message}");

Console.WriteLine("After update:");
await PrintUsersAsync();

var deleteRequest = new TransactionRequest
{
    TransactionId = Guid.NewGuid().ToString("N"),
    TransactionEntityName = "Users",
    DelProps = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Users"] = new()
        {
            new() { ["Id"] = 2 }
        }
    }
};

var deleteResult = await engine.TransactionProcess(deleteRequest);
Console.WriteLine($"Delete succeeded: {deleteResult.Success}, Message: {deleteResult.Message}");

Console.WriteLine("After delete:");
await PrintUsersAsync();

var readResult = await engine.ExecuteQuery(new FetchConfig
{
    QueryText = "SELECT Id, Name, Age FROM Users ORDER BY Id;"
}, ConnectionString);

Console.WriteLine($"Read succeeded: {readResult.Success}, Rows={readResult.TotalCount}");
foreach (var row in readResult.Rows)
    Console.WriteLine($"Row: Id={row["Id"]}, Name={row["Name"]}, Age={row["Age"]}");

Console.WriteLine("Dynamic engine CRUD demo complete.");

static async Task CreateTableAsync()
{
    await using var connection = new SqliteConnection(ConnectionString);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = @"
        CREATE TABLE IF NOT EXISTS Users (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Age INTEGER NOT NULL
        );";
    await command.ExecuteNonQueryAsync();
}

static async Task PrintUsersAsync()
{
    await using var connection = new SqliteConnection(ConnectionString);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT Id, Name, Age FROM Users ORDER BY Id;";

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var id = reader.GetInt64(0);
        var name = reader.GetString(1);
        var age = reader.GetInt32(2);
        Console.WriteLine($"Id={id}, Name={name}, Age={age}");
    }
}
