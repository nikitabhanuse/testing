using Microsoft.Extensions.AI;
using WebApplicationAI.AI.Abstractions;
using WebApplicationAI.AI.Models;

namespace WebApplicationAI.Services
{
    public class AiClientService : IAiClientService
    {
        private readonly IChatClient _chatClient;

        public AiClientService(IChatClient chatClient)
        {
            _chatClient = chatClient;
        }

        public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);

            return response.Text;
        }

        public async Task<ResumeProfile> ExtractProfileAsync(string prompt, CancellationToken cancellationToken = default)
        {
            // Sends a JSON schema so Ollama constrains the output to ResumeProfile.
            var response = await _chatClient.GetResponseAsync<ResumeProfile>(prompt, cancellationToken: cancellationToken);

            return response.TryGetResult(out var profile)
                ? profile
                : throw new InvalidOperationException("Model did not return a valid profile.");
        }
    }
}
