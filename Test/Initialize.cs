using DataEngine.Abstractions;
using DataEngine.DbProviders.Sqlite;
using Microsoft.Data.Sqlite;

namespace Test;

public class Initialize
{
    private readonly string _connectionString;

    // Accept paths globally from Program.cs via the constructor
    public Initialize(string projectRoot, string databasePath, string connectionString)
    {
        _connectionString = connectionString;

        try
        {
            Console.WriteLine($"[Startup] Target Directory: {projectRoot}");
            Console.WriteLine($"[Startup] Full File Path:  {databasePath}");

            if (!File.Exists(databasePath))
            {
                Console.WriteLine($"[Startup] Database file will be initialized at location.");
            }
            else
            {
                Console.WriteLine($"[Startup] Database file already exists at location.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup Failed] Could not verify database path: {ex.Message}");
        }
    }

    /// <summary>
    /// Prepares the schema, seeds data, and builds the query engine context.
    /// </summary>
    public async Task<IDynamicEngine> RunAsync()
    {
        // 1. First create the structural tables
        await CreateTablesAsync();

        // 2. Then populate the tables with sample records
        await SeedSampleDataAsync();

        Console.WriteLine("[Startup] Database initialization and seeding completed successfully.");

        // Return ready engine instance using the global connection string
        return new SqliteDynamicEngine(_connectionString);
    }

    private async Task CreateTablesAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // 1. Create Users Table
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Users (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Age INTEGER NOT NULL
                );";
            await command.ExecuteNonQueryAsync();
        }

        // 2. Create Query Definitions Table
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS de_query_definitions (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    definition_key  TEXT NOT NULL UNIQUE,
                    table_name      TEXT NOT NULL,
                    description     TEXT,
                    query_json      TEXT NOT NULL,
                    is_active       INTEGER NOT NULL DEFAULT 1,
                    created_at      TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at      TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );";
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task SeedSampleDataAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // 1. Seed Users table (Only if empty)
        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM Users;";
            long userCount = (long)(await checkCmd.ExecuteScalarAsync() ?? 0);

            if (userCount == 0)
            {
                await using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = @"
                    INSERT INTO Users (Name, Age) VALUES 
                    ('Alice Smith', 28),
                    ('Bob Jones', 34),
                    ('Charlie Brown', 19),
                    ('Diana Prince', 42),
                    ('Evan Wright', 17);";
                await insertCmd.ExecuteNonQueryAsync();
                Console.WriteLine("[Seeding] Populated Users table.");
            }
        }

        // 2. Seed Query Definitions table
        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = @"
                INSERT OR IGNORE INTO de_query_definitions (definition_key, table_name, description, query_json)
                VALUES 
                (
                    'FETCH_ACTIVE_ADULTS',
                    'Users',
                    'Retrieves users aged 18 and older.',
                    '{""Count"":20,""PageNumber"":1,""EnableServerSideFiltering"":true,""FilterConditions"":[{""Field"":""Age"",""Operator"":""gte"",""Value"":18}],""EnableServerSideSorting"":true,""SortField"":""Name"",""SortDirection"":""asc""}'
                ),
                (
                    'USER_GLOBAL_SEARCH',
                    'Users',
                    'Performs a wild-card search text matching across user records.',
                    '{""Count"":10,""PageNumber"":1,""SearchText"":""John"",""EnableServerSideSorting"":true,""SortField"":""Id"",""SortDirection"":""desc""}'
                ),
                (
                    'USER_AGE_DEMOGRAPHICS_RAW',
                    'Users',
                    'Direct execution bypass to calculate high-level user statistics.',
                    '{""EnableDirectQueryExecution"":true,""QueryText"":""SELECT Age, COUNT(*) as TotalUsers FROM Users GROUP BY Age ORDER BY TotalUsers DESC"",""EnableCaching"":true}'
                );";
            await queryCmd.ExecuteNonQueryAsync();
            Console.WriteLine("[Seeding] Populated de_query_definitions table.");
        }
    }
}
