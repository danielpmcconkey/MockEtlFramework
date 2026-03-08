using System.Text.RegularExpressions;

namespace Lib;

/// <summary>
/// Resolves paths against the solution root directory, with support for
/// environment variable tokens like {ETL_ROOT}.
///
/// Resolution order for the solution root:
///   1. ETL_ROOT environment variable (if set)
///   2. Walk up from AppContext.BaseDirectory until a .sln file is found
/// </summary>
internal static class PathHelper
{
    private static string? _solutionRoot;

    internal static string GetSolutionRoot()
    {
        if (_solutionRoot != null) return _solutionRoot;

        var envRoot = Environment.GetEnvironmentVariable("ETL_ROOT");
        if (!string.IsNullOrEmpty(envRoot))
        {
            _solutionRoot = envRoot;
            return _solutionRoot;
        }

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
            AppContext.BaseDirectory + " and ETL_ROOT is not set.");
    }

    /// <summary>
    /// Resolves a path that may contain {ENV_VAR} tokens and/or be relative
    /// to the solution root. Tokens are expanded first, then relative paths
    /// are resolved against GetSolutionRoot().
    /// </summary>
    internal static string Resolve(string path)
    {
        path = ExpandEnvironmentTokens(path);
        return Path.IsPathRooted(path) ? path : Path.Combine(GetSolutionRoot(), path);
    }

    private static string ExpandEnvironmentTokens(string path) =>
        Regex.Replace(path, @"\{(\w+)\}", match =>
        {
            var varName = match.Groups[1].Value;
            return Environment.GetEnvironmentVariable(varName)
                ?? throw new InvalidOperationException(
                    $"Environment variable '{varName}' is not set (referenced in path '{path}').");
        });
}
