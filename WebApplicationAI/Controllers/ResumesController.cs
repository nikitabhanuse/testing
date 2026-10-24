using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApplicationAI.AI.Abstractions;
using WebApplicationAI.Data;
using WebApplicationAI.Services;

namespace WebApplicationAI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ResumesController(
        AppDbContext db,
        IResumeTextExtractor resumeTextExtractor,
        IAiClientService aiService,
        IPromptBuilder promptBuilder,
        RagService rag,
        JobStore jobStore,
        ILogger<ResumesController> logger) : ControllerBase
    {
        // Upload a PDF/DOCX/TXT resume: extracts text and a structured profile, stores both, indexes passages for RAG.
        [HttpPost]
        public async Task<IActionResult> Upload([FromForm] IFormFile resume, CancellationToken cancellationToken)
        {
            string text;
            try
            {
                text = await resumeTextExtractor.ExtractTextAsync(resume, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                if (ex.InnerException is not null)
                    logger.LogWarning(ex.InnerException, "Resume parsing failed.");
                return BadRequest(new { error = ex.Message });
            }

            var profile = await aiService.ExtractProfileAsync(promptBuilder.BuildProfile(text), cancellationToken);
            var (vector, chunks) = await rag.IndexAsync(text, cancellationToken);

            var entity = new Resume
            {
                Id = Guid.NewGuid(),
                FileName = Path.GetFileName(resume.FileName),
                Text = text,
                Profile = profile,
                Embedding = vector,
                CreatedAt = DateTimeOffset.UtcNow
            };
            chunks.ForEach(c => c.ResumeId = entity.Id);
            db.Resumes.Add(entity);
            db.Chunks.AddRange(chunks);
            await db.SaveChangesAsync(cancellationToken);

            return CreatedAtAction(nameof(Get), new { id = entity.Id }, entity);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
            await db.Resumes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken) is { } resume
                ? Ok(resume)
                : NotFound();

        // Open jobs ranked for this resume.
        [HttpGet("{id:guid}/matches")]
        public async Task<IActionResult> Matches(
            Guid id,
            CancellationToken cancellationToken,
            [FromQuery] string? preferredLocation = null,
            [FromQuery] int top = 20)
        {
            var resume = await db.Resumes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (resume is null) return NotFound();

            await jobStore.SyncAsync(force: false, cancellationToken);
            var jobs = await db.Jobs.AsNoTracking().Where(j => j.IsOpen).ToListAsync(cancellationToken);
            var matches = JobMatcher.Rank(resume.Profile, resume.Embedding, jobs, preferredLocation);

            return Ok(new { resume.Profile, totalJobs = matches.Count, matches = matches.Take(Math.Clamp(top, 1, 100)) });
        }

        // RAG: why this job fits (or doesn't), citing resume [R#] and job [J#] passages.
        [HttpGet("{id:guid}/matches/{jobId:guid}/explain")]
        public async Task<IActionResult> Explain(Guid id, Guid jobId, CancellationToken cancellationToken)
        {
            var resume = await db.Resumes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
            if (resume is null || job is null) return NotFound();

            return Ok(await rag.ExplainMatchAsync(resume, job, cancellationToken));
        }
    }
}
