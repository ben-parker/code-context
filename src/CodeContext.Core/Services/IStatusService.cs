using CodeContext.Core.Serialization;

namespace CodeContext.Core.Services;

public interface IStatusService
{
    Task<StatusResponseDto> GetStatusAsync();
}