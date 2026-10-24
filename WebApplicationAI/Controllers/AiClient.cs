using Microsoft.AspNetCore.Mvc;
using WebApplicationAI.AI.Abstractions;

namespace WebApplicationAI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AIController : ControllerBase
    {
        private readonly IAiClientService _aiService;
        private readonly IResumeTextExtractor _resumeTextExtractor;
        private readonly IPromptBuilder _promptBuilder;
        private readonly ILogger<AIController> _logger;

        public AIController(
            IAiClientService aiService,
            IResumeTextExtractor resumeTextExtractor,
            IPromptBuilder promptBuilder,
            ILogger<AIController> logger)
        {
            _aiService = aiService;
            _resumeTextExtractor = resumeTextExtractor;
            _promptBuilder = promptBuilder;
            _logger = logger;
        }

        [HttpPost("ask")]
        public async Task<IActionResult> Ask(
            [FromForm] IFormFile resume,
            CancellationToken cancellationToken)
        {
            if (resume is null || resume.Length == 0)
            {
                return BadRequest(new { error = "Resume file is required." });
            }

            string resumeText;
            try
            {
                resumeText = await _resumeTextExtractor.ExtractTextAsync(resume, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                if (ex.InnerException is not null)
                    _logger.LogWarning(ex.InnerException, "Resume parsing failed.");
                return BadRequest(new { error = ex.Message });
            }

            var prompt = _promptBuilder.Build(resumeText);

            try
            {
                var answer = await _aiService.AskAsync(prompt, cancellationToken);
                return Ok(new { answer });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Not the caller's cancellation: the model client timed out.
                _logger.LogError("Model request timed out.");
                return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "The AI model timed out." });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Model request failed.");
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "The AI model is unavailable." });
            }
        }
    }
}
