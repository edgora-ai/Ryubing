using System;
using System.IO;
using static Ryujinx.Common.Configuration.RyubingCanaryLocalPolicy;

namespace Ryujinx.Common.Configuration
{
    public static class AppDataManager
    {
        private static IDataRootFileSystem _fileSystem;
        private static string _customModsPath;
        private static string _customSdModsPath;

        public enum LaunchMode
        {
            UserProfile,
            Portable,
            Custom,
        }

        public static LaunchMode Mode { get; private set; }
        public static string BaseDirPath { get; private set; }
        public static string GamesDirPath { get; private set; }
        public static string ProfilesDirPath { get; private set; }
        public static string KeysDirPath { get; private set; }
        public static string LogsDirPath { get; private set; }

        // Compatibility for callers displaying the keys directory; never discovers ~/.switch.
        public static string KeysDirPathUser => KeysDirPath;

        public const string DefaultNandDir = "bis";
        public const string DefaultSdcardDir = "sdcard";

        public static string CustomModsPath
        {
            get => _customModsPath;
            set => _customModsPath = string.IsNullOrWhiteSpace(value) ? null : ValidatePathWithinRoot(value);
        }

        public static string CustomSdModsPath
        {
            get => _customSdModsPath;
            set => _customSdModsPath = string.IsNullOrWhiteSpace(value) ? null : ValidatePathWithinRoot(value);
        }

        public static void Initialize(string baseDirPath) => Initialize(baseDirPath, PathEnvironment.Current(), PhysicalDataRootFileSystem.Instance);

        /// <summary>
        /// Nothing is published to services until all checks succeed. A rejected explicit root cannot
        /// leave a previous/default root active, and all existing-tree checks precede the first write.
        /// </summary>
        public static void Initialize(string baseDirPath, PathEnvironment environment, IDataRootFileSystem fileSystem)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);
            BaseDirPath = GamesDirPath = ProfilesDirPath = KeysDirPath = LogsDirPath = null;
            Mode = LaunchMode.UserProfile;
            _fileSystem = null;
            _customModsPath = _customSdModsPath = null;

            DataRootSelection selection = ResolveDataRoot(baseDirPath, environment, fileSystem);
            string root = selection.Path;
            ValidateExistingTree(root, fileSystem);
            string[] directories = ["Logs", "games", "profiles", "system", DefaultNandDir, DefaultSdcardDir];
            foreach (string directory in directories)
            {
                string path = ValidateContainedPath(root, Path.Combine(root, directory), fileSystem);
                if (fileSystem.GetPathKind(path) is not (PathKind.Missing or PathKind.Directory))
                {
                    throw new IOException($"The data directory entry must be a directory: '{path}'.");
                }
            }

            string existingDirectory = root;
            while (fileSystem.GetPathKind(existingDirectory) == PathKind.Missing)
            {
                existingDirectory = Path.GetDirectoryName(existingDirectory)
                    ?? throw new IOException($"No writable parent exists for the selected data directory: '{root}'.");
            }

            fileSystem.VerifyDirectoryWritable(existingDirectory);
            fileSystem.CreateDirectory(root);
            foreach (string directory in directories)
            {
                string path = ValidateContainedPath(root, Path.Combine(root, directory), fileSystem);
                fileSystem.CreateDirectory(path);
            }
            ValidateExistingTree(root, fileSystem);
            fileSystem.VerifyDirectoryWritable(root);

            _fileSystem = fileSystem;
            Mode = selection.Mode;
            BaseDirPath = root;
            LogsDirPath = Path.Combine(root, "Logs");
            GamesDirPath = Path.Combine(root, "games");
            ProfilesDirPath = Path.Combine(root, "profiles");
            KeysDirPath = Path.Combine(root, "system");
        }

        /// <summary>
        /// Revalidates selected-root data immediately before access; never resolves links or falls back.
        /// </summary>
        public static string ValidatePathWithinRoot(string path)
        {
            if (BaseDirPath == null || _fileSystem == null)
            {
                throw new InvalidOperationException("The independent data directory has not been successfully initialized.");
            }

            return ValidateContainedPath(BaseDirPath, path, _fileSystem);
        }

        public static void ValidateSelectedDataTree() => ValidateExistingTree(ValidatePathWithinRoot(BaseDirPath), _fileSystem);

        public static string GetOrCreateDirectory(string path)
        {
            path = ValidatePathWithinRoot(path);
            if (_fileSystem.GetPathKind(path) is not (PathKind.Missing or PathKind.Directory))
            {
                throw new IOException($"The data entry must be a directory: '{path}'.");
            }
            _fileSystem.CreateDirectory(path);
            return ValidatePathWithinRoot(path);
        }

        public static string GetOrCreateLogsDir() => GetOrCreateDirectory(LogsDirPath);

        public static string GetGlobalConfigurationPath(string configurationName) =>
            ValidatePathWithinRoot(RyubingCanaryLocalPolicy.GetGlobalConfigurationPath(BaseDirPath, configurationName, _fileSystem));

        public static string GetTitleConfigurationPath(string titleId, string configurationName) =>
            ValidatePathWithinRoot(RyubingCanaryLocalPolicy.GetTitleConfigurationPath(BaseDirPath, titleId, configurationName, _fileSystem));

        public static string GetInputProfilePath(string profileType, string profileName)
        {
            if (profileType is not ("keyboard" or "controller"))
            {
                throw new ArgumentException("A profile type must be 'keyboard' or 'controller'.", nameof(profileType));
            }
            if (string.IsNullOrWhiteSpace(profileName) || profileName is "." or ".." || Path.IsPathRooted(profileName) ||
                profileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profileName.Contains('/') ||
                profileName.Contains('\\') || profileName.Contains(':') || profileName.TrimEnd(' ', '.') != profileName)
            {
                throw new ArgumentException("A profile name must be a single nonempty file-name stem.", nameof(profileName));
            }
            foreach (char character in profileName)
            {
                if (char.IsControl(character))
                {
                    throw new ArgumentException("A profile name must not contain control characters.", nameof(profileName));
                }
            }

            return ValidatePathWithinRoot(Path.Combine(ProfilesDirPath, profileType, profileName + ".json"));
        }

        // Legacy schema migration calls this method. This independent build must never move old data,
        // resolve/rewrite an old symlink, or discover a legacy macOS configuration directory.
        public static void FixMacOSConfigurationFolders()
        {
            // Intentionally disabled: initialization owns only the selected independent root.
        }

        public static string GetModsPath() => GetOrCreateDirectory(_customModsPath ?? Path.Combine(BaseDirPath, "mods"));
        public static string GetSdModsPath() => GetOrCreateDirectory(_customSdModsPath ?? Path.Combine(BaseDirPath, DefaultSdcardDir, "atmosphere"));
    }
}
