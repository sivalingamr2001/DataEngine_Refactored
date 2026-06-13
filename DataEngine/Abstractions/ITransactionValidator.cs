using DataEngine.Model;

namespace DataEngine.Abstractions;

public interface ITransactionValidator
{
    Task<(bool IsValid, string FailureReason)> ValidatePayloadAsync(
        TransactionRequest request,
        List<ColumnMetadata> schema,
        CancellationToken cancellationToken = default);
}
