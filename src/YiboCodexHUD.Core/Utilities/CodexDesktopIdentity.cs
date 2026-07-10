using System.IO;

namespace YiboCodexHUD.Core.Utilities;

public static class CodexDesktopIdentity
{
    private static readonly string[] ProcessNameAliases = ["codex", "chatgpt"];
    private static readonly string[] ExecutableFileNames = ["codex.exe", "chatgpt.exe"];
    private static readonly string[] ProductDirectoryNames = ["Codex", "ChatGPT"];

    public static bool MatchesProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        return ProcessNameAliases.Any(alias => processName.Contains(alias, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryResolveExecutablePath(string? configuredPath, out string executablePath)
    {
        foreach (var candidate in EnumerateLaunchCandidates(configuredPath))
        {
            executablePath = candidate;
            return true;
        }

        executablePath = string.Empty;
        return false;
    }

    public static IReadOnlyList<string> GetLaunchCandidates(string? configuredPath)
        => EnumerateLaunchCandidates(configuredPath).ToArray();

    public static IReadOnlyList<string> GetDesktopAppUserModelIds()
        => EnumeratePackageFamilyNames()
            .Select(static packageFamilyName => $"{packageFamilyName}!App")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> GetProfileDirectoryCandidates()
        => EnumerateProfileDirectoryCandidates().ToArray();

    private static IEnumerable<string> EnumerateLaunchCandidates(string? configuredPath)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumerateConfiguredPathCandidates(configuredPath))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && yielded.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (var candidate in EnumeratePackagedExecutableCandidates())
        {
            if (yielded.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (var candidate in EnumerateBareExecutableCandidates(configuredPath))
        {
            if (yielded.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateConfiguredPathCandidates(string? configuredPath)
    {
        var expandedConfiguredPath = Environment.ExpandEnvironmentVariables(configuredPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(expandedConfiguredPath))
        {
            yield break;
        }

        if (Path.IsPathRooted(expandedConfiguredPath))
        {
            if (File.Exists(expandedConfiguredPath))
            {
                yield return expandedConfiguredPath;
            }

            var configuredDirectory = Path.GetDirectoryName(expandedConfiguredPath);
            if (!string.IsNullOrWhiteSpace(configuredDirectory))
            {
                foreach (var executableFileName in ExecutableFileNames)
                {
                    var siblingCandidate = Path.Combine(configuredDirectory, executableFileName);
                    if (File.Exists(siblingCandidate))
                    {
                        yield return siblingCandidate;
                    }
                }
            }

            yield break;
        }

        foreach (var resolvedPathCandidate in EnumeratePathResolvedCandidates(expandedConfiguredPath))
        {
            yield return resolvedPathCandidate;
        }
    }

    private static IEnumerable<string> EnumeratePackagedExecutableCandidates()
    {
        foreach (var packageDirectory in EnumerateOpenAiPackageDirectories())
        {
            foreach (var productDirectoryName in ProductDirectoryNames)
            {
                foreach (var executableFileName in ExecutableFileNames)
                {
                    var candidate = Path.Combine(
                        packageDirectory,
                        "LocalCache",
                        "Local",
                        "OpenAI",
                        productDirectoryName,
                        "bin",
                        executableFileName);

                    if (File.Exists(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateProfileDirectoryCandidates()
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumerateRoamingWebProfileCandidates())
        {
            if (Directory.Exists(candidate) && yielded.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (var packageDirectory in EnumerateOpenAiPackageDirectories())
        {
            foreach (var productDirectoryName in ProductDirectoryNames)
            {
                var candidate = Path.Combine(
                    packageDirectory,
                    "LocalCache",
                    "Roaming",
                    productDirectoryName);

                if (Directory.Exists(candidate) && yielded.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateRoamingWebProfileCandidates()
    {
        var roamingRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(roamingRoot))
        {
            yield break;
        }

        foreach (var productDirectoryName in ProductDirectoryNames)
        {
            yield return Path.Combine(roamingRoot, productDirectoryName, "web", productDirectoryName);
        }
    }

    private static IEnumerable<string> EnumerateBareExecutableCandidates(string? configuredPath)
    {
        var expandedConfiguredPath = Environment.ExpandEnvironmentVariables(configuredPath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(expandedConfiguredPath) && !Path.IsPathRooted(expandedConfiguredPath))
        {
            yield return expandedConfiguredPath;
        }

        foreach (var executableFileName in ExecutableFileNames)
        {
            yield return executableFileName;
        }
    }

    private static IEnumerable<string> EnumeratePathResolvedCandidates(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            yield break;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (var pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(pathEntry))
            {
                continue;
            }

            string combinedPath;
            try
            {
                combinedPath = Path.Combine(pathEntry, executableName);
            }
            catch
            {
                continue;
            }

            if (File.Exists(combinedPath))
            {
                yield return combinedPath;
            }
        }
    }

    private static IEnumerable<string> EnumerateOpenAiPackageDirectories()
    {
        var packagesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages");

        if (!Directory.Exists(packagesRoot))
        {
            yield break;
        }

        IEnumerable<string> packageDirectories;
        try
        {
            packageDirectories = Directory.EnumerateDirectories(packagesRoot, "OpenAI*");
        }
        catch
        {
            yield break;
        }

        foreach (var packageDirectory in packageDirectories)
        {
            yield return packageDirectory;
        }
    }

    private static IEnumerable<string> EnumeratePackageFamilyNames()
    {
        foreach (var packageDirectory in EnumerateOpenAiPackageDirectories())
        {
            var packageFamilyName = Path.GetFileName(packageDirectory);
            if (!string.IsNullOrWhiteSpace(packageFamilyName))
            {
                yield return packageFamilyName;
            }
        }
    }
}
