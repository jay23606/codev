using Codev;
using System.Runtime.InteropServices;

namespace Codev.Tests;

public sealed class AgentProfileCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-agent-profiles", Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "Codev-agent-project", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Parses_bounded_metadata_body_and_tool_permissions()
    {
        const string contents = "---\nname: Debug\ndescription: Reproduce and isolate a defect.\nmodel: qwen-coder\ntemperature: 0.2\nmax_steps: 6\ndefault_permission: ask\ntools: read_file=allow, search_files=allow, run_command=ask, mcp:*=deny\n---\nFirst reproduce the reported problem.\nThen test one explanation at a time.\n";

        var valid = AgentProfileCatalog.TryParse("debug.md", contents, "user", out var profile, out var error);

        Assert.True(valid, error);
        Assert.Equal("Debug", profile!.Name);
        Assert.Equal("qwen-coder", profile.Model);
        Assert.Equal(0.2, profile.Temperature);
        Assert.Equal(6, profile.MaxSteps);
        Assert.Equal(AgentToolPermission.Allow, profile.ToolPermissions["read_file"]);
        Assert.Equal(AgentToolPermission.Deny, profile.ToolPermissions["mcp:*"]);
        Assert.Contains("First reproduce", profile.Instructions);
    }

    [Fact]
    public void Profile_references_accept_display_names_while_file_stems_stay_slug_only()
    {
        Assert.Equal("Smoke QA", AgentProfileCatalog.NormalizeReferenceName("Smoke QA"));
        Assert.Null(AgentProfileCatalog.NormalizeReferenceName("../secrets"));
        Assert.Null(AgentProfileCatalog.NormalizeReferenceName("."));
        Assert.Null(AgentProfileCatalog.NormalizeReferenceName(".."));
        Assert.Null(AgentProfileCatalog.NormalizeReferenceName("Smoke\\QA"));
        Assert.Null(AgentProfileCatalog.NormalizeReferenceName(" Smoke QA"));
        Assert.Null(AgentProfileCatalog.NormalizeReferenceName(new string('x', 81)));
        Assert.Equal("smoke-qa", AgentProfileCatalog.NormalizeProfileFileStem("smoke-qa"));
        Assert.Null(AgentProfileCatalog.NormalizeProfileFileStem("Smoke QA"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("path/name")]
    [InlineData("path\\name")]
    public void Rejects_path_like_profile_display_names(string name)
    {
        var contents = $"---\nname: {name}\ndescription: Test profile\n---\nInstructions.";

        Assert.False(AgentProfileCatalog.TryParse("test.md", contents, "user", out _, out var error));
        Assert.Contains("display name", error);
    }

    [Fact]
    public void Parses_and_enforces_edit_path_allow_and_deny_globs()
    {
        const string contents = "---\nname: Frontend\ndescription: Edit frontend files only.\nedit_paths: src/**, index.html\ndeny_edit_paths: src/secrets/**\n---\nKeep changes focused.";
        Assert.True(AgentProfileCatalog.TryParse("frontend.md", contents, "user", out var profile, out var error), error);

        Assert.True(AgentProfilePolicy.CanEditPath(profile, "src/components/app.ts", Path.GetTempPath()));
        Assert.True(AgentProfilePolicy.CanEditPath(profile, "index.html", Path.GetTempPath()));
        Assert.False(AgentProfilePolicy.CanEditPath(profile, "README.md", Path.GetTempPath()));
        Assert.False(AgentProfilePolicy.CanEditPath(profile, "src/secrets/token.ts", Path.GetTempPath()));
    }

    [Fact]
    public void Tool_permission_globs_match_and_later_rules_override_earlier_rules()
    {
        const string contents = "---\nname: McpReader\ndescription: Read-only GitHub tools.\ntools: mcp_github_*=deny, mcp_github_search_*=allow, read_file_?=ask\n---\nSearch only.";
        Assert.True(AgentProfileCatalog.TryParse("mcp-reader.md", contents, "user", out var profile, out var error), error);

        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(profile, "mcp_github_search_repos_123"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "mcp_github_create_issue_456"));
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(profile, "read_file_1"));
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(profile, "run_command"));
    }

    [Fact]
    public void Command_permission_globs_apply_to_command_tools_in_order()
    {
        const string contents = "---\nname: BuildRules\ndescription: Allow checks and block publishing.\ncommands: *=allow, git *=ask, git push *=deny, git status --short=allow\n---\nUse project commands carefully.";
        Assert.True(AgentProfileCatalog.TryParse("build-rules.md", contents, "user", out var profile, out var error), error);

        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(profile, "run_command", "npm test"));
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(profile, "run_command", "git status --branch"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "verify_command", "git push origin main"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "run_command", "Remove-Item -Recurse signaling; git push origin main"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(profile, "run_command", "git status --short"));
    }

    [Theory]
    [InlineData("temperature: 2.1", "temperature must be")]
    [InlineData("max_steps: 99", "max_steps must be")]
    [InlineData("default_permission: execute", "default_permission must be")]
    [InlineData("tools: run_command=execute", "tools must be")]
    [InlineData("commands: =deny", "commands must be")]
    [InlineData("edit_paths: ../outside.txt", "edit_paths must contain safe")]
    [InlineData("deny_edit_paths: /rooted.txt", "deny_edit_paths must contain safe")]
    [InlineData("unknown: value", "Unsupported agent profile field")]
    public void Rejects_invalid_profile_values(string field, string errorText)
    {
        var contents = $"---\nname: Test\ndescription: Test profile\n{field}\n---\nInstructions.";
        Assert.False(AgentProfileCatalog.TryParse("test.md", contents, "user", out _, out var error));
        Assert.Contains(errorText, error);
    }

    [Fact]
    public async Task User_profiles_load_without_project_trust_and_project_profiles_require_explicit_scope()
    {
        var userDirectory = Path.Combine(_root, "user");
        Directory.CreateDirectory(userDirectory);
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(Path.Combine(_project, ".codev", "agents"));
        await File.WriteAllTextAsync(Path.Combine(userDirectory, "ask.md"), Profile("Ask"));
        await File.WriteAllTextAsync(Path.Combine(_project, ".codev", "agents", "code.md"), Profile("Code"));

        var userOnly = await AgentProfileCatalog.LoadAsync(userDirectory, _project, includeProjectProfiles: false);
        var both = await AgentProfileCatalog.LoadAsync(userDirectory, _project, includeProjectProfiles: true);

        Assert.Equal("user", Assert.Single(userOnly.Profiles, profile => profile.Name == "Ask").Scope);
        Assert.Contains(userOnly.Profiles, profile => profile.Name == "Code" && profile.Scope == "built-in");
        Assert.Equal(new[] { "Code", "Ask", "Debug", "Orchestrator", "Plan" }, both.Profiles.Select(profile => profile.Name));
        Assert.Equal("project", both.Profiles[0].Scope);
    }

    [Fact]
    public async Task Project_profile_is_not_loaded_when_hard_linked_to_a_file_outside_the_project()
    {
        if (!FileHardLinkInspector.IsSupportedPlatform) return;
        var profileDirectory = Path.Combine(_project, ".codev", "agents");
        var external = Path.Combine(_root, "outside-profile.md");
        Directory.CreateDirectory(profileDirectory);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(external, Profile("Outside"));
        if (!TryCreateHardLink(external, Path.Combine(profileDirectory, "outside.md"))) return;

        var loaded = await AgentProfileCatalog.LoadAsync(Path.Combine(_root, "user"), _project, includeProjectProfiles: true);

        Assert.DoesNotContain(loaded.Profiles, profile => profile.Name == "Outside");
        Assert.Contains(loaded.Warnings, warning => warning.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task User_profile_store_saves_valid_documents_and_rejects_invalid_or_duplicate_profiles()
    {
        var directory = Path.Combine(_root, "editable-user-profiles");
        var store = new UserAgentProfileStore(directory);
        var original = Profile("Reviewer");

        await store.SaveAsync("reviewer.md", original);
        Assert.Equal(new AgentProfileDocument("reviewer.md", original), Assert.Single(await store.LoadDocumentsAsync()));

        var updated = original.Replace("Follow the user's instructions.", "Review changes with focused feedback.", StringComparison.Ordinal);
        await store.SaveAsync("reviewer.md", updated);
        Assert.Equal(updated, Assert.Single(await store.LoadDocumentsAsync()).Contents);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("invalid.md", "not a profile"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("profile with spaces.md", Profile("Profile With Spaces")));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("other.md", Profile("Reviewer")));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("orchestrator.md", Profile("Orchestrator")));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("plan.md", Profile("Plan")));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("large.md", Profile("Large") + string.Concat(Enumerable.Repeat("🙂", 9_000))));
        Assert.False(File.Exists(Path.Combine(directory, "invalid.md")));
        Assert.False(File.Exists(Path.Combine(directory, "other.md")));
        Assert.False(File.Exists(Path.Combine(directory, "large.md")));
        Assert.Equal(updated, await File.ReadAllTextAsync(Path.Combine(directory, "reviewer.md")));
    }

    [Fact]
    public async Task Profile_name_collision_prefers_trusted_project_scope()
    {
        var userDirectory = Path.Combine(_root, "user");
        Directory.CreateDirectory(userDirectory);
        Directory.CreateDirectory(Path.Combine(_project, ".codev", "agents"));
        await File.WriteAllTextAsync(Path.Combine(userDirectory, "code.md"), Profile("Code"));
        await File.WriteAllTextAsync(Path.Combine(_project, ".codev", "agents", "code.md"), Profile("Code"));

        var loaded = await AgentProfileCatalog.LoadAsync(userDirectory, _project, includeProjectProfiles: true);

        var profile = Assert.Single(loaded.Profiles, profile => profile.Name == "Code");
        Assert.Equal("Code", profile.Name);
        Assert.Equal("project", profile.Scope);
    }

    [Fact]
    public async Task Imports_bounded_OpenCode_agent_frontmatter_and_maps_permissions_safely()
    {
        Directory.CreateDirectory(_project);
        var openCodeAgents = Path.Combine(_project, ".opencode", "agents");
        Directory.CreateDirectory(openCodeAgents);
        await File.WriteAllTextAsync(Path.Combine(openCodeAgents, "reviewer.md"), """
            ---
            description: Review changes and run focused checks.
            mode: subagent
            model: anthropic/claude-sonnet-4
            temperature: 0.1
            permission:
              edit: deny
              bash:
                npm test: allow
                git push*: deny
                git status*: ask
            tools:
              read: true
              webfetch: false
            ---
            Review changes for correctness and test coverage.
            """);

        var loaded = await AgentProfileCatalog.LoadAsync(Path.Combine(_root, "user"), _project, includeProjectProfiles: true);

        var profile = Assert.Single(loaded.Profiles, item => item.Name == "reviewer");
        Assert.Equal("project-opencode", profile.Scope);
        Assert.Equal("subagent", profile.Mode);
        Assert.Null(profile.Model);
        Assert.Equal(0.1, profile.Temperature);
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "write_file"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(profile, "read_file"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(profile, "run_command", "npm test"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "run_command", "git push origin main"));
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(profile, "verify_command", "git status --short"));
        Assert.Contains(loaded.Warnings, warning => warning.Contains("model preference was not applied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Imports_OpenCode_V2_ordered_permissions_steps_and_scoped_edit_rules()
    {
        Directory.CreateDirectory(_project);
        var openCodeAgents = Path.Combine(_project, ".opencode", "agents");
        Directory.CreateDirectory(openCodeAgents);
        await File.WriteAllTextAsync(Path.Combine(openCodeAgents, "reviewer.md"), """
            ---
            description: Review changes and report risks.
            mode: subagent
            steps: 8
            permissions:
              - action: "*"
                resource: "*"
                effect: deny
              - action: "github_*"
                resource: "*"
                effect: allow
              - action: read
                resource: "*"
                effect: allow
              - action: edit
                resource: "src/**"
                effect: allow
              - action: edit
                resource: "src/private/**"
                effect: deny
              - action: shell
                resource: "git status *"
                effect: allow
              - action: shell
                resource: "git push *"
                effect: deny
            ---
            Review the requested changes without exposing private files.
            """);

        var loaded = await AgentProfileCatalog.LoadAsync(Path.Combine(_root, "user"), _project, includeProjectProfiles: true);

        var profile = Assert.Single(loaded.Profiles, item => item.Name == "reviewer");
        Assert.Equal("subagent", profile.Mode);
        Assert.Equal(8, profile.MaxSteps);
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(profile, "read_file"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "delegate_task"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(profile, "mcp_github_search_repos_abc"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "mcp_gitlab_search_repos_abc"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(profile, "run_command"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(profile, "run_command", "git status --short"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "run_command", "git push origin main"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "run_command", "npm test"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "run_command", "git status --short; Remove-Item -Recurse secret"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "run_command", "git status --short & Remove-Item -Recurse secret"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(profile, "run_command", "git status --short" + Environment.NewLine + "Remove-Item -Recurse secret"));
        Assert.True(AgentProfilePolicy.CanEditPath(profile, "src/components/app.cs", _project));
        Assert.False(AgentProfilePolicy.CanEditPath(profile, "src/private/credentials.cs", _project));
        Assert.False(AgentProfilePolicy.CanEditPath(profile, "README.md", _project));
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public async Task Imports_OpenCode_V2_profiles_with_path_scoped_reads()
    {
        var directory = Path.Combine(_project, ".opencode", "agents");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "writer.md"), """
            ---
            description: Write project documentation.
            steps: 6
            permissions:
              - action: read
                resource: "docs/**"
                effect: allow
              - action: read
                resource: "docs/private/**"
                effect: deny
            ---
            Write concise, accurate documentation.
            """);

        var loaded = await AgentProfileCatalog.LoadAsync(Path.Combine(_root, "user"), _project, includeProjectProfiles: true);

        var writer = Assert.Single(loaded.Profiles, profile => profile.Name == "writer");
        Assert.Equal("primary", writer.Mode);
        Assert.Equal(6, writer.MaxSteps);
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(writer, "read_file", resource: "docs/guide.md"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(writer, "read_file", resource: "docs/private/key.md"));
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(writer, "read_file", resource: "README.md"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(writer, "list_files"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(writer, "search_files"));
        Assert.True(AgentProfilePolicy.CanExposeReadPath(writer, "docs/guide.md"));
        Assert.False(AgentProfilePolicy.CanExposeReadPath(writer, "docs/private/key.md"));
        Assert.False(AgentProfilePolicy.CanExposeReadPath(writer, "README.md"));
        Assert.Equal(["docs/guide.md"], AgentProfilePolicy.FilterReadablePaths(writer,
            ["docs/guide.md", "docs/private/key.md", "README.md"]));
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public async Task Imports_OpenCode_V2_resource_scoped_glob_and_grep_rules()
    {
        var directory = Path.Combine(_project, ".opencode", "agents");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "explorer.md"), """
            ---
            description: Explore selected project files.
            permissions:
              - action: glob
                resource: "docs/**/*.md"
                effect: allow
              - action: grep
                resource: "TODO.*"
                effect: allow
              - action: grep
                resource: "SECRET.*"
                effect: deny
            ---
            Inspect documentation.
            """);

        var loaded = await AgentProfileCatalog.LoadAsync(Path.Combine(_root, "user"), _project, includeProjectProfiles: true);

        var explorer = Assert.Single(loaded.Profiles, profile => profile.Name == "explorer");
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(explorer, "glob_files", resource: "docs/**/*.md"));
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(explorer, "glob_files", resource: "src/**/*.cs"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(explorer, "grep_files", resource: "TODO.*"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(explorer, "grep_files", resource: "SECRET.*"));
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(explorer, "grep_files", resource: "password"));
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public async Task Imports_and_applies_path_scoped_OpenCode_V2_edit_ask_rules()
    {
        var directory = Path.Combine(_project, ".opencode", "agents");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "writer.md"), """
            ---
            description: Write documentation with approval.
            permissions:
              - action: edit
                resource: "*"
                effect: allow
              - action: edit
                resource: "docs/**"
                effect: ask
              - action: edit
                resource: "private/**"
                effect: deny
            ---
            Keep edits focused.
            """);

        var loaded = await AgentProfileCatalog.LoadAsync(Path.Combine(_root, "user"), _project, includeProjectProfiles: true);

        var writer = Assert.Single(loaded.Profiles, profile => profile.Name == "writer");
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(writer, "write_file", resource: "docs/guide.md"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(writer, "write_file", resource: "src/app.cs"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(writer, "write_file", resource: "private/token.txt"));
        Assert.True(AgentProfilePolicy.CanEditPath(writer, "docs/guide.md", _project));
        Assert.True(AgentProfilePolicy.CanEditPath(writer, "src/app.cs", _project));
        Assert.False(AgentProfilePolicy.CanEditPath(writer, "private/token.txt", _project));
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public async Task Imports_and_applies_resource_scoped_OpenCode_V2_task_permissions()
    {
        var directory = Path.Combine(_project, ".opencode", "agents");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "coordinator.md"), """
            ---
            description: Delegate only to approved child agents.
            mode: primary
            permissions:
              - action: task
                resource: "*"
                effect: deny
              - action: task
                resource: "Explore*"
                effect: allow
              - action: subagent
                resource: "Audit"
                effect: ask
            ---
            Delegate bounded, independent work.
            """);

        var loaded = await AgentProfileCatalog.LoadAsync(Path.Combine(_root, "user"), _project, includeProjectProfiles: true);

        var coordinator = Assert.Single(loaded.Profiles, profile => profile.Name == "coordinator");
        Assert.Equal("primary", coordinator.Mode);
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(coordinator, "delegate_task"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(coordinator, "delegate_task", resource: "Explorer"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(coordinator, "delegate_task", resource: "Explore Docs"));
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(coordinator, "delegate_task", resource: "Audit"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(coordinator, "delegate_task", resource: "SecretReader"));
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public async Task Rejects_invalid_resource_scoped_OpenCode_V2_grep_regex()
    {
        var directory = Path.Combine(_project, ".opencode", "agents");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "invalid-grep.md"), """
            ---
            description: Invalid grep rule.
            permissions:
              - action: grep
                resource: "["
                effect: allow
            ---
            Search safely.
            """);

        var loaded = await AgentProfileCatalog.LoadAsync(Path.Combine(_root, "user"), _project, includeProjectProfiles: true);

        Assert.DoesNotContain(loaded.Profiles, profile => profile.Name == "invalid-grep");
        Assert.Contains(loaded.Warnings, warning => warning.Contains("valid, bounded regular expressions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Imports_OpenCode_user_profiles_from_explicit_compatible_directory()
    {
        var openCodeAgents = Path.Combine(_root, "opencode", "agents");
        Directory.CreateDirectory(openCodeAgents);
        await File.WriteAllTextAsync(Path.Combine(openCodeAgents, "docs.md"), """
            ---
            description: Explain API behavior.
            mode: primary
            ---
            Read the relevant API implementation and explain it.
            """);

        var loaded = await AgentProfileCatalog.LoadAsync(Path.Combine(_root, "codev-user"), null,
            includeProjectProfiles: false, additionalUserProfileDirectories: [openCodeAgents]);

        var profile = Assert.Single(loaded.Profiles, item => item.Name == "docs");
        Assert.Equal("user-opencode", profile.Scope);
        Assert.Equal("primary", profile.Mode);
        Assert.Equal(Path.Combine(openCodeAgents, "docs.md"), profile.FilePath);
        Assert.Null(loaded.Warnings.FirstOrDefault(warning => warning.Contains("model preference", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("permission:\\n  bash:\\n    '*': { allow: true }", "permission.bash patterns must map safe command patterns")]
    [InlineData("mode: unknown", "mode must be primary, subagent, or all")]
    [InlineData("permission:\\n  unknown: allow", "does not map to a supported Codev tool")]
    public async Task Rejects_unsupported_or_ambiguous_OpenCode_agent_permissions(string fields, string expectedWarning)
    {
        var directory = Path.Combine(_project, ".opencode", "agents");
        Directory.CreateDirectory(directory);
        var contents = "---\ndescription: Fail closed.\n" + fields.Replace("\\n", "\n", StringComparison.Ordinal) + "\n---\nDo not weaken policy.";
        await File.WriteAllTextAsync(Path.Combine(directory, "unsafe.md"), contents);

        var loaded = await AgentProfileCatalog.LoadAsync(Path.Combine(_root, "user"), _project, includeProjectProfiles: true);

        Assert.DoesNotContain(loaded.Profiles, profile => profile.Name == "unsafe");
        Assert.Contains(loaded.Warnings, warning => warning.Contains(expectedWarning, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reserved_plan_profile_cannot_be_overridden_by_project_or_user_markdown()
    {
        var userDirectory = Path.Combine(_root, "user-plan");
        var projectAgentDirectory = Path.Combine(_project, ".codev", "agents");
        Directory.CreateDirectory(userDirectory);
        Directory.CreateDirectory(projectAgentDirectory);
        await File.WriteAllTextAsync(Path.Combine(userDirectory, "plan.md"), Profile("Plan"));
        await File.WriteAllTextAsync(Path.Combine(projectAgentDirectory, "plan.md"), Profile("Plan"));

        var loaded = await AgentProfileCatalog.LoadAsync(userDirectory, _project, includeProjectProfiles: true);
        var plan = Assert.Single(loaded.Profiles, profile => profile.Name == "Plan");

        Assert.Equal("built-in", plan.Scope);
        Assert.Contains(loaded.Warnings, warning => warning.Contains("reserved built-in agent profile name 'Plan'", StringComparison.Ordinal));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(plan, "write_file"));
    }

    [Fact]
    public void Built_in_profile_policies_restrict_tools_without_granting_project_permissions()
    {
        var ask = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Ask");
        var debug = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Debug");
        var code = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Code");
        var orchestrator = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Orchestrator");
        var plan = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Plan");

        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(ask, "run_command"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(ask, "read_file"));
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(ask, "mcp_server_tool"));
        Assert.Equal(AgentToolPermission.Ask, AgentProfilePolicy.PermissionFor(debug, "run_command"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(code, "write_file"));
        Assert.False(AgentProfilePolicy.IsAvailable(ask, "write_file"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(orchestrator, "read_file"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(orchestrator, "delegate_task"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(plan, "list_files"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(plan, "read_file"));
        Assert.Equal(AgentToolPermission.Allow, AgentProfilePolicy.PermissionFor(plan, "search_files"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(plan, "create_file"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(plan, "write_file"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(plan, "apply_patch"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(plan, "run_command"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(plan, "verify_command"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(plan, "mcp_server_tool"));
        Assert.Equal(AgentToolPermission.Deny, AgentProfilePolicy.PermissionFor(plan, "delegate_task"));
        Assert.False(AgentProfilePolicy.IsAvailable(code with { DefaultPermission = AgentToolPermission.Deny }, "delegate_task"));
        Assert.False(AgentProfilePolicy.RequiresOneCallApproval(AgentToolPermission.Ask, ProjectCommandPermissionMode.Auto));
        Assert.True(AgentProfilePolicy.RequiresOneCallApproval(AgentToolPermission.Ask, ProjectCommandPermissionMode.AskEveryTime));
        Assert.False(AgentProfilePolicy.RequiresOneCallApproval(AgentToolPermission.Allow, ProjectCommandPermissionMode.AskEveryTime));
    }

    [Fact]
    public void Plan_profile_removes_write_command_mcp_delegation_and_background_tools_from_both_provider_schemas()
    {
        var plan = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Plan");
        using var schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""");
        var mcpTool = new McpCodeTaskTool("mcp_github_search_abc", "github", "GitHub", "search", "Search repositories.", schema.RootElement.Clone(), null);
        var shell = ShellCommandResolver.ResolveCurrent();

        var ollama = ToolNames(CodeTaskToolSchemaFactory.CreateOllamaTools(shell, [mcpTool], plan,
            allowDelegation: true, allowBackgroundCommands: true));
        var openAi = ToolNames(CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell, [mcpTool], plan,
            allowDelegation: true, allowBackgroundCommands: true));

        foreach (var names in new[] { ollama, openAi })
        {
            Assert.Contains("list_files", names);
            Assert.Contains("read_file", names);
            Assert.Contains("search_files", names);
            Assert.DoesNotContain("create_file", names);
            Assert.DoesNotContain("write_file", names);
            Assert.DoesNotContain("apply_patch", names);
            Assert.DoesNotContain("run_command", names);
            Assert.DoesNotContain("verify_command", names);
            Assert.DoesNotContain("mcp_github_search_abc", names);
            Assert.DoesNotContain("delegate_task", names);
            Assert.DoesNotContain("start_background_command", names);
            Assert.DoesNotContain("read_background_command", names);
            Assert.DoesNotContain("stop_background_command", names);
        }
    }

    private static HashSet<string> ToolNames(object[] tools)
    {
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(tools));
        return document.RootElement.EnumerateArray()
            .Select(tool => (tool.TryGetProperty("function", out var function) ? function : tool).GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Legacy_builtin_code_selection_migrates_to_build_but_custom_code_profile_is_preserved()
    {
        var builtInCode = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Code");
        Assert.Null(AgentProfileCatalog.MigrateBuiltInCodeSelection("Code", [builtInCode]));
        Assert.Equal("Code", AgentProfileCatalog.MigrateBuiltInCodeSelection("Code", [builtInCode with { Scope = "project" }]));
        Assert.Equal("Plan", AgentProfileCatalog.MigrateBuiltInCodeSelection("Plan", [builtInCode]));
        Assert.Equal("Unavailable", AgentProfileCatalog.MigrateBuiltInCodeSelection("Unavailable", [builtInCode]));
    }

    private static string Profile(string name) => $"---\nname: {name}\ndescription: Do a coding task.\n---\nFollow the user's instructions.";

    private static bool TryCreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows()) return CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return CreateHardLinkUnix(existingPath, newPath) == 0;
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_project)) Directory.Delete(_project, recursive: true);
    }
}
