using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WebApplicationAI.AI.Models;

namespace WebApplicationAI.Data
{
    public class Resume
    {
        public Guid Id { get; set; }
        public string FileName { get; set; } = "";
        public string Text { get; set; } = "";
        public ResumeProfile Profile { get; set; } = new([], 0, [], null);
        [JsonIgnore] public float[] Embedding { get; set; } = [];
        public DateTimeOffset CreatedAt { get; set; }
    }

    public class Job
    {
        public Guid Id { get; set; }
        public string Company { get; set; } = "";
        public string Title { get; set; } = "";
        public string Location { get; set; } = "";
        public string Url { get; set; } = "";
        public string Description { get; set; } = "";
        public string Source { get; set; } = "";
        // company|title|location, normalized: catches the same role re-posted under a new URL.
        [JsonIgnore] public string DedupeKey { get; set; } = "";
        public DateTimeOffset? PostedAt { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public bool IsOpen { get; set; }
        [JsonIgnore] public float[] Embedding { get; set; } = [];
    }

    // A passage of a resume or job description, embedded for retrieval (RAG).
    public class Chunk
    {
        public long Id { get; set; }
        public Guid? ResumeId { get; set; }
        public Guid? JobId { get; set; }
        public int Index { get; set; }
        public string Text { get; set; } = "";
        public float[] Embedding { get; set; } = [];
    }

    public enum ApplicationStatus { Saved, Drafted, Approved, Submitted, Interviewing, Offer, Rejected, Withdrawn }

    public class Application
    {
        public Guid Id { get; set; }
        public Guid ResumeId { get; set; }
        public Guid JobId { get; set; }
        public Job? Job { get; set; }
        public ApplicationStatus Status { get; set; }
        public string? TailoredResume { get; set; }
        public string? Notes { get; set; }
        public DateTimeOffset? ApprovedAt { get; set; }
        public DateTimeOffset? SubmittedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
    {
        // Own schema so nothing touches the existing tables in the shared database.
        public const string Schema = "resumeai";

        public DbSet<Resume> Resumes => Set<Resume>();
        public DbSet<Job> Jobs => Set<Job>();
        public DbSet<Chunk> Chunks => Set<Chunk>();
        public DbSet<Application> Applications => Set<Application>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            b.HasDefaultSchema(Schema);

            b.Entity<Resume>().Property(r => r.Profile)
                .HasColumnType("jsonb")
                .HasConversion(
                    p => JsonSerializer.Serialize(p, JsonSerializerOptions.Web),
                    s => JsonSerializer.Deserialize<ResumeProfile>(s, JsonSerializerOptions.Web)!);

            b.Entity<Job>().HasIndex(j => j.Url).IsUnique();
            b.Entity<Job>().HasIndex(j => j.DedupeKey).IsUnique();
            b.Entity<Job>().HasIndex(j => j.IsOpen);

            b.Entity<Chunk>().HasIndex(c => c.ResumeId);
            b.Entity<Chunk>().HasIndex(c => c.JobId);
            b.Entity<Chunk>().HasOne<Resume>().WithMany().HasForeignKey(c => c.ResumeId).OnDelete(DeleteBehavior.Cascade);
            b.Entity<Chunk>().HasOne<Job>().WithMany().HasForeignKey(c => c.JobId).OnDelete(DeleteBehavior.Cascade);

            b.Entity<Application>().HasIndex(a => new { a.ResumeId, a.JobId }).IsUnique();
            b.Entity<Application>().HasOne<Resume>().WithMany().HasForeignKey(a => a.ResumeId).OnDelete(DeleteBehavior.Cascade);
            b.Entity<Application>().Property(a => a.Status).HasConversion<string>();
        }
    }
}
