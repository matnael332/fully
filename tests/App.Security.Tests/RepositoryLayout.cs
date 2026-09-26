namespace WorkContinuity.Security.Tests;

internal static class RepositoryLayout
{
    internal static DirectoryInfo Root { get; } = Locate();

    internal static IReadOnlyList<FileInfo> ProductionSourceFiles { get; } =
        new DirectoryInfo(Path.Combine(Root.FullName, "src"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .ToList();

    internal static string RelativePath(FileInfo file) =>
        Path.GetRelativePath(Root.FullName, file.FullName).Replace('\\', '/');

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
