using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.IO;
using System.Text.Json.Nodes;

namespace FloppyUtils.Xrepo;

public class XrepoPrePackage : Task
{
    [Required]
    public string ProjectName { get; set; } = null!;

    [Required]
    public string ProjectNameNoX { get; set; } = null!;

    [Required]
    public string ProjectDir { get; set; } = null!;

    public override bool Execute()
    {
        NormalizePaths();

        UpdateMetadataJson();

        return true;
    }

    private void NormalizePaths()
    {
        ProjectDir = NormalizePath(ProjectDir);
    }

    private string NormalizePath(string path) =>
        path
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    private void UpdateMetadataJson()
    {
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectDir, $"{ProjectNameNoX}.json")))!;

        metadata["Name"] = metadata["Name"]!.GetValue<string>() + "X";
        metadata["InternalName"] = metadata["InternalName"]!.GetValue<string>() + "X";
        metadata["Punchline"] = "[Xrepo] " + metadata["Punchline"]!.GetValue<string>();
        metadata["Description"] = "[Xrepo] " + metadata["Description"]!.GetValue<string>();

        var tags = metadata["Tags"]!.AsArray();
        tags.Add($"orig:{ProjectNameNoX}");

        File.WriteAllText(Path.Combine(ProjectDir, $"{ProjectName}.json"), metadata.ToJsonString(new() { WriteIndented = true }));
    }
}
