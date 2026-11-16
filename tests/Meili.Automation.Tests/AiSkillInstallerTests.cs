using Meili.Automation;
namespace Meili.Automation.Tests;
public sealed class AiSkillInstallerTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"meili skill ' "+Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("codex",".agents")]
    [InlineData("claude-code",".claude")]
    public void Installs_embedded_skill_idempotently_and_never_replaces_different_content(string client,string folder)
    {
        var first=WireJson.Element(AiSkillInstaller.Install(client,_root));Assert.Equal("installed",first.GetProperty("status").GetString());
        var target=Path.Combine(_root,folder,"skills",AiSkillInstaller.SkillName,"SKILL.md");Assert.Equal(AiSkillInstaller.Content,File.ReadAllText(target));
        Assert.Contains("name: easysellput-local",AiSkillInstaller.Content);Assert.Contains("--allow-writes",AiSkillInstaller.Content);
        Assert.Equal("already_installed",WireJson.Element(AiSkillInstaller.Install(client,_root)).GetProperty("status").GetString());
        File.WriteAllText(target,"user-owned");Assert.Throws<SkillInstallConflictException>(()=>AiSkillInstaller.Install(client,_root));Assert.Equal("user-owned",File.ReadAllText(target));
    }
    [Fact]
    public void Embedded_skill_covers_every_catalog_tool_with_matching_access()
    {
        var content=AiSkillInstaller.Content;
        var section=content.Split("<!-- mcp-tool-guide:start -->")[1].Split("<!-- mcp-tool-guide:end -->")[0];
        var rows=section.Split('\n').Where(line=>line.StartsWith("| `",StringComparison.Ordinal)).ToArray();
        Assert.Equal(OperationCatalog.All.Count,rows.Length);
        foreach(var operation in OperationCatalog.All)
        {
            var prefix=$"| `{operation.Name}` | {(operation.Writes ? "write" : "read")} | ";
            Assert.Single(rows,line=>line.StartsWith(prefix,StringComparison.Ordinal));
        }
    }
    [Fact]
    public void Unedited_previous_skill_is_backed_up_and_upgraded_but_custom_edits_are_preserved()
    {
        using var stream=typeof(AiSkillInstallerTests).Assembly.GetManifestResourceStream("EasySellPut.Tests.PreviousSkill")!;
        using var reader=new StreamReader(stream);var previous=reader.ReadToEnd();
        var target=Path.Combine(_root,".agents","skills",AiSkillInstaller.SkillName,"SKILL.md");Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllText(target,previous);
        Assert.Equal("update",WireJson.Element(AiSkillInstaller.Check("codex",_root)).GetProperty("status").GetString());
        var result=WireJson.Element(AiSkillInstaller.Install("codex",_root));Assert.Equal("updated",result.GetProperty("status").GetString());
        Assert.Equal(previous,File.ReadAllText(result.GetProperty("backup").GetString()!));Assert.Equal(AiSkillInstaller.Content,File.ReadAllText(target));
        File.WriteAllText(target,"custom change");Assert.Throws<SkillInstallConflictException>(()=>AiSkillInstaller.Check("codex",_root));
        Assert.Equal("custom change",File.ReadAllText(target));
    }
    [Fact]
    public void Check_does_not_create_directories_and_workflow_references_match_the_compiled_catalog()
    {
        AiSkillInstaller.Check("codex",_root);Assert.False(Directory.Exists(_root));
        var names=OperationCatalog.All.Select(o=>o.Name).ToHashSet();
        foreach(var workflow in AiWorkflowGuide.All)Assert.All(workflow.Tools,tool=>Assert.Contains(tool,names));
        var setup=new McpConnectionSetup("/Applications/EasySellPut.app/Contents/Resources/automation/easysellput",McpConnectionSetup.ProfileDataDirectory,false);
        Assert.EndsWith(" connect codex",setup.CodexSetupCommand);Assert.DoesNotContain("install-skill",setup.CodexSetupCommand);
    }
    [Fact]
    public void Unknown_client_cannot_choose_a_destination()
    {
        Assert.Throws<ArgumentException>(()=>AiSkillInstaller.Install("../other",_root));Assert.False(Directory.Exists(_root));
    }
    private sealed class UnixLinkFactAttribute : FactAttribute
    {
        public UnixLinkFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Symlink creation privileges depend on Windows host policy; content-conflict tests run on all OSes."; }
    }
    [UnixLinkFact]
    public void Existing_skill_link_is_not_followed()
    {
        var destination=Path.Combine(_root,".agents","skills",AiSkillInstaller.SkillName);Directory.CreateDirectory(destination);
        var outside=Path.Combine(_root,"owned");File.WriteAllText(outside,"untouched");File.CreateSymbolicLink(Path.Combine(destination,"SKILL.md"),outside);
        Assert.Throws<SkillInstallConflictException>(()=>AiSkillInstaller.Install("codex",_root));Assert.Equal("untouched",File.ReadAllText(outside));
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
