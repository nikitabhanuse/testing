using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApplicationAI.Data;
using WebApplicationAI.Services;

namespace WebApplicationAI.Controllers
{
    public record CreateApplicationRequest(Guid ResumeId, Guid JobId, string? Notes);
    public record DraftRequest(string TailoredResume);
    public record StatusRequest(ApplicationStatus Status, string? Notes);

    // Saved jobs and application tracking. Nothing is ever sent to an employer by this API:
    // the candidate applies via the job URL and then records "Submitted", which requires an approved draft.
    [ApiController]
    [Route("api/[controller]")]
    public class ApplicationsController(AppDbContext db, RagService rag) : ControllerBase
    {
        // Save a job for a resume.
        [HttpPost]
        public async Task<IActionResult> Create(CreateApplicationRequest request, CancellationToken cancellationToken)
        {
            if (!await db.Resumes.AnyAsync(r => r.Id == request.ResumeId, cancellationToken)
                || !await db.Jobs.AnyAsync(j => j.Id == request.JobId, cancellationToken))
                return NotFound(new { error = "Resume or job not found." });

            if (await db.Applications.FirstOrDefaultAsync(
                    a => a.ResumeId == request.ResumeId && a.JobId == request.JobId, cancellationToken) is { } existing)
                return Conflict(new { error = "Job already saved for this resume.", existing.Id });

            var now = DateTimeOffset.UtcNow;
            var app = new Application
            {
                Id = Guid.NewGuid(),
                ResumeId = request.ResumeId,
                JobId = request.JobId,
                Status = ApplicationStatus.Saved,
                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Applications.Add(app);
            await db.SaveChangesAsync(cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = app.Id }, app);
        }

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] Guid? resumeId, [FromQuery] ApplicationStatus? status, CancellationToken cancellationToken) =>
            Ok(await db.Applications.AsNoTracking()
                .Include(a => a.Job)
                .Where(a => resumeId == null || a.ResumeId == resumeId)
                .Where(a => status == null || a.Status == status)
                .OrderByDescending(a => a.UpdatedAt)
                .ToListAsync(cancellationToken));

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
            await db.Applications.AsNoTracking().Include(a => a.Job).FirstOrDefaultAsync(a => a.Id == id, cancellationToken) is { } app
                ? Ok(app)
                : NotFound();

        // Generate a tailored resume draft with Ollama (from the resume's own facts only).
        [HttpPost("{id:guid}/draft")]
        public async Task<IActionResult> GenerateDraft(Guid id, CancellationToken cancellationToken)
        {
            var app = await db.Applications.Include(a => a.Job).FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (app is null) return NotFound();
            if (IsFinal(app)) return Conflict(new { error = $"Application is already {app.Status}." });

            var resume = await db.Resumes.AsNoTracking().FirstAsync(r => r.Id == app.ResumeId, cancellationToken);
            SetDraft(app, await rag.DraftTailoredResumeAsync(resume, app.Job!, cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
            return Ok(app);
        }

        // Replace the draft with the human-edited version. Any edit revokes a previous approval.
        [HttpPut("{id:guid}/draft")]
        public async Task<IActionResult> EditDraft(Guid id, DraftRequest request, CancellationToken cancellationToken)
        {
            var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (app is null) return NotFound();
            if (IsFinal(app)) return Conflict(new { error = $"Application is already {app.Status}." });
            if (string.IsNullOrWhiteSpace(request.TailoredResume)) return BadRequest(new { error = "Draft is empty." });

            SetDraft(app, request.TailoredResume);
            await db.SaveChangesAsync(cancellationToken);
            return Ok(app);
        }

        // Human approval of the current draft: the gate before an application can be marked submitted.
        [HttpPost("{id:guid}/approve")]
        public async Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken)
        {
            var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (app is null) return NotFound();
            if (app.Status != ApplicationStatus.Drafted || string.IsNullOrWhiteSpace(app.TailoredResume))
                return Conflict(new { error = "Only a drafted application with a tailored resume can be approved." });

            app.Status = ApplicationStatus.Approved;
            app.ApprovedAt = app.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Ok(app);
        }

        // Record progress after the candidate has applied. Drafting and approval use their own endpoints.
        [HttpPost("{id:guid}/status")]
        public async Task<IActionResult> SetStatus(Guid id, StatusRequest request, CancellationToken cancellationToken)
        {
            var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (app is null) return NotFound();

            var allowed = (app.Status, request.Status) switch
            {
                (_, ApplicationStatus.Withdrawn) => app.Status is not (ApplicationStatus.Rejected or ApplicationStatus.Withdrawn),
                (ApplicationStatus.Approved, ApplicationStatus.Submitted) => true,
                (ApplicationStatus.Submitted or ApplicationStatus.Interviewing,
                 ApplicationStatus.Interviewing or ApplicationStatus.Offer or ApplicationStatus.Rejected) => true,
                _ => false
            };
            if (!allowed)
                return Conflict(new
                {
                    error = request.Status == ApplicationStatus.Submitted
                        ? "An application can only be submitted after its tailored resume is approved."
                        : $"Cannot move from {app.Status} to {request.Status}."
                });

            app.Status = request.Status;
            if (request.Status == ApplicationStatus.Submitted) app.SubmittedAt = DateTimeOffset.UtcNow;
            if (request.Notes is not null) app.Notes = request.Notes;
            app.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Ok(app);
        }

        private static void SetDraft(Application app, string draft)
        {
            app.TailoredResume = draft;
            app.Status = ApplicationStatus.Drafted;
            app.ApprovedAt = null;
            app.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Once submitted (or closed out), drafts and approval are frozen.
        private static bool IsFinal(Application app) =>
            app.Status is ApplicationStatus.Submitted or ApplicationStatus.Interviewing or ApplicationStatus.Offer
                or ApplicationStatus.Rejected or ApplicationStatus.Withdrawn;
    }
}
