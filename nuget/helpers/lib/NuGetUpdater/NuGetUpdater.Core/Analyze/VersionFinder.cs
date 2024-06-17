using System.Collections.Immutable;

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGetUpdater.Core.Analyze;

internal static class VersionFinder
{
    public static async Task<VersionResult> GetVersionsAsync(
        DependencyInfo dependencyInfo,
        NuGetContext nugetContext,
        CancellationToken cancellationToken)
    {
        var packageId = dependencyInfo.Name;
        var versionRange = VersionRange.Parse(dependencyInfo.Version);
        var currentVersion = versionRange.MinVersion!;
        var includePrerelease = currentVersion.IsPrerelease;

        var versionFilter = CreateVersionFilter(dependencyInfo, versionRange);
        VersionResult result = new(currentVersion);

        var sourceMapping = PackageSourceMapping.GetPackageSourceMapping(nugetContext.Settings);
        var packageSources = sourceMapping.GetConfiguredPackageSources(packageId).ToHashSet();
        var sources = packageSources.Count == 0
            ? nugetContext.PackageSources
            : nugetContext.PackageSources
                .Where(p => packageSources.Contains(p.Name))
                .ToImmutableArray();

        foreach (var source in sources)
        {
            var sourceRepository = Repository.Factory.GetCoreV3(source);
            var feed = await sourceRepository.GetResourceAsync<MetadataResource>();
            if (feed is null)
            {
                // $"Failed to get MetadataResource for {source.SourceUri}"
                continue;
            }

            var existsInFeed = await feed.Exists(
                packageId,
                includePrerelease,
                includeUnlisted: false,
                nugetContext.SourceCacheContext,
                NullLogger.Instance,
                cancellationToken);
            if (!existsInFeed)
            {
                continue;
            }

            var feedVersions = (await feed.GetVersions(
                packageId,
                includePrerelease,
                includeUnlisted: false,
                nugetContext.SourceCacheContext,
                NullLogger.Instance,
                CancellationToken.None)).ToHashSet();

            if (feedVersions.Contains(currentVersion))
            {
                result.AddCurrentVersionSource(source);
            }

            result.AddRange(source, feedVersions.Where(versionFilter));
        }

        return result;
    }

    internal static Func<NuGetVersion, bool> CreateVersionFilter(DependencyInfo dependencyInfo, VersionRange versionRange)
    {
        // If we are floating to the aboslute latest version, we should not filter pre-release versions at all.
        var currentVersion = versionRange.Float?.FloatBehavior != NuGetVersionFloatBehavior.AbsoluteLatest
            ? versionRange.MinVersion
            : null;

        return version => versionRange.Satisfies(version)
            && (currentVersion is null || !currentVersion.IsPrerelease || !version.IsPrerelease || version.Version == currentVersion.Version)
            && !dependencyInfo.IgnoredVersions.Any(r => r.IsSatisfiedBy(version))
            && !dependencyInfo.Vulnerabilities.Any(v => v.IsVulnerable(version));
    }
}
