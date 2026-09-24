using System.Text.RegularExpressions;
using Xunit;

namespace SellWise.Tests;

/// <summary>
/// The plugin's constructor can't run outside the game, so this reads it instead: nothing may be used before the line
/// that creates it. (v0.9.0 set <c>Crafter.Busy</c> one line before <c>Crafter</c> existed and failed to load.)
/// </summary>
public class PluginStartupTests
{
    private static string PluginSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SellWise", "Plugin.cs"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "SellWise", "Plugin.cs"));
    }

    [Fact]
    public void ConstructorCreatesThingsBeforeUsingThem()
    {
        var source = PluginSource();
        var start = source.IndexOf("public Plugin()", StringComparison.Ordinal);
        var end = source.IndexOf("public void ToggleMain()", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "couldn't find the constructor");
        var lines = source[start..end].Split('\n');

        // Where each member is first assigned ("Crafter = new ...", "mainWindow = new ...").
        var created = new Dictionary<string, int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^\s*(\w+)\s*=\s*(?!>)");
            if (m.Success && !created.ContainsKey(m.Groups[1].Value)) created[m.Groups[1].Value] = i;
        }
        Assert.Contains("Crafter", created.Keys);

        var problems = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match use in Regex.Matches(lines[i], @"\b(\w+)\??\."))
            {
                var name = use.Groups[1].Value;
                if (created.TryGetValue(name, out var at) && i < at)
                    problems.Add($"line {i + 1}: uses {name} before it's created (line {at + 1}): {lines[i].Trim()}");
            }
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void AFailedStartCleansUp()
    {
        var source = PluginSource();
        var start = source.IndexOf("public Plugin()", StringComparison.Ordinal);
        var end = source.IndexOf("public void ToggleMain()", StringComparison.Ordinal);
        var ctor = source[start..end];
        Assert.Contains("catch", ctor);
        Assert.Contains("Shutdown();", ctor);
        Assert.Contains("throw;", ctor);
    }
}
