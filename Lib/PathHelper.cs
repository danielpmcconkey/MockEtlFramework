namespace Lib;

/// <summary>
/// Resolves relative output paths against the solution root directory.
/// Walks up from AppContext.BaseDirectory (bin/Debug/net8.0/) until it finds
/// the .sln file, then resolves relative paths from there.
/// </summary>
internal static class PathHelper
{
    private static string? _solutionRoot;

    internal static string GetSolutionRoot()
    {
        if (_solutionRoot != null) return _solutionRoot;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
            {
                _solutionRoot = dir.FullName;
                return _solutionRoot;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate solution root. No .sln file found in any ancestor of " +
            AppContext.BaseDirectory);
    }

    /// <summary>
    /// Resolves a path that may be relative (to the solution root) or absolute.
    /// </summary>
    internal static string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(GetSolutionRoot(), path);
}
