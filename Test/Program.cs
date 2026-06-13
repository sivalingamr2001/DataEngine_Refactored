using DataEngine.Abstractions;
using DataEngine.Model;
using Test;

string ProjectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
string DatabasePath = Path.Combine(ProjectRoot, "sqlite_test.db");
string ConnectionString = $"Data Source={DatabasePath}";

var initializer = new Initialize(ProjectRoot, DatabasePath, ConnectionString);
IDynamicEngine engine = await initializer.RunAsync();

Console.WriteLine($"Global Connection String can be reused: {ConnectionString}");

var config = new FetchConfig
{
    QueryNumber = 101,
    InputParameters = System.Text.Json.JsonSerializer.SerializeToElement(new
    {
        DepartmentId = 5,
        Status = "Active"
    }),
    Count = 25,
    PageNumber = 2,
    IncludeReferenceLabels = true,
    EnableDirectQueryExecution = false
};

var users = await engine.ExecuteQuery(config, ConnectionString);

Console.WriteLine($"Fetched {users} user records based on dynamic query configuration.");

Console.ReadKey();
