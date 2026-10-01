namespace Codev.Tests;

public sealed class ProjectSkillCatalogTests
{
    [Fact]
    public async Task Lists_skill_metadata_without_retaining_prompt_and_loads_body_only_on_invocation()
    {
        var root = CreateTempDirectory();
        try
        {
            var user = Path.Combine(root, "user-skills");
            var skillDirectory = Path.Combine(user, "review");
            Directory.CreateDirectory(skillDirectory);
            var skillPath = Path.Combine(skillDirectory, "SKILL.md");
            await File.WriteAllTextAsync(skillPath, Markdown("Review {{area}} for bugs.", "area"));

            var result = await ProjectSkillCatalog.LoadAsync(user, null, includeProjectSkills: false);
            var skill = Assert.Single(result.Skills);
            Assert.Equal("/skill-review", skill.Name);
            Assert.Equal("user", skill.Description);
            Assert.Equal("skill-user", skill.Scope);
            Assert.Equal(["area"], skill.ArgumentNames);
            Assert.Null(skill.Prompt);

            var expanded = await ProjectSkillCatalog.ReadPromptAsync(skill, user, null, projectTrusted: false,
                "/skill-review area=authentication");
            Assert.True(expanded.Success, expanded.Error);
            Assert.Equal("Review authentication for bugs.", expanded.Prompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Trusted_project_skill_overrides_user_skill_and_untrusted_skills_are_hidden()
    {
        var root = CreateTempDirectory();
        try
        {
            var user = Path.Combine(root, "user-skills");
            var project = Path.Combine(root, "project");
            var userSkill = Path.Combine(user, "review");
            var projectSkill = Path.Combine(project, ".codev", "skills", "review");
            Directory.CreateDirectory(userSkill);
            Directory.CreateDirectory(projectSkill);
            await File.WriteAllTextAsync(Path.Combine(userSkill, "SKILL.md"), Markdown("User instructions."));
            await File.WriteAllTextAsync(Path.Combine(projectSkill, "SKILL.md"), Markdown("Project instructions."));

            var untrusted = await ProjectSkillCatalog.LoadAsync(user, project, includeProjectSkills: false);
            Assert.Equal("skill-user", Assert.Single(untrusted.Skills).Scope);
            var trusted = await ProjectSkillCatalog.LoadAsync(user, project, includeProjectSkills: true);
            var merged = Assert.Single(trusted.Skills);
            Assert.Equal("skill-project", merged.Scope);
            Assert.Equal("/skill-review", merged.Name);
            var expanded = await ProjectSkillCatalog.ReadPromptAsync(merged, user, project, projectTrusted: true, merged.Name);
            Assert.True(expanded.Success, expanded.Error);
            Assert.Equal("Project instructions.", expanded.Prompt);
            var revoked = await ProjectSkillCatalog.ReadPromptAsync(merged, user, project, projectTrusted: false, merged.Name);
            Assert.False(revoked.Success);
            Assert.Contains("trusted", revoked.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Discovers_opencode_claude_and_agent_skills_only_inside_trusted_project()
    {
        var root = CreateTempDirectory();
        try
        {
            var user = Path.Combine(root, "user-skills");
            var project = Path.Combine(root, "project");
            var codevSkill = Path.Combine(project, ".codev", "skills", "review");
            var openCodeSkill = Path.Combine(project, ".opencode", "skills", "review");
            var claudeSkill = Path.Combine(project, ".claude", "skills", "release");
            var agentSkill = Path.Combine(project, ".agents", "skills", "tests");
            Directory.CreateDirectory(codevSkill);
            Directory.CreateDirectory(openCodeSkill);
            Directory.CreateDirectory(claudeSkill);
            Directory.CreateDirectory(agentSkill);
            await File.WriteAllTextAsync(Path.Combine(codevSkill, "SKILL.md"), Markdown("Codev review."));
            await File.WriteAllTextAsync(Path.Combine(openCodeSkill, "SKILL.md"),
                "---\nname: review\ndescription: OpenCode format review skill\nlicense: MIT\ncompatibility: Works with Codev\nmetadata:\n  author: team\n  version: '1'\n---\nOpenCode review.");
            await File.WriteAllTextAsync(Path.Combine(claudeSkill, "SKILL.md"),
                "---\nname: release\ndescription: Claude format release skill\n---\nClaude release.");
            await File.WriteAllTextAsync(Path.Combine(agentSkill, "SKILL.md"),
                "---\nname: tests\ndescription: Agent format testing skill\n---\nAgent tests.");
            var mismatched = Path.Combine(project, ".opencode", "skills", "wrong-folder");
            Directory.CreateDirectory(mismatched);
            await File.WriteAllTextAsync(Path.Combine(mismatched, "SKILL.md"),
                "---\nname: different-name\ndescription: Mismatched skill\n---\nDo not load.");

            var untrusted = await ProjectSkillCatalog.LoadAsync(user, project, includeProjectSkills: false);
            Assert.Empty(untrusted.Skills);

            var trusted = await ProjectSkillCatalog.LoadAsync(user, project, includeProjectSkills: true);
            Assert.Equal(["/skill-review", "/skill-release", "/skill-tests"], trusted.Skills.Select(skill => skill.Name));
            Assert.Contains(trusted.Warnings, warning => warning.Contains("match its folder", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.Combine(codevSkill, "SKILL.md"), trusted.Skills[0].FilePath);
            var loaded = await ProjectSkillCatalog.ReadPromptAsync(trusted.Skills[2], user, project, projectTrusted: true,
                trusted.Skills[2].Name);
            Assert.True(loaded.Success, loaded.Error);
            Assert.Equal("Agent tests.", loaded.Prompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Discovers_compatible_user_skill_directories_and_reloads_from_its_original_root()
    {
        var root = CreateTempDirectory();
        try
        {
            var codevUser = Path.Combine(root, "codev-user");
            var compatibleUser = Path.Combine(root, "opencode", "skills");
            var directory = Path.Combine(compatibleUser, "release");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"),
                "---\nname: release\ndescription: Shared user release skill\n---\nPrepare release notes.");

            var loaded = await ProjectSkillCatalog.LoadAsync(codevUser, null, includeProjectSkills: false,
                additionalUserSkillsDirectories: [compatibleUser]);
            var skill = Assert.Single(loaded.Skills);
            Assert.Equal("skill-user", skill.Scope);
            Assert.Equal(Path.Combine(directory, "SKILL.md"), skill.FilePath);
            var expanded = await ProjectSkillCatalog.ReadPromptAsync(skill, codevUser, null, projectTrusted: false,
                skill.Name, additionalUserSkillsDirectories: [compatibleUser]);
            Assert.True(expanded.Success, expanded.Error);
            Assert.Equal("Prepare release notes.", expanded.Prompt);

            var unrelatedFile = Path.Combine(directory, "notes.md");
            await File.WriteAllTextAsync(unrelatedFile, Markdown("Do not load this file."));
            var forged = skill with { FilePath = unrelatedFile };
            var rejected = await ProjectSkillCatalog.ReadPromptAsync(forged, codevUser, null, projectTrusted: false, skill.Name,
                additionalUserSkillsDirectories: [compatibleUser]);
            Assert.False(rejected.Success);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Skips_symlinked_opencode_skill_root_and_rejects_its_saved_reference()
    {
        var root = CreateTempDirectory();
        try
        {
            var project = Path.Combine(root, "project");
            var external = Path.Combine(root, "external-skills");
            Directory.CreateDirectory(project);
            var externalSkill = Path.Combine(external, "review");
            Directory.CreateDirectory(externalSkill);
            await File.WriteAllTextAsync(Path.Combine(externalSkill, "SKILL.md"), Markdown("External instructions."));
            try { Directory.CreateSymbolicLink(Path.Combine(project, ".opencode"), external); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }

            var loaded = await ProjectSkillCatalog.LoadAsync(Path.Combine(root, "user-skills"), project, includeProjectSkills: true);
            Assert.Empty(loaded.Skills);
            Assert.Contains(loaded.Warnings, warning => warning.Contains(".opencode/skills", StringComparison.Ordinal));
            var stale = new SlashCommandDefinition("/skill-review", "review", SlashCommandAction.UserPrompt, null, null,
                "skill-project", FilePath: Path.Combine(externalSkill, "SKILL.md"));
            var expanded = await ProjectSkillCatalog.ReadPromptAsync(stale, Path.Combine(root, "user-skills"), project,
                projectTrusted: true, stale.Name);
            Assert.False(expanded.Success);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Project_skills_are_not_loaded_through_a_symlinked_project_ancestor()
    {
        var root = CreateTempDirectory();
        try
        {
            var project = Path.Combine(root, "project");
            var linkedProject = Path.Combine(root, "linked-project");
            var skillDirectory = Path.Combine(project, ".codev", "skills", "review");
            Directory.CreateDirectory(skillDirectory);
            await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), Markdown("Linked project instructions."));
            try { Directory.CreateSymbolicLink(linkedProject, project); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }

            var result = await ProjectSkillCatalog.LoadAsync(Path.Combine(root, "user-skills"), linkedProject, includeProjectSkills: true);

            Assert.Empty(result.Skills);
            var skill = new SlashCommandDefinition("/skill-review", "review", SlashCommandAction.UserPrompt,
                null, null, "skill-project");
            var expanded = await ProjectSkillCatalog.ReadPromptAsync(skill, Path.Combine(root, "user-skills"), linkedProject,
                projectTrusted: true, skill.Name);
            Assert.False(expanded.Success);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Skill_is_reloaded_before_inserting_so_changed_files_are_not_stale()
    {
        var root = CreateTempDirectory();
        try
        {
            var user = Path.Combine(root, "user-skills");
            var directory = Path.Combine(user, "explain");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "SKILL.md");
            await File.WriteAllTextAsync(path, Markdown("Original."));
            var skill = Assert.Single((await ProjectSkillCatalog.LoadAsync(user, null, false)).Skills);
            await File.WriteAllTextAsync(path, Markdown("Updated."));

            var expanded = await ProjectSkillCatalog.ReadPromptAsync(skill, user, null, false, skill.Name);

            Assert.True(expanded.Success, expanded.Error);
            Assert.Equal("Updated.", expanded.Prompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task User_only_skill_remains_slash_invocable_but_is_marked_for_model_tool_filtering()
    {
        var root = CreateTempDirectory();
        try
        {
            var user = Path.Combine(root, "user-skills");
            var directory = Path.Combine(user, "private-review");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"),
                "---\ndescription: Private review workflow\narguments: area\nuser-only: true\n---\nReview {{area}} privately.");

            var loaded = await ProjectSkillCatalog.LoadAsync(user, null, includeProjectSkills: false);
            var skill = Assert.Single(loaded.Skills);
            Assert.True(skill.UserOnly);

            var expanded = await ProjectSkillCatalog.ReadPromptAsync(skill, user, null, projectTrusted: false,
                "/skill-private-review area=security");
            Assert.True(expanded.Success, expanded.Error);
            Assert.Equal("Review security privately.", expanded.Prompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("Bad Name", "invalid name")]
    [InlineData("invalid", "description")]
    [InlineData("oversized", "size limit")]
    public async Task Invalid_skill_folders_and_files_are_omitted(string folderName, string expectedWarning)
    {
        var root = CreateTempDirectory();
        try
        {
            var user = Path.Combine(root, "user-skills");
            var directory = Path.Combine(user, folderName);
            Directory.CreateDirectory(directory);
            var contents = expectedWarning == "size limit"
                ? Markdown(new string('x', CustomSlashCommandService.MaxCommandFileBytes))
                : folderName == "invalid" ? "no metadata" : Markdown("Valid prompt.");
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), contents);

            var result = await ProjectSkillCatalog.LoadAsync(user, null, false);

            Assert.Empty(result.Skills);
            Assert.Contains(result.Warnings, warning => warning.Contains(expectedWarning, StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Markdown(string prompt, string? arguments = null) =>
        $"---\ndescription: user\n{(arguments is null ? "" : $"arguments: {arguments}\n")}---\n{prompt}";

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "codev-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
