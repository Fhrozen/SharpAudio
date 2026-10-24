// wiki: testing#unit-tests
using System.Text.RegularExpressions;

namespace SharpAudio.Api.Tests;

/// <summary>
/// Enforces the wiki-header convention described in docs/LLM_WIKI.md. Line-based frontmatter
/// parsing keeps the test free of a YAML dependency.
/// </summary>
public class WikiHeaderCoverageTests
{
    private static readonly string[] MappedExtensions =
        [".cs", ".proto", ".vue", ".ts", ".js", ".sh", ".ps1"];

    private static readonly string[] ExcludedDirectories =
        ["bin", "obj", "node_modules", ".git", "out_refs", "tools", "wwwroot", "Properties", "TestData", ".docker-cache"];

    private sealed record WikiEntry(string Slug, List<string> Covers, Dictionary<string, List<string>> Sections, string Updated);

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "docs", "wiki")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static List<WikiEntry> LoadWikis(string root)
    {
        var wikis = new List<WikiEntry>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "docs", "wiki"), "*.md"))
        {
            var lines = File.ReadAllLines(file);
            if (lines.Length == 0 || lines[0].Trim() != "---")
            {
                continue;
            }

            var slug = "";
            var covers = new List<string>();
            var sections = new Dictionary<string, List<string>>();
            var updated = "";
            var inCovers = false;
            var inSections = false;

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Trim() == "---")
                {
                    break;
                }

                if (line.StartsWith("wiki: "))
                {
                    slug = line[6..].Trim();
                    continue;
                }

                if (line.StartsWith("updated: "))
                {
                    updated = line[9..].Trim();
                    continue;
                }

                if (line == "covers:")
                {
                    inCovers = true;
                    inSections = false;
                    continue;
                }

                if (line == "sections:")
                {
                    inSections = true;
                    inCovers = false;
                    continue;
                }

                if (inCovers && line.StartsWith("  - "))
                {
                    covers.Add(line[4..].Trim());
                    continue;
                }

                if (inSections && Regex.IsMatch(line, "^  [a-z0-9-]+:$"))
                {
                    var anchor = line[2..^1];
                    sections[anchor] = new List<string>();
                    inCovers = false;
                    continue;
                }

                if (inSections && line.StartsWith("    - ") && sections.Count > 0)
                {
                    var anchor = sections.Keys.Last();
                    sections[anchor].Add(line[6..].Trim());
                }
            }

            wikis.Add(new WikiEntry(slug, covers, sections, updated));
        }

        return wikis;
    }

    private static string Slugify(string heading)
    {
        var slug = Regex.Replace(heading.ToLowerInvariant(), "[^a-z0-9]+", "-");
        return slug.Trim('-');
    }

    private static bool MatchesGlob(string path, string glob)
    {
        var pattern = "^" + Regex.Escape(glob).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$";
        return Regex.IsMatch(path, pattern);
    }

    private static string CommentSyntax(string path, string target)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        if (name.StartsWith("Dockerfile") || ext is ".sh" or ".ps1")
        {
            return $"# wiki: {target}";
        }

        if (ext == ".vue" || ext == ".csproj")
        {
            return $"<!-- wiki: {target} -->";
        }

        return $"// wiki: {target}";
    }

    private static bool IsMapped(string path)
    {
        var name = Path.GetFileName(path);
        if (name == "Dockerfile" || name.StartsWith("Dockerfile."))
        {
            return true;
        }

        return MappedExtensions.Contains(Path.GetExtension(path));
    }

    private static List<string> EnumerateMappedFiles(string root)
    {
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (ExcludedDirectories.Any(d => relative.Split('/').Contains(d)))
            {
                continue;
            }

            if (IsMapped(file))
            {
                files.Add(relative);
            }
        }

        return files;
    }

    private static string? HeaderSlotContent(string absolutePath)
    {
        var first = File.ReadLines(absolutePath).FirstOrDefault() ?? "";
        var lines = File.ReadAllLines(absolutePath);
        var slot = first.StartsWith("#!") ? 1 : 0;
        return slot < lines.Length ? lines[slot] : null;
    }

    [Fact]
    public void Every_mapped_file_is_owned_by_exactly_one_wiki()
    {
        var root = FindRepoRoot();
        if (root == null)
        {
            return;
        }

        var wikis = LoadWikis(root);
        Assert.NotEmpty(wikis);

        var owner = new Dictionary<string, WikiEntry>();
        foreach (var wiki in wikis)
        {
            foreach (var files in wiki.Sections.Values)
            {
                foreach (var file in files)
                {
                    Assert.False(owner.ContainsKey(file), $"{file} is owned by two wiki files");
                    Assert.True(File.Exists(Path.Combine(root, file)), $"sections: entry does not exist on disk: {file}");
                    Assert.True(wiki.Covers.Any(g => MatchesGlob(file, g)), $"{file} is not inside {wiki.Slug}'s covers");
                    owner[file] = wiki;
                }
            }
        }

        foreach (var file in EnumerateMappedFiles(root))
        {
            Assert.True(owner.ContainsKey(file), $"{file} is not mapped by any wiki file");
        }
    }

    [Fact]
    public void Every_sections_anchor_is_an_actual_heading()
    {
        var root = FindRepoRoot();
        if (root == null)
        {
            return;
        }

        foreach (var file in Directory.GetFiles(Path.Combine(root, "docs", "wiki"), "*.md"))
        {
            var headings = File.ReadAllLines(file)
                .Where(l => l.StartsWith("#"))
                .Select(l => Slugify(l.TrimStart('#').Trim()))
                .ToHashSet();

            foreach (var wiki in LoadWikis(root).Where(w => w.Slug == Path.GetFileNameWithoutExtension(file)))
            {
                foreach (var anchor in wiki.Sections.Keys)
                {
                    Assert.Contains(anchor, headings);
                }
            }
        }
    }

    [Fact]
    public void Every_wiki_has_an_updated_date()
    {
        var root = FindRepoRoot();
        if (root == null)
        {
            return;
        }

        foreach (var wiki in LoadWikis(root))
        {
            Assert.True(Regex.IsMatch(wiki.Updated, @"^\d{4}-\d{2}-\d{2}$"), $"{wiki.Slug} has no valid updated date");
        }
    }

    [Fact]
    public void Headers_match_the_sections_mapping_in_multi_file_folders()
    {
        var root = FindRepoRoot();
        if (root == null)
        {
            return;
        }

        var wikis = LoadWikis(root);
        var owner = new Dictionary<string, (string Slug, string Anchor)>();
        foreach (var wiki in wikis)
        {
            foreach (var (anchor, files) in wiki.Sections)
            {
                foreach (var file in files)
                {
                    owner[file] = (wiki.Slug, anchor);
                }
            }
        }

        var perFolder = owner.Keys.GroupBy(f => Path.GetDirectoryName(f)?.Replace('\\', '/') ?? ".")
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (file, (slug, anchor)) in owner)
        {
            var folder = Path.GetDirectoryName(file)?.Replace('\\', '/') ?? ".";
            var isProgramCs = Path.GetFileName(file) == "Program.cs";
            if (perFolder[folder] <= 1 && !isProgramCs)
            {
                continue;
            }

            var absolute = Path.Combine(root, file);
            var content = HeaderSlotContent(absolute);
            Assert.NotNull(content);
            Assert.Equal(CommentSyntax(file, $"{slug}#{anchor}"), content);
        }
    }
}
