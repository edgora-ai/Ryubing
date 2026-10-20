using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using ShellLink;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Ryujinx.Ava.Utilities
{
    public static class ShortcutHelper
    {
        private const string RunnerName = "runner.sh";
        private const string IconName = "icon.png";

        /// <summary>Builds process tokens, not shell source. Always pins the selected absolute data root.</summary>
        public static IReadOnlyList<string> BuildLaunchArguments(string resolvedRoot, string applicationFilePath,
            string applicationId, IEnumerable<string> additionalArguments = null)
        {
            RequireAbsolutePath(resolvedRoot, nameof(resolvedRoot));
            RequireAbsolutePath(applicationFilePath, nameof(applicationFilePath));
            ValidateApplicationId(applicationId);
            List<string> arguments = ["--root-data-dir", Path.GetFullPath(resolvedRoot)];
            foreach (string argument in additionalArguments ?? [])
            {
                ArgumentNullException.ThrowIfNull(argument);
                if (argument.IndexOf('\0') >= 0 || argument == "--" ||
                    argument.StartsWith("-r", StringComparison.OrdinalIgnoreCase) ||
                    argument.Equals("--root-data-dir", StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith("--root-data-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Additional shortcut arguments cannot override the resolved data root.", nameof(additionalArguments));
                }
                arguments.Add(argument);
            }
            if (applicationFilePath.EndsWith(".xci", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add("--application-id");
                arguments.Add(applicationId);
            }
            arguments.Add(Path.GetFullPath(applicationFilePath));
            return arguments.AsReadOnly();
        }

        public static string QuotePosixArgument(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("A process argument cannot contain NUL.", nameof(value));
            }
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }

        public static string BuildMacosLauncher(string executablePath, IReadOnlyList<string> arguments)
        {
            RequireAbsolutePath(executablePath, nameof(executablePath));
            ArgumentNullException.ThrowIfNull(arguments);
            string template = EmbeddedResources.ReadAllText("Ryujinx/Assets/ShortcutFiles/shortcut-launch-script.sh");
            return string.Format(template, QuotePosixArgument(executablePath), string.Join(" ", arguments.Select(QuotePosixArgument)));
        }

        public static string BuildMacosShortcutPlist(string applicationName, string applicationId)
        {
            ValidateApplicationId(applicationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
            string template = EmbeddedResources.ReadAllText("Ryujinx/Assets/ShortcutFiles/shortcut-template.plist");
            string displayName = $"{RyubingCanaryLocalPolicy.DisplayName} — {applicationName}";
            XDocument plist = XDocument.Parse(string.Format(template, RunnerName, SecurityElement.Escape(displayName), IconName));
            plist.Root!.Element("dict")!.AddFirst(
                new XElement("key", "CFBundleName"), new XElement("string", displayName),
                new XElement("key", "CFBundleIdentifier"),
                new XElement("string", $"{RyubingCanaryLocalPolicy.BundleIdentifier}.shortcut.{applicationId.ToLowerInvariant()}"));
            return plist.ToString();
        }

        public static string GetMacosShortcutPath(string userHome, string applicationName, string applicationId)
        {
            RequireAbsolutePath(userHome, nameof(userHome));
            ValidateApplicationId(applicationId);
            return Path.Combine(Path.GetFullPath(userHome), "Applications", $"{RyubingCanaryLocalPolicy.DisplayName} Shortcuts",
                $"{CleanName(applicationName)} - {applicationId.ToLowerInvariant()}.app");
        }

        /// <summary>Publishes only new bundles in this build's user Applications namespace, never /Applications.</summary>
        [SupportedOSPlatform("macos")]
        public static void WriteMacosShortcut(string destination, string launcher, string plist, byte[] pngData, string userHome = null)
        {
            RequireAbsolutePath(destination, nameof(destination));
            userHome ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            RequireAbsolutePath(userHome, nameof(userHome));
            destination = Path.GetFullPath(destination);
            string expectedParent = Path.Combine(Path.GetFullPath(userHome), "Applications", $"{RyubingCanaryLocalPolicy.DisplayName} Shortcuts");
            if (!destination.EndsWith(".app", StringComparison.Ordinal) ||
                !string.Equals(Path.GetDirectoryName(destination), expectedParent, StringComparison.Ordinal))
            {
                throw new ArgumentException("A shortcut must be a new .app in the dedicated user Applications namespace.", nameof(destination));
            }
            ArgumentNullException.ThrowIfNull(launcher);
            ArgumentNullException.ThrowIfNull(plist);
            ArgumentNullException.ThrowIfNull(pngData);
            EnsureNoLinks(destination);
            RefuseExisting(destination);
            Directory.CreateDirectory(expectedParent);
            EnsureNoLinks(expectedParent);
            string staging = Path.Combine(expectedParent, $".ryubing-canary-local-shortcut-{Guid.NewGuid():N}");
            RefuseExisting(staging);
            Directory.CreateDirectory(staging);
            try
            {
                string contents = Path.Combine(staging, "Contents");
                string macos = Path.Combine(contents, "MacOS");
                string resources = Path.Combine(contents, "Resources");
                Directory.CreateDirectory(macos);
                Directory.CreateDirectory(resources);
                string runner = Path.Combine(macos, RunnerName);
                WriteNewFile(runner, Encoding.UTF8.GetBytes(launcher));
                File.SetUnixFileMode(runner, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                WriteNewFile(Path.Combine(resources, IconName), pngData);
                WriteNewFile(Path.Combine(contents, "Info.plist"), Encoding.UTF8.GetBytes(plist));
                EnsureNoLinks(destination);
                Directory.Move(staging, destination);
            }
            catch (Exception exception)
            {
                // Retain partial owned staging for inspection rather than deleting unknown contents.
                throw new IOException($"Shortcut was not installed. Any partial output is retained at '{staging}'. Existing destinations were not replaced.", exception);
            }
        }

        public static void CreateAppShortcut(string applicationFilePath, string applicationName, string applicationId, byte[] iconData, string args = "")
        {
            string root = AppDataManager.ValidatePathWithinRoot(AppDataManager.BaseDirPath);
            IReadOnlyList<string> arguments = BuildLaunchArguments(root, applicationFilePath, applicationId, ParseAdditionalArguments(args));
            string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, OperatingSystem.IsWindows() ? "Ryujinx.exe" : "Ryujinx");
            EnsureNoLinks(executable);
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("Create shortcuts from the running local app, not an unrelated host or installation.", executable);
            }
            using SKBitmap image = SKBitmap.Decode(iconData) ?? throw new InvalidDataException("The game shortcut icon could not be decoded.");
            string cleanedName = $"{RyubingCanaryLocalPolicy.DisplayName} - {CleanName(applicationName)} - {applicationId.ToLowerInvariant()}";
            if (OperatingSystem.IsMacOS())
            {
                using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
                WriteMacosShortcut(GetMacosShortcutPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), applicationName, applicationId),
                    BuildMacosLauncher(executable, arguments), BuildMacosShortcutPlist(applicationName, applicationId), png.ToArray());
                return;
            }
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            RequireAbsolutePath(desktop, nameof(desktop));
            EnsureNoLinks(desktop);
            string icons = AppDataManager.ValidatePathWithinRoot(Path.Combine(root, "shortcuts"));
            Directory.CreateDirectory(icons);
            icons = AppDataManager.ValidatePathWithinRoot(icons);
            if (OperatingSystem.IsWindows())
            {
                string destination = Path.Combine(desktop, cleanedName + ".lnk");
                RefuseExisting(destination);
                string icon = AppDataManager.ValidatePathWithinRoot(Path.Combine(icons, applicationId + ".ico"));
                SaveBitmapAsIcon(image, icon);
                Shortcut shortcut = Shortcut.CreateShortcut(executable, string.Join(" ", arguments.Select(QuoteWindowsArgument)), icon, 0);
                shortcut.StringData.NameString = cleanedName;
                string stagedLink = Path.Combine(desktop, $".ryubing-canary-local-shortcut-{Guid.NewGuid():N}.lnk");
                RefuseExisting(stagedLink);
                shortcut.WriteToFile(stagedLink);
                File.Move(stagedLink, destination, false);
                return;
            }
            if (OperatingSystem.IsLinux())
            {
                string destination = Path.Combine(desktop, cleanedName + ".desktop");
                RefuseExisting(destination);
                string icon = AppDataManager.ValidatePathWithinRoot(Path.Combine(icons, applicationId + ".png"));
                using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
                WriteNewFile(icon, png.ToArray());
                string command = string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteDesktopArgument));
                string template = EmbeddedResources.ReadAllText("Ryujinx/Assets/ShortcutFiles/shortcut-template.desktop");
                WriteNewFile(destination, Encoding.UTF8.GetBytes(string.Format(template, cleanedName, icon, command)));
                return;
            }
            throw new PlatformNotSupportedException("Shortcut support has not been implemented for this OS.");
        }

        public static void WriteNewFile(string path, byte[] bytes)
        {
            EnsureNoLinks(path);
            using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(bytes);
            file.Flush(true);
        }

        private static string CleanName(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            HashSet<char> invalid = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':'];
            string result = new(value.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).Take(100).ToArray());
            return result.Trim().Trim('.') is { Length: > 0 } name ? name : "Game";
        }

        private static void ValidateApplicationId(string value)
        {
            if (value is null || value.Length != 16 || value.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new ArgumentException("A game shortcut requires a 16-digit hexadecimal application ID.", nameof(value));
            }
        }

        private static void RequireAbsolutePath(string path, string parameter)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("A shortcut requires an absolute resolved path.", parameter);
            }
        }

        private static void RefuseExisting(string path)
        {
            if (Path.Exists(path) || new FileInfo(path).LinkTarget != null || new DirectoryInfo(path).LinkTarget != null)
            {
                throw new IOException($"Refusing to overwrite existing shortcut output '{path}'.");
            }
        }

        private static void EnsureNoLinks(string path)
        {
            RequireAbsolutePath(path, nameof(path));
            string current = Path.GetPathRoot(path)!;
            foreach (string component in Path.GetRelativePath(current, Path.GetFullPath(path)).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, component);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException($"Shortcut paths must not traverse a symbolic link or reparse point: '{current}'.");
                    }
                }
                catch (FileNotFoundException)
                {
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }
            }
        }

        private static IEnumerable<string> ParseAdditionalArguments(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return [];
            }
            if (value.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("Shortcut arguments cannot contain NUL.", nameof(value));
            }
            List<string> result = [];
            StringBuilder token = new();
            char quote = '\0';
            bool started = false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == '\\' && index + 1 < value.Length && (value[index + 1] == '"' || value[index + 1] == '\\' || value[index + 1] == '\''))
                {
                    token.Append(value[++index]);
                    started = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }
                else if (quote == '\0' && (character == '\'' || character == '"'))
                {
                    quote = character;
                    started = true;
                }
                else if (quote == '\0' && char.IsWhiteSpace(character))
                {
                    if (started)
                    {
                        result.Add(token.ToString());
                        token.Clear();
                        started = false;
                    }
                }
                else
                {
                    token.Append(character);
                    started = true;
                }
            }
            if (quote != '\0')
            {
                throw new ArgumentException("Unterminated quote in shortcut arguments.", nameof(value));
            }
            if (started)
            {
                result.Add(token.ToString());
            }
            return result;
        }

        private static string QuoteWindowsArgument(string value)
        {
            StringBuilder result = new("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    slashes++;
                    continue;
                }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }

        private static string QuoteDesktopArgument(string value) =>
            "\"" + value.Replace("\\", "\\\\\\\\").Replace("\"", "\\\\\"").Replace("$", "\\\\$").Replace("`", "\\\\`").Replace("%", "%%") + "\"";

        [SupportedOSPlatform("windows")]
        private static void SaveBitmapAsIcon(SKBitmap source, string filePath)
        {
            byte[] header = [0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, 0, 32, 0, 0, 0, 0, 0, 22, 0, 0, 0];
            EnsureNoLinks(filePath);
            using FileStream file = new(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(header);
            using SKData data = source.Encode(SKEncodedImageFormat.Png, 100);
            data.SaveTo(file);
            long length = file.Length - header.Length;
            file.Seek(14, SeekOrigin.Begin);
            file.WriteByte((byte)length);
            file.WriteByte((byte)(length >> 8));
            file.WriteByte((byte)(length >> 16));
            file.WriteByte((byte)(length >> 24));
            file.Flush(true);
        }
    }
}
