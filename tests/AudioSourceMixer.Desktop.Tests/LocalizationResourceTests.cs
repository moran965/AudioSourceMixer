using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AudioSourceMixer.Desktop.Localization;

namespace AudioSourceMixer.Desktop.Tests;

public sealed class LocalizationResourceTests
{
    [Fact]
    public void DesktopResourcesAreCompleteAndEveryReferencedKeyExists()
    {
        var resources = new LocalizedResourceManager();
        var keys = resources.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.True(keys.Count >= 200);

        foreach (var language in LocalizationService.SupportedLanguages)
        {
            foreach (var key in keys)
            {
                var value = resources.GetString(key, language);
                Assert.False(string.IsNullOrWhiteSpace(value), $"{language}:{key} is empty.");
                Assert.DoesNotContain("[[", value, StringComparison.Ordinal);
                Assert.DoesNotContain("{{", value, StringComparison.Ordinal);
                Assert.NotEqual(key, value);
            }
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in SourceFiles("*.xaml", "SourceXaml"))
        {
            var source = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(source, @"\{l:Loc\s+Key=([A-Za-z0-9_.-]+)"))
                referenced.Add(match.Groups[1].Value);
        }
        foreach (var path in SourceFiles("*.cs", "SourceCode"))
        {
            var source = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(source, "(?:_localization|LocalizationService\\.Current)\\[\\\"([^\\\"]+)\\\"\\]"))
                referenced.Add(match.Groups[1].Value);
        }

        Assert.NotEmpty(referenced);
        Assert.Empty(referenced.Except(keys, StringComparer.Ordinal));
    }

    [Fact]
    public void XamlVisibleTextIsLocalizedOrLanguageNeutral()
    {
        var visibleAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Text", "Content", "Header", "Title", "ToolTip", "AutomationProperties.Name", "AutomationProperties.HelpText"
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            " · ", "Microsoft Edge", "Google Chrome", "1", "2", "3", "4", "5", "6"
        };

        foreach (var path in SourceFiles("*.xaml", "SourceXaml"))
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var attribute in document.Descendants().Attributes())
            {
                var name = attribute.Name.LocalName;
                if (!visibleAttributes.Contains(name)) continue;
                if (attribute.Value.StartsWith("{", StringComparison.Ordinal) || allowed.Contains(attribute.Value)) continue;
                Assert.Fail($"Hard-coded visible XAML text in {Path.GetFileName(path)}: {name}=\"{attribute.Value}\"");
            }
        }
    }

    [Fact]
    public void ProductionCSharpContainsNoScatteredChineseUiText()
    {
        foreach (var path in SourceFiles("*.cs", "SourceCode"))
        {
            if (path.EndsWith("UiSmokeVerifier.cs", StringComparison.OrdinalIgnoreCase)) continue;
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                if (!Regex.IsMatch(lines[index], @"[\p{IsCJKUnifiedIdeographs}]")) continue;
                Assert.Contains("简体中文", lines[index], StringComparison.Ordinal);
            }
        }
    }

    private static IEnumerable<string> SourceFiles(string pattern, string directory)
    {
        var root = Path.Combine(AppContext.BaseDirectory, directory);
        return Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
    }
}
