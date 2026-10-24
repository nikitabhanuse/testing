namespace WebApplicationAI.AI.Models
{
    public class GeneratePromptRequest
    {
        public List<string>? Skills { get; set; }
        public int ExperienceYears { get; set; }
        public decimal ExpectedSalaryLpa { get; set; }
        public string WorkPreference { get; set; } = string.Empty;
    }
}
