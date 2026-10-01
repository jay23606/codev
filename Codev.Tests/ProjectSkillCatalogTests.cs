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
