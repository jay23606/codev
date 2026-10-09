using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codev;

/// <summary>Names and transcript-wraps the on-demand, read-only agent skill tool.</summary>
public static class AgentSkillTool
{
    public static string PermissionResource(SlashCommandDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.Scope is not ("skill-user" or "skill-project") ||
            !skill.Name.StartsWith("/skill-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only discovered user or trusted-project skills have a permission resource.", nameof(skill));
        return skill.Name["/skill-".Length..];
    }

    public static string FunctionName(SlashCommandDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.Scope is not ("skill-user" or "skill-project") ||
            !skill.Name.StartsWith("/skill-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only discovered user or trusted-project skills can be exposed to an agent.", nameof(skill));
        var slug = skill.Name["/skill-".Length..];
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(skill.Scope + "\0" + slug))).ToLowerInvariant()[..12];
        return $"load_skill_{slug}_{digest}";
    }

    public static string FormatLoadedPrompt(SlashCommandDefinition skill, string prompt)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return JsonSerializer.Serialize(new AgentSkillToolOutput("agent_skill_output", "Agent skill guidance",
            skill.Name, skill.ScopeLabel, prompt), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed record AgentSkillToolOutput(string Type, string Source, string Name, string Scope, string Content);
}
