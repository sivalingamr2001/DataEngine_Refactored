using DataEngine.Abstractions;
using DataEngine.Model;

namespace DataEngine.Services;

/// <summary>
/// Retained for backwards compatibility.
/// All logic has moved to DataEngineTransaction.ExecuteQuery.
/// Existing callers using IDynamicReadEngine continue to work without any change.
/// </summary>
public class DynamicReadEngine : IDynamicEngine
{
    private readonly IDynamicEngine _engine;

    public DynamicReadEngine(IDynamicEngine engine) => _engine = engine;

    public Task<FetchResult> ExecuteQuery(FetchConfig config, string connectionString, CancellationToken cancellationToken = default)
        => _engine.ExecuteQuery(config, connectionString, cancellationToken);

    public Task<TransactionResult> TransactionProcess(TransactionRequest request, CancellationToken cancellationToken = default)
        => _engine.TransactionProcess(request, cancellationToken);
}
