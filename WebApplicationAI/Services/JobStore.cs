using Microsoft.EntityFrameworkCore;
using WebApplicationAI.AI.Abstractions;
using WebApplicationAI.Data;

namespace WebApplicationAI.Services
{
    public record SyncResult(int Fetched, int Added, int Updated, int Closed, bool Skipped);

    // Imports feed jobs into Postgres: de-duplicates by URL and company|title|location,
    // embeds new or changed descriptions, and closes jobs no longer listed.
    public class JobStore(AppDbContext db, IJobFeedService feed, RagService rag, ILogger<JobStore> logger)
    {
        private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(30);
        // ponytail: a job is closed after missing from feeds this long, so one failed fetch doesn't close
        // a whole company; track per-source fetch success if closures need to be same-day.
        private static readonly TimeSpan CloseAfter = TimeSpan.FromDays(2);

        public async Task<SyncResult> SyncAsync(bool force, CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            if (!force && await db.Jobs.AnyAsync(j => j.LastSeenAt > now - SyncInterval, ct))
                return new SyncResult(0, 0, 0, 0, Skipped: true);

            var postings = await feed.GetJobsAsync(ct);
            var urls = postings.Select(p => p.Url).ToList();
            var keys = postings.Select(JobFeedService.DedupeKey).ToList();
            var existing = await db.Jobs
                .Where(j => urls.Contains(j.Url) || keys.Contains(j.DedupeKey))
                .ToListAsync(ct);

            int added = 0, updated = 0;
            foreach (var p in postings)
            {
                var key = JobFeedService.DedupeKey(p);
                var job = existing.FirstOrDefault(j => j.Url == p.Url) ?? existing.FirstOrDefault(j => j.DedupeKey == key);

                // URL matched one stored job but company|title|location matches another: same role twice, keep the first.
                if (job is not null && existing.Any(j => j != job && j.DedupeKey == key))
                    continue;

                if (job is null)
                {
                    job = new Job { Id = Guid.NewGuid(), FirstSeenAt = now };
                    db.Jobs.Add(job);
                    existing.Add(job);
                    added++;
                }
                else if (job.Description != p.Description || job.Title != p.Title)
                {
                    await db.Chunks.Where(c => c.JobId == job.Id).ExecuteDeleteAsync(ct);
                    job.Embedding = [];
                    updated++;
                }

                (job.Company, job.Title, job.Location, job.Url, job.Description, job.Source, job.PostedAt) =
                    (p.Company, p.Title, p.Location, p.Url, p.Description, p.Source, p.PostedAt?.ToUniversalTime());
                job.DedupeKey = key;
                job.LastSeenAt = now;
                job.IsOpen = true;

                if (job.Embedding.Length == 0)
                {
                    var (doc, chunks) = await rag.IndexAsync($"{p.Title}. {p.Description}", ct);
                    job.Embedding = doc;
                    chunks.ForEach(c => c.JobId = job.Id);
                    db.Chunks.AddRange(chunks);
                }
            }

            await db.SaveChangesAsync(ct);

            var closed = await db.Jobs
                .Where(j => j.IsOpen && j.LastSeenAt < now - CloseAfter)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.IsOpen, false), ct);

            logger.LogInformation("Job sync: fetched {Fetched}, added {Added}, updated {Updated}, closed {Closed}.",
                postings.Count, added, updated, closed);
            return new SyncResult(postings.Count, added, updated, closed, Skipped: false);
        }
    }
}
