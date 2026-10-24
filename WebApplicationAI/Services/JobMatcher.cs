using System.Text.RegularExpressions;
using WebApplicationAI.AI.Models;
using WebApplicationAI.Data;

namespace WebApplicationAI.Services
{
    // Deterministic scoring so the ranking is explainable and repeatable; the LLM only extracts the profile.
    // Weights: skills 35 (keyword), semantic 35 (embeddings), experience 20, location 10.
    // Semantic carries equal weight so a job that shares buzzwords but not the role (e.g. a TypeScript
    // tooling job for a C# developer) doesn't outrank a genuinely similar role.
    public static partial class JobMatcher
    {
        // ponytail: cosine range for nomic-embed-text, tuned by eye; recalibrate if the model changes.
        private const float SimFloor = 0.45f, SimCeiling = 0.80f;

        public static List<JobMatch> Rank(ResumeProfile profile, float[] resumeVector, IEnumerable<Job> jobs, string? preferredLocation)
        {
            var location = string.IsNullOrWhiteSpace(preferredLocation) ? profile.Location : preferredLocation;

            return jobs.Select(job =>
                {
                    var text = $"{job.Title} {job.Description}";
                    var matched = profile.Skills
                        .Where(s => !string.IsNullOrWhiteSpace(s) && ContainsTerm(text, s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Saturates at 8 matched skills so a long resume isn't required to score well.
                    var skillScore = 35 * Math.Min(matched.Count, 8) / 8;

                    var similarity = RagService.Similarity(resumeVector, job.Embedding);
                    var semanticScore = (int)Math.Round(35 *Math.Clamp((similarity - SimFloor) / (SimCeiling - SimFloor), 0, 1));

                    var required = RequiredYears(job.Description);
                    var experienceScore = required switch
                    {
                        null => 10, // not stated: neutral
                        var r when profile.YearsOfExperience >= r => 20,
                        var r when profile.YearsOfExperience >= r - 2 => 10,
                        _ => 0
                    };

                    var locationMatch = !string.IsNullOrWhiteSpace(location)
                        && (job.Location.Contains(location, StringComparison.OrdinalIgnoreCase)
                            || location.Contains(job.Location.Split(',')[0].Trim(), StringComparison.OrdinalIgnoreCase)
                            || job.Location.Contains("remote", StringComparison.OrdinalIgnoreCase));

                    return new JobMatch(job, skillScore + semanticScore + experienceScore + (locationMatch ? 10 : 0),
                        matched, MathF.Round(similarity, 3), required, locationMatch);
                })
                .OrderByDescending(m => m.Score)
                .ThenByDescending(m => m.Job.PostedAt)
                .ToList();
        }

        // Smallest "N years" / "N+ years" / "N-M years" figure in the description, i.e. the minimum asked for.
        public static int? RequiredYears(string description)
        {
            var years = YearsRegex().Matches(description)
                .Select(m => int.Parse(m.Groups[1].Value))
                .Where(y => y is > 0 and < 30)
                .ToList();
            return years.Count == 0 ? null : years.Min();
        }

        // Whole-term match, so "Java" doesn't hit "JavaScript" and "C#" still matches.
        public static bool ContainsTerm(string text, string term) =>
            Regex.IsMatch(text, $@"(?<![\w]){Regex.Escape(term.Trim())}(?![\w])", RegexOptions.IgnoreCase);

        [GeneratedRegex(@"(\d{1,2})\s*\+?\s*(?:(?:-|–|to)\s*\d{1,2}\s*)?(?:years?|yrs?)", RegexOptions.IgnoreCase)]
        private static partial Regex YearsRegex();
    }
}
