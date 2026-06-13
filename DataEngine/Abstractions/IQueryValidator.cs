using DataEngine.Model;

namespace DataEngine.Abstractions;

public interface IQueryValidator
{
    (bool IsValid, string FailureReason) ValidateQueryConfig(FetchConfig config);
}
