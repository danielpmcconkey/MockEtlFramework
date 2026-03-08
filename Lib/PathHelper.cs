using System.Text.RegularExpressions;

namespace Lib;

/// <summary>
/// Resolves paths against the solution root directory, with support for
/// {TOKEN} expansion. All known tokens are sourced from AppConfig; no
/// direct Environment reads.
///
/// Resolution order for the solution root:
///   1. AppConfig.Paths.EtlRoot (if set)
///   2. Walk up from AppContext.BaseDirectory until a .sln file is found
/// </summary>
public static class PathHelper
{
    private static string? _solutionRoot;
    private static Dictionary<string, string> _tokenMap = new();

    public static void Initialize(AppConfig config)
    {
        _solutionRoot = null; // reset on re-init
        _tokenMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(config.Paths.EtlRoot))
            _tokenMap["ETL_ROOT"] = config.Paths.EtlRoot;

        if (!string.IsNullOrEmpty(config.Paths.EtlReOutput))
            _tokenMap["ETL_RE_OUTPUT"] = config.Paths.EtlReOutput;
    }

    internal static string GetSolutionRoot()
    {
        if (_solutionRoot != null) return _solutionRoot;

        if (_tokenMap.TryGetValue("ETL_ROOT", out var etlRoot) && !string.IsNullOrEmpty(etlRoot))
        {
            _solutionRoot = etlRoot;
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
    /// Resolves a path that may contain {TOKEN} patterns and/or be relative
    /// to the solution root. Tokens are expanded first, then relative paths
    /// are resolved against GetSolutionRoot().
    /// </summary>
    internal static string Resolve(string path)
    {
        path = ExpandTokens(path);
        return Path.IsPathRooted(path) ? path : Path.Combine(GetSolutionRoot(), path);
    }

    private static string ExpandTokens(string path) =>
        Regex.Replace(path, @"\{(\w+)\}", match =>
        {
            var tokenName = match.Groups[1].Value;
            return _tokenMap.TryGetValue(tokenName, out var value)
                ? value
                : throw new InvalidOperationException(
                    $"Unknown path token '{{{tokenName}}}' (referenced in path '{path}'). " +
                    $"Known tokens: {string.Join(", ", _tokenMap.Keys)}");
        });
}
