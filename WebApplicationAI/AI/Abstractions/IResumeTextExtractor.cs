namespace WebApplicationAI.AI.Abstractions
{
    public interface IResumeTextExtractor
    {
        Task<string> ExtractTextAsync(IFormFile file, CancellationToken cancellationToken = default);
    }
}
