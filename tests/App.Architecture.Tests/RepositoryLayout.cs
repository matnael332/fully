using System.Xml.Linq;

namespace WorkContinuity.Architecture.Tests;

internal static class RepositoryLayout
{
    internal static DirectoryInfo Root { get; } = Locate();

    internal static IReadOnlyList<FileInfo> ProductionProjectFiles { get; } =
        new DirectoryInfo(Path.Combine(Root.FullName, "src"))
            .EnumerateFiles("*.csproj", SearchOption.AllDirectories)
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();

    internal static IReadOnlyList<FileInfo> CoreSourceFiles { get; } =
        new DirectoryInfo(Path.Combine(Root.FullName, "src", "App.Core"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .ToList();

    internal static string RelativePath(FileInfo file) =>
        Path.GetRelativePath(Root.FullName, file.FullName).Replace('\\', '/');

    internal static string ProjectName(FileInfo projectFile) =>
        Path.GetFileNameWithoutExtension(projectFile.Name);

    /// <summary>Reads the referenced project names declared by a project file.</summary>
    internal static IReadOnlySet<string> ProjectReferences(FileInfo projectFile) =>
        XDocument.Load(projectFile.FullName)
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .Where(include => include.Length > 0)
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Reads the raw Include values of referenced projects, preserving their paths.</summary>
    internal static IReadOnlyList<string> RawProjectReferencePaths(FileInfo projectFile) =>
        XDocument.Load(projectFile.FullName)
            .Descendants("ProjectReference")
            .Select(element => ((string?)element.Attribute("Include") ?? string.Empty).Replace('\\', '/'))
            .Where(include => include.Length > 0)
            .ToList();

    /// <summary>Reads the package names referenced by a project file.</summary>
    internal static IReadOnlySet<string> PackageReferences(FileInfo projectFile) =>
        XDocument.Load(projectFile.FullName)
            .Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .Where(include => include.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsBuildOutput(FileInfo file)
    {
        var path = file.FullName.Replace('\\', '/');

        return path.Contains("/bin/", StringComparison.Ordinal)
            || path.Contains("/obj/", StringComparison.Ordinal);
    }

    private static DirectoryInfo Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("WorkContinuityEngine.sln").Any())
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
