namespace WebApplicationAI.AI.Models
{
    public record JobPosting(
        string Company,
        string Title,
        string Location,
        string Url,
        string Description,
        DateTimeOffset? PostedAt,
        string Source);

    // Ats: "greenhouse" or "smartrecruiters"; Company: the board slug / company identifier in that ATS.
    public record JobSource(string Ats, string Company);

    public record ResumeProfile(
        List<string> Skills,
        int YearsOfExperience,
        List<string> JobTitles,
        string? Location);

    public record JobMatch(
        Data.Job Job,
        int Score,
        List<string> MatchedSkills,
        float Similarity,
        int? RequiredYears,
        bool LocationMatch);
}
