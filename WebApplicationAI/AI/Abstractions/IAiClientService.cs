using WebApplicationAI.AI.Models;

namespace WebApplicationAI.AI.Abstractions
{
    public interface IAiClientService
    {
        Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default);
        Task<ResumeProfile> ExtractProfileAsync(string prompt, CancellationToken cancellationToken = default);
    }
}
