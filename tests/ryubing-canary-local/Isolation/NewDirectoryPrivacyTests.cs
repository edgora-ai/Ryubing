using NUnit.Framework;
using Ryujinx.Common.Configuration;
using System;
using System.IO;
using static Ryujinx.Common.Configuration.RyubingCanaryLocalPolicy;

namespace Ryubing.Canary.Local.Tests.Isolation
{
    [TestFixture]
    [NonParallelizable]
    public class NewDirectoryPrivacyTests
    {
        private TestDirectory _fixture;
        private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        [SetUp]
        public void SetUp()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("Unix permission bits are not a Windows filesystem contract.");
            }
            _fixture = new TestDirectory("directory-privacy");
        }

        [TearDown]
        public void TearDown() => _fixture?.Dispose();

        [Test]
        public void NewDirectoriesAndEveryNewIntermediateParentAreOwnerOnly()
        {
            string parent = _fixture.Child("new-parent");
            string child = Path.Combine(parent, "new-data");
            PhysicalDataRootFileSystem.Instance.CreateDirectory(child);
            Assert.Multiple(() =>
            {
                Assert.That(File.GetUnixFileMode(parent), Is.EqualTo(OwnerOnly));
                Assert.That(File.GetUnixFileMode(child), Is.EqualTo(OwnerOnly));
            });
        }

        [Test]
        public void ExistingDirectoryPermissionsAreNotChanged()
        {
            UnixFileMode original = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(_fixture.Path, original);
            PhysicalDataRootFileSystem.Instance.CreateDirectory(_fixture.Path);
            Assert.That(File.GetUnixFileMode(_fixture.Path), Is.EqualTo(original));
        }

        [Test]
        public void InitializationKeepsExistingModesAndCreatesPrivateLayoutAndMods()
        {
            string root = _fixture.Child("data");
            string logs = Path.Combine(root, "Logs");
            Directory.CreateDirectory(logs);
            UnixFileMode original = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(root, original);
            File.SetUnixFileMode(logs, original);
            AppDataManager.Initialize(root, EnvironmentForFixture(), PhysicalDataRootFileSystem.Instance);
            string mods = AppDataManager.GetModsPath();
            string sdMods = AppDataManager.GetSdModsPath();
            Assert.Multiple(() =>
            {
                Assert.That(File.GetUnixFileMode(root), Is.EqualTo(original));
                Assert.That(File.GetUnixFileMode(logs), Is.EqualTo(original));
                foreach (string relative in new[] { "games", "profiles", "system", "bis", "sdcard" })
                {
                    Assert.That(File.GetUnixFileMode(Path.Combine(root, relative)), Is.EqualTo(OwnerOnly), relative);
                }
                Assert.That(File.GetUnixFileMode(mods), Is.EqualTo(OwnerOnly));
                Assert.That(File.GetUnixFileMode(sdMods), Is.EqualTo(OwnerOnly));
            });
        }

        [Test]
        public void DefaultMissingRootAndItsNewParentsArePrivate()
        {
            PathEnvironment environment = EnvironmentForFixture() with { IsMacOS = true };
            PhysicalDataRootFileSystem.Instance.CreateDirectory(environment.UserProfilePath);
            AppDataManager.Initialize(null, environment, PhysicalDataRootFileSystem.Instance);
            Assert.Multiple(() =>
            {
                Assert.That(AppDataManager.BaseDirPath,
                    Is.EqualTo(Path.Combine(environment.UserProfilePath, "Library", "Application Support", DataDirectoryName)));
                Assert.That(File.GetUnixFileMode(Path.Combine(environment.UserProfilePath, "Library")), Is.EqualTo(OwnerOnly));
                Assert.That(File.GetUnixFileMode(Path.Combine(environment.UserProfilePath, "Library", "Application Support")), Is.EqualTo(OwnerOnly));
                Assert.That(File.GetUnixFileMode(AppDataManager.BaseDirPath), Is.EqualTo(OwnerOnly));
            });
        }

        [Test]
        public void DanglingLinksAreClassifiedWithoutFollowingTargets()
        {
            string missingTarget = _fixture.Child("missing-target");
            string directoryLink = _fixture.Child("directory-link");
            string fileLink = _fixture.Child("file-link");
            Directory.CreateSymbolicLink(directoryLink, missingTarget);
            File.CreateSymbolicLink(fileLink, Path.Combine(missingTarget, "key.keys"));
            Assert.Multiple(() =>
            {
                Assert.That(PhysicalDataRootFileSystem.Instance.GetPathKind(directoryLink), Is.EqualTo(PathKind.Link));
                Assert.That(PhysicalDataRootFileSystem.Instance.GetPathKind(fileLink), Is.EqualTo(PathKind.Link));
                Assert.That(() => PhysicalDataRootFileSystem.Instance.CreateDirectory(directoryLink), Throws.TypeOf<IOException>());
                Assert.That(Directory.Exists(missingTarget), Is.False);
            });
        }

        [Test]
        public void UnwritableRootDoesNotCreateChildrenOrFallbackDirectories()
        {
            string root = _fixture.Child("readonly");
            Directory.CreateDirectory(root);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.That(() => AppDataManager.Initialize(root, EnvironmentForFixture(), PhysicalDataRootFileSystem.Instance),
                    Throws.TypeOf<UnauthorizedAccessException>());
                Assert.Multiple(() =>
                {
                    Assert.That(Directory.EnumerateFileSystemEntries(root), Is.Empty);
                    Assert.That(AppDataManager.BaseDirPath, Is.Null);
                    Assert.That(Directory.Exists(_fixture.Child("home")), Is.False);
                });
            }
            finally
            {
                File.SetUnixFileMode(root, OwnerOnly);
            }
        }

        private PathEnvironment EnvironmentForFixture() => new(_fixture.Child("home"), _fixture.Child("appdata"),
            _fixture.Child("app"), _fixture.Path, OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());
    }
}
