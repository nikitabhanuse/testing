using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApplicationAI.Data;
using WebApplicationAI.Services;

namespace WebApplicationAI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class JobsController(AppDbContext db, JobStore jobStore) : ControllerBase
    {
        // Open .NET/C# jobs in India. Syncs from the employer feeds first if the data is over 30 minutes old.
        [HttpGet]
        public async Task<IActionResult> List(CancellationToken cancellationToken)
        {
            await jobStore.SyncAsync(force: false, cancellationToken);
            var jobs = await db.Jobs.AsNoTracking()
                .Where(j => j.IsOpen)
                .OrderByDescending(j => j.PostedAt)
                .ToListAsync(cancellationToken);
            return Ok(new { count = jobs.Count, jobs });
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
            await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancellationToken) is { } job
                ? Ok(job)
                : NotFound();

        // Force an import from the employer feeds now.
        [HttpPost("sync")]
        public async Task<IActionResult> Sync(CancellationToken cancellationToken) =>
            Ok(await jobStore.SyncAsync(force: true, cancellationToken));
    }
}
