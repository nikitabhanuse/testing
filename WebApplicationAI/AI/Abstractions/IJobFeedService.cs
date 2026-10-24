using WebApplicationAI.AI.Models;

namespace WebApplicationAI.AI.Abstractions
{
    public interface IJobFeedService
    {
        // Open .NET/C# jobs in India from the configured employer career feeds, de-duplicated.
        Task<IReadOnlyList<JobPosting>> GetJobsAsync(CancellationToken cancellationToken = default);
    }
}
