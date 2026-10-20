using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using NUnit.Framework;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging.Targets;
using Ryujinx.HLE.FileSystem;
using System;
using System.IO;
using System.Linq;
using static Ryujinx.Common.Configuration.RyubingCanaryLocalPolicy;

namespace Ryubing.Canary.Local.Tests.Isolation
{
    [TestFixture]
    [NonParallelizable]
    public class VfsAndLogsTests
    {
        private TestDirectory _fixture;
        private PathEnvironment _environment;
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _fixture = new TestDirectory("vfs-logs");
            _root = _fixture.Child("data");
            PhysicalDataRootFileSystem.Instance.CreateDirectory(_root);
            _environment = new PathEnvironment(_fixture.Child("home"), _fixture.Child("appdata"),
                _fixture.Child("app"), _fixture.Path, OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());
            AppDataManager.Initialize(_root, _environment, PhysicalDataRootFileSystem.Instance);
            // Fail before invoking a key reader if a regression ever restores the host ~/.switch alias.
            Assert.That(AppDataManager.KeysDirPathUser, Is.EqualTo(Path.Combine(_root, "system")));
        }

        [TearDown]
        public void TearDown() => _fixture?.Dispose();

        [TestCase("/../sdcard-other/file")]
        [TestCase("/../../data-other/file")]
        [TestCase("//../system/prod.keys")]
        [TestCase("/../system/prod.keys")]
        public void VfsMountTraversalCannotReachAnotherMountOrPrefixSibling(string fileName)
        {
            string sdcard = Path.Combine(_root, "sdcard");
            Assert.That(VirtualFileSystem.GetFullPath(sdcard, fileName), Is.Null);
            Assert.That(Directory.Exists(_root + "-other"), Is.False);
        }

        [Test]
        public void VfsInternalMappingsRoundTripInsideSelectedRoot()
        {
            string sdcard = Path.Combine(_root, "sdcard");
            string expected = Path.Combine(sdcard, "Nintendo", "file");
            Assert.Multiple(() =>
            {
                Assert.That(VirtualFileSystem.GetFullPath(sdcard, "/Nintendo/file"), Is.EqualTo(expected));
                Assert.That(VirtualFileSystem.GetFullPath(sdcard, "//Nintendo/file"), Is.EqualTo(expected));
                Assert.That(VirtualFileSystem.SwitchPathToSystemPath("sdcard:/Nintendo/file"), Is.EqualTo(expected));
                Assert.That(VirtualFileSystem.SystemPathToSwitchPath(expected), Is.EqualTo("sdcard:/Nintendo/file"));
                Assert.That(VirtualFileSystem.GetNandPath(), Is.EqualTo(Path.Combine(_root, "bis")));
                Assert.That(VirtualFileSystem.SystemPathToSwitchPath(_root + "-other/file"), Is.Null);
                Assert.That(VirtualFileSystem.SystemPathToSwitchPath(_root), Is.Null);
                Assert.That(VirtualFileSystem.SystemPathToSwitchPath("relative/file"), Is.Null);
                Assert.That(VirtualFileSystem.GetFullPath(sdcard, "relative/file"), Is.Null);
                Assert.That(VirtualFileSystem.SwitchPathToSystemPath("no-mount"), Is.Null);
            });
        }

        [Test]
        public void VfsCannotCreateOrResolveAnOutsideMount()
        {
            string outside = _fixture.Child("outside");
            Assert.That(() => VirtualFileSystem.SwitchPathToSystemPath("../outside:/file"), Throws.TypeOf<IOException>());
            Assert.That(() => VirtualFileSystem.GetFullPath(outside, "/file"), Throws.TypeOf<IOException>());
            Assert.That(Directory.Exists(outside), Is.False);
        }

        [Test]
        public void VfsChildLinksAreRejectedBeforeReadingOwnedSentinelTargets()
        {
            string target = _fixture.Child("linked-target");
            Directory.CreateDirectory(target);
            string sentinel = Path.Combine(target, "file");
            File.WriteAllText(sentinel, "fixture-sentinel");
            string link = Path.Combine(_root, "sdcard", "linked");
            Directory.CreateSymbolicLink(link, target);
            Assert.That(() => VirtualFileSystem.GetFullPath(Path.Combine(_root, "sdcard"), "/linked/file"), Throws.TypeOf<IOException>());
            Assert.That(() => VirtualFileSystem.SystemPathToSwitchPath(Path.Combine(link, "file")), Throws.TypeOf<IOException>());
            Assert.That(AppDataManager.ValidateSelectedDataTree, Throws.TypeOf<IOException>());
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("fixture-sentinel"));
        }

        [TestCase("prod.keys")]
        [TestCase("title.keys")]
        [TestCase("console.keys")]
        [TestCase("dev.keys")]
        public void EveryKeyFileLeafIsRevalidatedAndLinkedKeyTargetsAreNotRead(string name)
        {
            using VirtualFileSystem vfs = CreateIsolatedVfs();
            string target = _fixture.Child("owned-key-sentinel");
            byte[] sentinel = [0x46, 0x49, 0x58, 0x54, 0x55, 0x52, 0x45];
            File.WriteAllBytes(target, sentinel);
            File.CreateSymbolicLink(Path.Combine(AppDataManager.KeysDirPath, name), target);
            Assert.That(vfs.ReloadKeySet, Throws.TypeOf<IOException>());
            Assert.That(File.ReadAllBytes(target), Is.EqualTo(sentinel));
        }

        [Test]
        public void ReplacingSystemDirectoryWithALinkCannotChangeKeySource()
        {
            using VirtualFileSystem vfs = CreateIsolatedVfs();
            string selectedSystem = AppDataManager.KeysDirPath;
            Directory.Move(selectedSystem, _fixture.Child("initial-system"));
            string target = _fixture.Child("old-system");
            Directory.CreateDirectory(target);
            string sentinel = Path.Combine(target, "prod.keys");
            File.WriteAllText(sentinel, "fixture-only-not-real-keys");
            Directory.CreateSymbolicLink(selectedSystem, target);
            Assert.That(vfs.ReloadKeySet, Throws.TypeOf<IOException>());
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("fixture-only-not-real-keys"));
        }

        [Test]
        public void UserProfileModeStillUsesOnlySelectedSystemAndLoadsSyntheticLocalKeys()
        {
            string legacyKeys = Path.Combine(_environment.UserProfilePath, ".switch");
            Directory.CreateDirectory(legacyKeys);
            string dummyHeaderKey = "header_key = " + new string('1', 64) + Environment.NewLine;
            string[] keyNames = ["prod.keys", "title.keys", "console.keys", "dev.keys"];
            foreach (string name in keyNames)
            {
                File.WriteAllText(Path.Combine(legacyKeys, name), dummyHeaderKey);
            }
            AppDataManager.Initialize(null, _environment, PhysicalDataRootFileSystem.Instance);
            Assert.That(AppDataManager.Mode, Is.EqualTo(AppDataManager.LaunchMode.UserProfile));
            Assert.That(AppDataManager.KeysDirPathUser, Is.EqualTo(AppDataManager.KeysDirPath));
            Assert.That(AppDataManager.KeysDirPath,
                Is.EqualTo(Path.Combine(AppDataManager.BaseDirPath, "system")));
            using VirtualFileSystem vfs = CreateIsolatedVfs();
            Assert.That(vfs.KeySet.HeaderKey.IsZeros(), Is.True, "Dummy keys beside the fake home must never be loaded.");

            string localProd = Path.Combine(AppDataManager.KeysDirPath, "prod.keys");
            File.WriteAllText(localProd, dummyHeaderKey);
            vfs.ReloadKeySet();
            Assert.That(vfs.KeySet.HeaderKey.IsZeros(), Is.False, "The production reader must load the selected system file.");
            Assert.That(File.ReadAllText(localProd), Is.EqualTo(dummyHeaderKey));
            foreach (string name in keyNames)
            {
                Assert.That(File.ReadAllText(Path.Combine(legacyKeys, name)), Is.EqualTo(dummyHeaderKey));
            }
        }

        [Test]
        public void LogsAreUniqueExclusiveRootContainedAndNeverDeleteEarlierEvidence()
        {
            string logs = AppDataManager.LogsDirPath;
            string[] previousLogs = Enumerable.Range(0, 5).Select(index => Path.Combine(logs, $"earlier-{index}.log")).ToArray();
            foreach (string previous in previousLogs)
            {
                File.WriteAllText(previous, "retained-log-sentinel");
            }
            string oldLogs = Path.Combine(_environment.UserProfilePath, "Library", "Logs", "Nextendo-Local");
            Directory.CreateDirectory(oldLogs);
            string oldSentinel = Path.Combine(oldLogs, "old.log");
            File.WriteAllText(oldSentinel, "old-log-sentinel");

            string firstPath;
            string secondPath;
            using (FileStream first = FileLogTarget.PrepareLogFile(logs))
            using (FileStream second = FileLogTarget.PrepareLogFile("Logs"))
            {
                Assert.That(first, Is.Not.Null);
                Assert.That(second, Is.Not.Null);
                firstPath = first.Name;
                secondPath = second.Name;
                first.WriteByte(1);
                second.WriteByte(2);
                first.Flush(flushToDisk: true);
                second.Flush(flushToDisk: true);
                Assert.That(() => File.Open(firstPath, FileMode.CreateNew, FileAccess.Write).Dispose(), Throws.TypeOf<IOException>());
            }
            Assert.Multiple(() =>
            {
                Assert.That(Path.GetDirectoryName(firstPath), Is.EqualTo(logs));
                Assert.That(firstPath, Is.Not.EqualTo(secondPath));
                Assert.That(Path.GetFileName(firstPath), Does.StartWith(DataDirectoryName + "_"));
                Assert.That(File.ReadAllBytes(firstPath), Is.EqualTo(new byte[] { 1 }));
                Assert.That(File.ReadAllBytes(secondPath), Is.EqualTo(new byte[] { 2 }));
                Assert.That(Directory.EnumerateFiles(logs, "*.log").Count(), Is.EqualTo(7));
                foreach (string previous in previousLogs)
                {
                    Assert.That(File.ReadAllText(previous), Is.EqualTo("retained-log-sentinel"));
                }
                Assert.That(File.ReadAllText(oldSentinel), Is.EqualTo("old-log-sentinel"));
                if (!OperatingSystem.IsWindows())
                {
                    Assert.That(File.GetUnixFileMode(firstPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
                    Assert.That(File.GetUnixFileMode(secondPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
                }
            });
        }

        [Test]
        public void LogWriterCannotUseAnOutsideOrAlternateInternalDirectory()
        {
            string outside = _fixture.Child("outside-logs");
            string alternate = Path.Combine(_root, "alternate-logs");
            Assert.That(() => FileLogTarget.PrepareLogFile(outside), Throws.TypeOf<IOException>());
            Assert.That(() => FileLogTarget.PrepareLogFile(alternate), Throws.TypeOf<IOException>());
            Assert.That(Directory.Exists(outside), Is.False);
            Assert.That(Directory.Exists(alternate), Is.False);
        }

        [Test]
        public void ReplacingLogsWithALinkCannotAppendOrCleanOwnedTargetLogs()
        {
            string selectedLogs = AppDataManager.LogsDirPath;
            Directory.Move(selectedLogs, _fixture.Child("initial-logs"));
            string target = _fixture.Child("other-logs");
            Directory.CreateDirectory(target);
            string sentinel = Path.Combine(target, "old.log");
            File.WriteAllText(sentinel, "unchanged-log");
            Directory.CreateSymbolicLink(selectedLogs, target);
            Assert.That(() => FileLogTarget.PrepareLogFile(selectedLogs), Throws.TypeOf<IOException>());
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("unchanged-log"));
            Assert.That(Directory.EnumerateFiles(target).Single(), Is.EqualTo(sentinel));
        }

        [Test]
        public void UnwritableLogDirectoryNeverFallsBackToSharedLocations()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("The target build's unwritable-directory contract uses Unix permissions.");
            }
            string logs = AppDataManager.LogsDirPath;
            UnixFileMode original = File.GetUnixFileMode(logs);
            File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                using FileStream failed = FileLogTarget.PrepareLogFile(logs);
                Assert.That(failed, Is.Null);
                Assert.That(Directory.EnumerateFileSystemEntries(logs), Is.Empty);
                Assert.That(Directory.Exists(_environment.UserProfilePath), Is.False);
                Assert.That(Directory.Exists(_environment.ApplicationBaseDirectory), Is.False);
            }
            finally
            {
                File.SetUnixFileMode(logs, original);
            }
        }

        // Exercise the real key reader without mutating the process-wide CreateInstance singleton.
        // This constructor does not start an FS server, guest process, SDL, GUI or hardware service.
        private static VirtualFileSystem CreateIsolatedVfs() =>
            (VirtualFileSystem)Activator.CreateInstance(typeof(VirtualFileSystem), nonPublic: true);
    }
}
