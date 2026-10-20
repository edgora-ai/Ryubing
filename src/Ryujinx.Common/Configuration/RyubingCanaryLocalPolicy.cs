using System;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.Common.Configuration
{
    /// <summary>
    /// Identity and fail-closed storage policy for this independent build. No legacy data is discovered or migrated.
    /// </summary>
    public static class RyubingCanaryLocalPolicy
    {
        public const string DisplayName = "Ryubing Canary Local";
        public const string BundleIdentifier = "local.ryubing.RyubingCanaryLocal";
        public const string DataDirectoryName = "Ryubing-Canary-Local";
        public const string PortableDirectoryName = "portable-ryubing-canary-local";

        public sealed record PathEnvironment(
            string UserProfilePath,
            string ApplicationDataPath,
            string ApplicationBaseDirectory,
            string WorkingDirectory,
            bool IsMacOS,
            bool IsWindows)
        {
            public static PathEnvironment Current() => new(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppContext.BaseDirectory,
                Environment.CurrentDirectory,
                OperatingSystem.IsMacOS(),
                OperatingSystem.IsWindows());
        }

        public sealed record DataRootSelection(string Path, AppDataManager.LaunchMode Mode);

        public enum PathKind
        {
            Missing,
            File,
            Directory,
            Link,
        }

        /// <summary>
        /// GetPathKind inspects the entry itself without following a link. Enumeration returns immediate
        /// entry paths only; root validation never needs configuration, key, account or save contents.
        /// </summary>
        public interface IDataRootFileSystem
        {
            PathKind GetPathKind(string path);
            IEnumerable<string> EnumerateEntries(string directory);
            void VerifyDirectoryWritable(string directory);
            void CreateDirectory(string directory);
        }

        public sealed class PhysicalDataRootFileSystem : IDataRootFileSystem
        {
            public static PhysicalDataRootFileSystem Instance { get; } = new();

            public PathKind GetPathKind(string path)
            {
                try
                {
                    // File.GetAttributes reports the link/reparse entry, including a dangling link.
                    // Callers check parent components first, so this does not traverse a linked parent.
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return PathKind.Link;
                    }

                    return (attributes & FileAttributes.Directory) != 0 ? PathKind.Directory : PathKind.File;
                }
                catch (FileNotFoundException)
                {
                    return PathKind.Missing;
                }
                catch (DirectoryNotFoundException)
                {
                    return PathKind.Missing;
                }
            }

            public IEnumerable<string> EnumerateEntries(string directory) => Directory.EnumerateFileSystemEntries(directory);

            public void VerifyDirectoryWritable(string directory)
            {
                ValidatePathComponents(directory, this);
                if (GetPathKind(directory) != PathKind.Directory)
                {
                    throw new IOException($"The write-probe parent must be a directory: '{directory}'.");
                }

                string probePath = System.IO.Path.Combine(directory, $".ryubing-canary-local-write-{Guid.NewGuid():N}");
                FileStreamOptions options = new()
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.WriteThrough | FileOptions.DeleteOnClose,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using FileStream probe = new(probePath, options);
                probe.WriteByte(0);
                probe.Flush(flushToDisk: true);
            }

            public void CreateDirectory(string directory)
            {
                ValidatePathComponents(directory, this);
                PathKind kind = GetPathKind(directory);
                if (kind == PathKind.Directory)
                {
                    return; // Existing permissions belong to the user and are never rewritten.
                }
                if (kind != PathKind.Missing)
                {
                    throw new IOException($"The data entry must be a directory: '{directory}'.");
                }

                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(directory);
                }
                else
                {
                    // The Unix-mode overload applies only to the final component. Create every
                    // missing ancestor separately so intermediate directories are private too.
                    Stack<string> missing = new();
                    string current = Normalize(directory);
                    while (GetPathKind(current) == PathKind.Missing)
                    {
                        missing.Push(current);
                        current = System.IO.Path.GetDirectoryName(current)
                            ?? throw new IOException($"No existing parent exists for '{directory}'.");
                    }
                    ValidatePathComponents(current, this);
                    while (missing.TryPop(out string child))
                    {
                        ValidatePathComponents(child, this);
                        Directory.CreateDirectory(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                }

                ValidatePathComponents(directory, this);
            }
        }

        /// <summary>
        /// Resolves a root without creating it or opening user data. An explicit root is checked first,
        /// must already exist, and never falls back to portable or default storage after an error.
        /// </summary>
        public static DataRootSelection ResolveDataRoot(string requestedRoot, PathEnvironment environment,
            IDataRootFileSystem fileSystem = null)
        {
            ArgumentNullException.ThrowIfNull(environment);
            fileSystem ??= PhysicalDataRootFileSystem.Instance;

            string home = RequireAbsolute(environment.UserProfilePath, nameof(environment.UserProfilePath));
            string applicationBase = RequireAbsolute(environment.ApplicationBaseDirectory, nameof(environment.ApplicationBaseDirectory));
            string applicationData = environment.IsMacOS
                ? System.IO.Path.Combine(home, "Library", "Application Support")
                : string.IsNullOrWhiteSpace(environment.ApplicationDataPath)
                    ? System.IO.Path.Combine(home, ".config")
                    : RequireAbsolute(environment.ApplicationDataPath, nameof(environment.ApplicationDataPath));

            string selected;
            AppDataManager.LaunchMode mode;
            if (requestedRoot != null)
            {
                if (string.IsNullOrWhiteSpace(requestedRoot))
                {
                    throw new ArgumentException("The explicit data directory must not be empty.", nameof(requestedRoot));
                }

                selected = Normalize(System.IO.Path.GetFullPath(requestedRoot,
                    RequireAbsolute(environment.WorkingDirectory, nameof(environment.WorkingDirectory))));
                mode = AppDataManager.LaunchMode.Custom;
            }
            else
            {
                selected = System.IO.Path.Combine(applicationData, DataDirectoryName);
                mode = AppDataManager.LaunchMode.UserProfile;

                foreach (string portable in GetPortableCandidates(applicationBase, environment.IsMacOS))
                {
                    RejectProtectedOverlap(portable, environment, applicationData, applicationBase);
                    ValidatePathComponents(portable, fileSystem);
                    PathKind kind = fileSystem.GetPathKind(portable);
                    if (kind == PathKind.Directory)
                    {
                        selected = portable;
                        mode = AppDataManager.LaunchMode.Portable;
                        break;
                    }
                    if (kind != PathKind.Missing)
                    {
                        throw new IOException($"The dedicated portable data directory is not a directory: '{portable}'.");
                    }
                }
            }

            selected = Normalize(selected);
            // Protection is lexical and precedes any inspection of the selected location or its contents.
            RejectProtectedOverlap(selected, environment, applicationData, applicationBase);
            ValidatePathComponents(selected, fileSystem);
            PathKind selectedKind = fileSystem.GetPathKind(selected);
            if (selectedKind != PathKind.Directory && (requestedRoot != null || selectedKind != PathKind.Missing))
            {
                throw new IOException($"The selected data directory must be an existing directory: '{selected}'.");
            }

            return new DataRootSelection(selected, mode);
        }

        /// <summary>
        /// Validates existing descendants before the first writer or service starts. Child links are
        /// rejected without enumerating their targets, including profiles, caches, NAND and SD storage.
        /// </summary>
        public static void ValidateExistingTree(string root, IDataRootFileSystem fileSystem = null)
        {
            fileSystem ??= PhysicalDataRootFileSystem.Instance;
            root = Normalize(root);
            ValidatePathComponents(root, fileSystem);
            PathKind rootKind = fileSystem.GetPathKind(root);
            if (rootKind == PathKind.Missing)
            {
                return;
            }
            if (rootKind != PathKind.Directory)
            {
                throw new IOException($"The data root must be a directory: '{root}'.");
            }

            Stack<string> pending = new();
            pending.Push(root);
            while (pending.TryPop(out string directory))
            {
                // Revalidate even after an earlier traversal pass, before enumerating this directory.
                ValidatePathComponents(directory, fileSystem);
                foreach (string entry in fileSystem.EnumerateEntries(directory))
                {
                    string path = ValidateContainedPath(root, entry, fileSystem);
                    if (fileSystem.GetPathKind(path) == PathKind.Directory)
                    {
                        pending.Push(path);
                    }
                }
            }
        }

        /// <summary>
        /// Validates a root-relative reader/writer path. An absolute path is allowed only inside root.
        /// Existing symbolic links or reparse points at any component, including a file leaf, are rejected.
        /// </summary>
        public static string ValidateContainedPath(string root, string path, IDataRootFileSystem fileSystem = null)
        {
            fileSystem ??= PhysicalDataRootFileSystem.Instance;
            root = Normalize(root);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            path = Normalize(System.IO.Path.IsPathFullyQualified(path) ? path : System.IO.Path.Combine(root, path));
            if (!IsWithin(root, path, PathComparison))
            {
                throw new IOException($"Path must remain within the selected {DisplayName} data directory: '{path}'.");
            }

            ValidatePathComponents(root, fileSystem);
            ValidatePathComponents(path, fileSystem);
            return path;
        }

        public static string GetGlobalConfigurationPath(string root, string configurationName,
            IDataRootFileSystem fileSystem = null)
        {
            ValidateFileName(configurationName, nameof(configurationName));
            return ValidateContainedPath(root, System.IO.Path.Combine(root, configurationName), fileSystem);
        }

        public static string GetTitleConfigurationPath(string root, string titleId, string configurationName,
            IDataRootFileSystem fileSystem = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(titleId);
            if (titleId.Length != 16 || !ulong.TryParse(titleId, System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                throw new ArgumentException("A title ID must contain exactly 16 hexadecimal digits.", nameof(titleId));
            }

            ValidateFileName(configurationName, nameof(configurationName));
            return ValidateContainedPath(root, System.IO.Path.Combine(root, "games", titleId, configurationName), fileSystem);
        }

        /// <summary>
        /// Both entry points accept exactly one separated root option. Reject forms their different
        /// parsers could interpret differently, rather than silently ignoring a root or choosing one.
        /// </summary>
        public static void ValidateRootArguments(string[] arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            bool rootSeen = false;
            bool endOfOptions = false;
            for (int index = 0; index < arguments.Length; index++)
            {
                string argument = arguments[index] ?? throw new ArgumentException("Arguments must not contain null entries.", nameof(arguments));
                if (argument == "--")
                {
                    endOfOptions = true;
                }
                else if (argument is "-r" or "--root-data-dir")
                {
                    if (endOfOptions || rootSeen)
                    {
                        throw new ArgumentException("Specify one unambiguous data-directory option before '--'.", nameof(arguments));
                    }

                    rootSeen = true;
                    if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]) || arguments[index].StartsWith('-'))
                    {
                        throw new ArgumentException($"Option '{argument}' requires a data directory.", nameof(arguments));
                    }
                }
                else if (argument.StartsWith("--root-data-dir", StringComparison.Ordinal) ||
                    (argument != "-rdct" && argument.StartsWith("-r", StringComparison.Ordinal)))
                {
                    // -rdct is the existing GUI RenderDoc capture-title-format option, not an attached root.
                    throw new ArgumentException("Use '--root-data-dir <directory>' or '-r <directory>', not an equals or attached-value form.", nameof(arguments));
                }
            }
        }

        public static string Normalize(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }

        public static bool IsWithin(string root, string path, StringComparison comparison)
        {
            root = Normalize(root);
            path = Normalize(path);
            return string.Equals(root, path, comparison) || path.StartsWith(
                System.IO.Path.EndsInDirectorySeparator(root) ? root : root + System.IO.Path.DirectorySeparatorChar, comparison);
        }

        private static StringComparison PathComparison => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static string RequireAbsolute(string path, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("An absolute path is required for root selection.", parameterName);
            }

            return Normalize(path);
        }

        private static void ValidateFileName(string name, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                name.Contains('/') || name.Contains('\\') || name.Contains(':'))
            {
                throw new ArgumentException("A single file name is required.", parameterName);
            }
        }

        private static IEnumerable<string> GetPortableCandidates(string applicationBase, bool isMacOS)
        {
            yield return System.IO.Path.Combine(applicationBase, PortableDirectoryName);
            string bundle = Normalize(System.IO.Path.Combine(applicationBase, "..", ".."));
            if (isMacOS && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                yield return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(bundle), PortableDirectoryName);
            }
        }

        private static void RejectProtectedOverlap(string selected, PathEnvironment environment, string applicationData, string applicationBase)
        {
            string home = Normalize(environment.UserProfilePath);
            List<string> protectedRoots = [System.IO.Path.Combine(home, ".switch")];
            string[] legacyNames = ["Ryujinx", "Ryubing", "Nextendo", "Nextendo-Local"];
            foreach (string legacyName in legacyNames)
            {
                protectedRoots.Add(System.IO.Path.Combine(applicationData, legacyName));
                protectedRoots.Add(System.IO.Path.Combine(home, ".config", legacyName));
                protectedRoots.Add(System.IO.Path.Combine(home, "Library", "Application Support", legacyName));
                protectedRoots.Add(System.IO.Path.Combine(home, "Library", "Logs", legacyName));
            }
            protectedRoots.Add(System.IO.Path.Combine(home, "Library", "Logs", "Nextendo Local"));

            string[] legacyBundles = ["Ryujinx.app", "Ryubing.app", "Nextendo.app", "Nextendo Local.app", "Nextendo-Local.app"];
            string[] installationParents = [System.IO.Path.Combine(System.IO.Path.GetPathRoot(home), "Applications"),
                System.IO.Path.Combine(home, "Applications"), System.IO.Path.Combine(home, "Downloads")];
            foreach (string parent in installationParents)
            {
                foreach (string bundleName in legacyBundles)
                {
                    protectedRoots.Add(System.IO.Path.Combine(parent, bundleName));
                }
            }

            List<string> portableParents = [applicationBase, home, RequireAbsolute(environment.WorkingDirectory, nameof(environment.WorkingDirectory))];
            portableParents.AddRange(installationParents);
            string bundle = Normalize(System.IO.Path.Combine(applicationBase, "..", ".."));
            if (environment.IsMacOS && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                portableParents.Add(System.IO.Path.GetDirectoryName(bundle));
            }
            foreach (string parent in portableParents)
            {
                protectedRoots.Add(System.IO.Path.Combine(parent, "portable"));
                protectedRoots.Add(System.IO.Path.Combine(parent, "portable-nextendo-local"));
            }

            StringComparison comparison = environment.IsMacOS || environment.IsWindows
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            // Also protect an old bundle at a nonstandard installation location without inspecting it.
            for (string ancestor = Normalize(selected); ancestor != null; ancestor = System.IO.Path.GetDirectoryName(ancestor))
            {
                foreach (string bundleName in legacyBundles)
                {
                    if (string.Equals(System.IO.Path.GetFileName(ancestor), bundleName, comparison))
                    {
                        protectedRoots.Add(ancestor);
                    }
                }
            }
            foreach (string protectedRoot in protectedRoots)
            {
                if (IsWithin(protectedRoot, selected, comparison) || IsWithin(selected, protectedRoot, comparison))
                {
                    throw new IOException($"The selected data directory overlaps a protected legacy location: '{selected}'. Choose an independent directory.");
                }
            }
        }

        private static void ValidatePathComponents(string path, IDataRootFileSystem fileSystem)
        {
            path = Normalize(path);
            string current = System.IO.Path.GetPathRoot(path);
            string remainder = path[current.Length..];
            string[] components = remainder.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index <= components.Length; index++)
            {
                if (index > 0)
                {
                    current = System.IO.Path.Combine(current, components[index - 1]);
                }
                PathKind kind = fileSystem.GetPathKind(current);
                if (kind == PathKind.Link)
                {
                    throw new IOException($"Symbolic links and reparse points are not permitted in the selected data path: '{current}'.");
                }
                if (kind == PathKind.File && index < components.Length)
                {
                    throw new IOException($"A data path component is not a directory: '{current}'.");
                }
                if (kind == PathKind.Missing)
                {
                    break;
                }
            }
        }
    }
}
