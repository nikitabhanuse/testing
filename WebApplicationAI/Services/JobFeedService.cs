using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebApplicationAI.AI.Abstractions;
using WebApplicationAI.AI.Models;

namespace WebApplicationAI.Services
{
    // Reads public employer career feeds (Greenhouse job boards, SmartRecruiters postings API).
    // No keys needed; listings come straight from the employer's ATS, so they are open at fetch time.
    public partial class JobFeedService : IJobFeedService
    {
        private static readonly string[] IndiaPlaces =
        [
            "india", "bengaluru", "bangalore", "pune", "hyderabad", "mumbai", "chennai",
            "gurgaon", "gurugram", "noida", "delhi", "kolkata", "ahmedabad", "coimbatore", "kochi"
        ];

        private readonly HttpClient _http;
        private readonly IReadOnlyList<JobSource> _sources;
        private readonly ILogger<JobFeedService> _logger;

        public JobFeedService(HttpClient http, IConfiguration config, ILogger<JobFeedService> logger)
        {
            _http = http;
            _sources = config.GetSection("JobSources").Get<List<JobSource>>() ?? [];
            _logger = logger;
        }

        public async Task<IReadOnlyList<JobPosting>> GetJobsAsync(CancellationToken cancellationToken = default)
        {
            var perSource = await Task.WhenAll(_sources.Select(s => FetchSourceAsync(s, cancellationToken)));

            return perSource
                .SelectMany(jobs => jobs)
                .Where(j => IsDotNet(j.Title + " " + j.Description) && IsIndia(j.Location))
                .DistinctBy(j => j.Url)
                .DistinctBy(DedupeKey)
                .OrderByDescending(j => j.PostedAt)
                .ToList();
        }

        public static string DedupeKey(JobPosting j) => $"{Normalize(j.Company)}|{Normalize(j.Title)}|{Normalize(j.Location)}";

        private async Task<List<JobPosting>> FetchSourceAsync(JobSource source, CancellationToken ct)
        {
            try
            {
                return source.Ats.ToLowerInvariant() switch
                {
                    "greenhouse" => await FetchGreenhouseAsync(source.Company, ct),
                    "smartrecruiters" => await FetchSmartRecruitersAsync(source.Company, ct),
                    _ => throw new NotSupportedException($"Unknown ATS '{source.Ats}'.")
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One broken feed must not take down the others.
                _logger.LogWarning(ex, "Job feed {Ats}/{Company} failed.", source.Ats, source.Company);
                return [];
            }
        }

        private async Task<List<JobPosting>> FetchGreenhouseAsync(string board, CancellationToken ct)
        {
            var root = await GetJsonAsync($"https://boards-api.greenhouse.io/v1/boards/{board}/jobs?content=true", ct);

            return root?["jobs"]?.AsArray().Select(j => new JobPosting(
                Company: board,
                Title: (string?)j?["title"] ?? "",
                Location: (string?)j?["location"]?["name"] ?? "",
                Url: (string?)j?["absolute_url"] ?? "",
                // Greenhouse returns HTML-escaped HTML.
                Description: StripHtml(WebUtility.HtmlDecode((string?)j?["content"] ?? "")),
                PostedAt: DateTimeOffset.TryParse((string?)j?["updated_at"], out var d) ? d : null,
                Source: "greenhouse")).ToList() ?? [];
        }

        private async Task<List<JobPosting>> FetchSmartRecruitersAsync(string company, CancellationToken ct)
        {
            // The list endpoint has no description, so search narrowly, then fetch details per posting.
            var ids = new HashSet<string>();
            foreach (var term in new[] { ".net", "c#" })
            {
                var list = await GetJsonAsync(
                    $"https://api.smartrecruiters.com/v1/companies/{company}/postings?country=in&limit=100&q={Uri.EscapeDataString(term)}", ct);
                foreach (var p in list?["content"]?.AsArray() ?? [])
                    if ((string?)p?["id"] is { } id) ids.Add(id);
            }

            var jobs = await Task.WhenAll(ids.Select(async id =>
            {
                var p = await GetJsonAsync($"https://api.smartrecruiters.com/v1/companies/{company}/postings/{id}", ct);
                if (p is null || (bool?)p["active"] == false) return null;

                var sections = p["jobAd"]?["sections"]?.AsObject()
                    .Select(s => (string?)s.Value?["text"]) ?? [];
                var loc = p["location"];

                return new JobPosting(
                    Company: (string?)p["company"]?["name"] ?? company,
                    Title: (string?)p["name"] ?? "",
                    Location: string.Join(", ", new[] { (string?)loc?["city"], (string?)loc?["region"], (string?)loc?["country"] }
                        .Where(s => !string.IsNullOrWhiteSpace(s))),
                    Url: (string?)p["postingUrl"] ?? "",
                    Description: StripHtml(string.Join("\n", sections)),
                    PostedAt: DateTimeOffset.TryParse((string?)p["releasedDate"], out var d) ? d : null,
                    Source: "smartrecruiters");
            }));

            return jobs.OfType<JobPosting>().ToList();
        }

        private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
        {
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }

        private static bool IsDotNet(string text) => DotNetRegex().IsMatch(text);

        // SmartRecruiters returns "in" as the country code.
        private static bool IsIndia(string location) =>
            IndiaPlaces.Any(p => location.Contains(p, StringComparison.OrdinalIgnoreCase))
            || location.EndsWith(", in", StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string s) => WhitespaceRegex().Replace(s.Trim().ToLowerInvariant(), " ");

        private static string StripHtml(string html) =>
            WhitespaceRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(html, " ")), " ").Trim();

        [GeneratedRegex(@"(?<![\w.])(\.net|asp\.net|dot\s?net|c#)(?!\w)", RegexOptions.IgnoreCase)]
        private static partial Regex DotNetRegex();

        [GeneratedRegex("<[^>]+>")]
        private static partial Regex TagRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
    }
}
