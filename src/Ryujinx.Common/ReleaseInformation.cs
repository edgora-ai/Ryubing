using System;
using System.Reflection;

namespace Ryujinx.Common
{
    // Pinned independent local build; do not replace these values with upstream CI placeholders.
    public static class ReleaseInformation
    {
        private const string CanaryChannel = "canary";
        private const string ReleaseChannel = "release";

        private const string BuildVersion = "1.3.351";
        private const string BuildGitHash = "475615f0431b4995b03c930c5089d20f6f3570e0";
        private const string ReleaseChannelName = CanaryChannel;
        private const string ConfigFileName = "Config.json";

        public const string LocalBuildMarker = "ryubing-canary-local+475615f.local1.rumblefix1";
        public static string SourceRevision => BuildGitHash;

        public static string ConfigName => !ConfigFileName.StartsWith("%%") ? ConfigFileName : "Config.json";

        public static bool IsValid =>
            !BuildGitHash.StartsWith("%%") &&
            !ReleaseChannelName.StartsWith("%%") &&
            !ConfigFileName.StartsWith("%%");

        public static bool IsCanaryBuild => IsValid && ReleaseChannelName.Equals(CanaryChannel);

        public static bool IsReleaseBuild => IsValid && ReleaseChannelName.Equals(ReleaseChannel);

        public static string Version => IsValid ? BuildVersion : Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        public static string GetChangelogUrl(Version currentVersion, Version newVersion) =>
            IsCanaryBuild
                ? $"https://git.ryujinx.app/projects/Ryubing/compare/Canary-{currentVersion}...Canary-{newVersion}"
                : $"https://git.ryujinx.app/projects/Ryubing/releases/tag/{newVersion}";
    }


}
