namespace WebApplicationAI.AI.Abstractions
{
    public interface IPromptBuilder
    {
        string Build(string resumeText);
        string BuildProfile(string resumeText);
    }
}
