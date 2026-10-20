using NUnit.Framework;
using Ryujinx.Common.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static Ryujinx.Common.Configuration.RyubingCanaryLocalPolicy;

namespace Ryubing.Canary.Local.Tests.Isolation
{
    [TestFixture]
    [NonParallelizable]
    public class DataRootPolicyTests
    {
        private PathEnvironment _environment;
        private FixtureFileSystem _fileSystem;
        private string _fixture;

        [SetUp]
        public void SetUp()
        {
            string ownedRoot = Environment.GetEnvironmentVariable("RYUBING_CANARY_LOCAL_TEST_ROOT");
            Assert.That(ownedRoot, Is.Not.Null.And.Not.Empty, "An explicit task-owned test root is required.");
            Assert.That(Path.IsPathFullyQualified(ownedRoot), Is.True);
            // These are in-memory path identities under the owned task, never host profile directories.
            _fixture = Path.Combine(ownedRoot, "policy-identities");
            string home = Path.Combine(_fixture, "home");
            _environment = new PathEnvironment(home, Path.Combine(home, ".config"),
                Path.Combine(_fixture, "apps", "Ryubing Canary Local.app", "Contents", "MacOS"),
                Path.Combine(_fixture, "working"), IsMacOS: true, IsWindows: false);
            _fileSystem = new FixtureFileSystem();
            _fileSystem.AddDirectory(home);
            _fileSystem.AddDirectory(_environment.ApplicationBaseDirectory);
            _fileSystem.AddDirectory(_environment.WorkingDirectory);
        }

        [Test]
        public void IdentityIsIndependentAndStable()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DisplayName, Is.EqualTo("Ryubing Canary Local"));
                Assert.That(BundleIdentifier, Is.EqualTo("local.ryubing.RyubingCanaryLocal"));
                Assert.That(DataDirectoryName, Is.EqualTo("Ryubing-Canary-Local"));
                Assert.That(PortableDirectoryName, Is.EqualTo("portable-ryubing-canary-local"));
            });
        }

        [Test]
        public void DefaultMacRootIsIndependentAndResolutionDoesNotWrite()
        {
            DataRootSelection selected = ResolveDataRoot(null, _environment, _fileSystem);
            Assert.Multiple(() =>
            {
                Assert.That(selected.Path, Is.EqualTo(Path.Combine(_environment.UserProfilePath, "Library", "Application Support", DataDirectoryName)));
                Assert.That(selected.Mode, Is.EqualTo(AppDataManager.LaunchMode.UserProfile));
                Assert.That(_fileSystem.Created, Is.Empty);
                Assert.That(_fileSystem.WriteProbes, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OtherPlatformsUseIndependentApplicationDataRoot(bool isWindows)
        {
            PathEnvironment environment = _environment with { IsMacOS = false, IsWindows = isWindows };
            DataRootSelection selected = ResolveDataRoot(null, environment, _fileSystem);
            Assert.That(selected.Path, Is.EqualTo(Path.Combine(environment.ApplicationDataPath, DataDirectoryName)));
            environment = environment with { ApplicationDataPath = "" };
            Assert.That(ResolveDataRoot(null, environment, _fileSystem).Path,
                Is.EqualTo(Path.Combine(environment.UserProfilePath, ".config", DataDirectoryName)));
        }

        [Test]
        public void GenericPortableOldDedicatedPortableAndAdjacentConfigAreNeverDiscovered()
        {
            string[] ignored = [Path.Combine(_environment.ApplicationBaseDirectory, "portable"),
                Path.Combine(_environment.ApplicationBaseDirectory, "portable-nextendo-local"),
                Path.Combine(_environment.ApplicationBaseDirectory, "Config.json")];
            _fileSystem.AddDirectory(ignored[0]);
            _fileSystem.AddLink(ignored[1]);
            _fileSystem.AddFile(ignored[2]);
            DataRootSelection selected = ResolveDataRoot(null, _environment, _fileSystem);
            string global = GetGlobalConfigurationPath(selected.Path, "Config.json", _fileSystem);
            Assert.Multiple(() =>
            {
                Assert.That(selected.Mode, Is.EqualTo(AppDataManager.LaunchMode.UserProfile));
                Assert.That(global, Is.EqualTo(Path.Combine(selected.Path, "Config.json")));
                foreach (string path in ignored)
                {
                    Assert.That(_fileSystem.Inspections, Does.Not.Contain(path));
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OnlyDedicatedPortableNameIsSelected(bool besideBundle)
        {
            string parent = besideBundle
                ? Path.GetFullPath(Path.Combine(_environment.ApplicationBaseDirectory, "..", "..", ".."))
                : _environment.ApplicationBaseDirectory;
            string portable = Path.Combine(parent, PortableDirectoryName);
            _fileSystem.AddDirectory(portable);
            Assert.That(ResolveDataRoot(null, _environment, _fileSystem),
                Is.EqualTo(new DataRootSelection(portable, AppDataManager.LaunchMode.Portable)));
        }

        [Test]
        public void ExplicitRootOverridesPortableWithoutEvenInspectingIt()
        {
            string portable = Path.Combine(_environment.ApplicationBaseDirectory, PortableDirectoryName);
            _fileSystem.AddLink(portable);
            string root = AddIndependentRoot();
            Assert.That(ResolveDataRoot("independent", _environment, _fileSystem),
                Is.EqualTo(new DataRootSelection(root, AppDataManager.LaunchMode.Custom)));
            Assert.That(_fileSystem.Inspections, Does.Not.Contain(portable));
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase("missing")]
        [TestCase("bad\0path")]
        public void InvalidExplicitRootDoesNotCreateOrFallback(string root)
        {
            Assert.That(() => AppDataManager.Initialize(root, _environment, _fileSystem), Throws.Exception);
            Assert.Multiple(() =>
            {
                Assert.That(AppDataManager.BaseDirPath, Is.Null);
                Assert.That(_fileSystem.Created, Is.Empty);
                Assert.That(_fileSystem.WriteProbes, Is.Empty);
            });
        }

        [Test]
        public void RejectedRootClearsEarlierSuccessfulRoot()
        {
            AppDataManager.Initialize(AddIndependentRoot(), _environment, _fileSystem);
            _fileSystem.Created.Clear();
            _fileSystem.WriteProbes.Clear();
            Assert.That(() => AppDataManager.Initialize("missing", _environment, _fileSystem), Throws.TypeOf<IOException>());
            Assert.Multiple(() =>
            {
                Assert.That(AppDataManager.BaseDirPath, Is.Null);
                Assert.That(AppDataManager.LogsDirPath, Is.Null);
                Assert.That(AppDataManager.KeysDirPathUser, Is.Null);
                Assert.That(() => AppDataManager.ValidatePathWithinRoot("Config.json"), Throws.TypeOf<InvalidOperationException>());
                Assert.That(_fileSystem.Created, Is.Empty);
                Assert.That(_fileSystem.WriteProbes, Is.Empty);
            });
        }

        [Test]
        public void FileCannotBeAnExplicitRoot()
        {
            string root = Path.Combine(_environment.WorkingDirectory, "file");
            _fileSystem.AddFile(root);
            Assert.That(() => ResolveDataRoot(root, _environment, _fileSystem), Throws.TypeOf<IOException>());
        }

        [TestCase("Ryujinx")]
        [TestCase("Ryubing")]
        [TestCase("Nextendo")]
        [TestCase("Nextendo-Local")]
        public void LegacyDataLogsAndTheirAncestorsAreRejectedBeforeInspection(string name)
        {
            string[] protectedRoots = [Path.Combine(_environment.UserProfilePath, "Library", "Application Support", name),
                Path.Combine(_environment.UserProfilePath, ".config", name),
                Path.Combine(_environment.UserProfilePath, "Library", "Logs", name)];
            foreach (string legacy in protectedRoots)
            {
                foreach (string requested in new[] { legacy, Path.Combine(legacy, "system"), Path.GetDirectoryName(legacy) })
                {
                    AssertProtectedBeforeInspection(requested);
                }
            }
            AssertProtectedBeforeInspection(Path.Combine(_environment.UserProfilePath, "Library", "Logs", "Nextendo Local"));
        }

        [TestCase("Ryujinx.app")]
        [TestCase("Ryubing.app")]
        [TestCase("Nextendo.app")]
        [TestCase("Nextendo Local.app")]
        public void OldInstalledBundleTreesAreProtectedWithoutReadingThem(string bundleName)
        {
            foreach (string parent in new[] { Path.Combine(Path.GetPathRoot(_fixture), "Applications"),
                Path.Combine(_environment.UserProfilePath, "Applications"), Path.Combine(_environment.UserProfilePath, "Downloads"),
                Path.Combine(_fixture, "nonstandard-install") })
            {
                string bundle = Path.Combine(parent, bundleName);
                AssertProtectedBeforeInspection(bundle);
                AssertProtectedBeforeInspection(Path.Combine(bundle, "Contents", "MacOS", "data"));
            }
        }

        [TestCase(".switch")]
        [TestCase("portable")]
        [TestCase("portable-nextendo-local")]
        public void OldKeysAndPortableRootsAndTheirChildrenAreProtected(string name)
        {
            string parent = name == ".switch" ? _environment.UserProfilePath : _environment.ApplicationBaseDirectory;
            AssertProtectedBeforeInspection(Path.Combine(parent, name));
            AssertProtectedBeforeInspection(Path.Combine(parent, name, "Logs"));
            if (name != ".switch")
            {
                string besideBundle = Path.GetFullPath(Path.Combine(_environment.ApplicationBaseDirectory, "..", "..", "..", name));
                AssertProtectedBeforeInspection(besideBundle);
            }
        }

        [Test]
        public void MacCaseVariantsAreProtectedAndComponentSiblingsRemainIndependent()
        {
            AssertProtectedBeforeInspection(Path.Combine(_environment.UserProfilePath, "Library", "Application Support", "nextendo-local"));
            string root = Path.Combine(_environment.WorkingDirectory, "Ryujinx-LocalFixture");
            _fileSystem.AddDirectory(root);
            Assert.That(ResolveDataRoot(root, _environment, _fileSystem).Path, Is.EqualTo(root));
            Assert.That(() => ValidateContainedPath(root, root + "-other/Config.json", _fileSystem), Throws.TypeOf<IOException>());
            Assert.That(() => ValidateContainedPath(root, Path.Combine(root, "..", "other", "Config.json"), _fileSystem), Throws.TypeOf<IOException>());
        }

        [Test]
        public void RootAndParentLinksAreRejectedWithoutFollowing()
        {
            string alias = Path.Combine(_environment.WorkingDirectory, "old-alias");
            _fileSystem.AddLink(alias);
            Assert.That(() => ResolveDataRoot(alias, _environment, _fileSystem), Throws.TypeOf<IOException>());
            Assert.That(() => ResolveDataRoot(Path.Combine(alias, "child"), _environment, _fileSystem), Throws.TypeOf<IOException>());
            Assert.That(_fileSystem.Inspections, Does.Not.Contain(Path.Combine(alias, "child")));
            Assert.That(_fileSystem.Enumerated, Does.Not.Contain(alias));
            Assert.That(_fileSystem.WriteProbes, Is.Empty);
        }

        [Test]
        public void DedicatedPortableLinkFailsInsteadOfFallingBack()
        {
            _fileSystem.AddLink(Path.Combine(_environment.ApplicationBaseDirectory, PortableDirectoryName));
            Assert.That(() => AppDataManager.Initialize(null, _environment, _fileSystem), Throws.TypeOf<IOException>());
            Assert.That(_fileSystem.Created, Is.Empty);
            Assert.That(_fileSystem.WriteProbes, Is.Empty);
        }

        [TestCase("Logs")]
        [TestCase("bis/user/save")]
        [TestCase("sdcard/Nintendo")]
        [TestCase("games/0100000000000001/Config.json")]
        [TestCase("profiles/controller/profile.json")]
        [TestCase("system/prod.keys")]
        [TestCase("system/title.keys")]
        [TestCase("system/console.keys")]
        [TestCase("system/dev.keys")]
        public void ExistingChildLinksStopAllInitializationWrites(string relative)
        {
            string root = AddIndependentRoot();
            string link = Path.Combine(root, relative);
            _fileSystem.AddLink(link);
            Assert.That(() => AppDataManager.Initialize(root, _environment, _fileSystem), Throws.TypeOf<IOException>());
            Assert.That(_fileSystem.Created, Is.Empty);
            Assert.That(_fileSystem.WriteProbes, Is.Empty);
            Assert.That(_fileSystem.Enumerated, Does.Not.Contain(link));
        }

        [TestCase("Logs")]
        [TestCase("system")]
        public void MandatoryDirectoryFilesFailBeforeAnyWrite(string directory)
        {
            string root = AddIndependentRoot();
            _fileSystem.AddFile(Path.Combine(root, directory));
            Assert.That(() => AppDataManager.Initialize(root, _environment, _fileSystem), Throws.TypeOf<IOException>());
            Assert.That(_fileSystem.Created, Is.Empty);
            Assert.That(_fileSystem.WriteProbes, Is.Empty);
        }

        [Test]
        public void UnwritableExplicitRootFailsBeforeCreatingChildren()
        {
            string root = AddIndependentRoot();
            _fileSystem.Unwritable = root;
            Assert.That(() => AppDataManager.Initialize(root, _environment, _fileSystem), Throws.TypeOf<UnauthorizedAccessException>());
            Assert.That(AppDataManager.BaseDirPath, Is.Null);
            Assert.That(_fileSystem.Created, Is.Empty);
        }

        [Test]
        public void FullLayoutAndBothConfigurationIdentitiesStayWithinRoot()
        {
            string root = AddIndependentRoot();
            AppDataManager.Initialize(root, _environment, _fileSystem);
            string global = AppDataManager.GetGlobalConfigurationPath("Config.json");
            string title = AppDataManager.GetTitleConfigurationPath("0100000000000001", "Config.json");
            Assert.Multiple(() =>
            {
                Assert.That(AppDataManager.GetOrCreateLogsDir(), Is.EqualTo(Path.Combine(root, "Logs")));
                Assert.That(AppDataManager.KeysDirPath, Is.EqualTo(Path.Combine(root, "system")));
                Assert.That(AppDataManager.KeysDirPathUser, Is.EqualTo(AppDataManager.KeysDirPath));
                Assert.That(AppDataManager.GamesDirPath, Is.EqualTo(Path.Combine(root, "games")));
                Assert.That(AppDataManager.ProfilesDirPath, Is.EqualTo(Path.Combine(root, "profiles")));
                Assert.That(_fileSystem.Kind(Path.Combine(root, "bis")), Is.EqualTo(PathKind.Directory));
                Assert.That(_fileSystem.Kind(Path.Combine(root, "sdcard")), Is.EqualTo(PathKind.Directory));
                Assert.That(title, Is.EqualTo(Path.Combine(root, "games", "0100000000000001", "Config.json")));
                Assert.That(AppDataManager.GetGlobalConfigurationPath("Config.json"), Is.EqualTo(global));
            });
            _fileSystem.AddLink(AppDataManager.LogsDirPath);
            Assert.That(() => AppDataManager.GetOrCreateLogsDir(), Throws.TypeOf<IOException>());
        }

        [TestCase("../old")]
        [TestCase("0100000000000001/../../old")]
        [TestCase("not-a-title")]
        [TestCase("000000000000000g")]
        public void InvalidTitleIdsCannotBecomeConfigurationPaths(string titleId)
        {
            Assert.That(() => GetTitleConfigurationPath(AddIndependentRoot(), titleId, "Config.json", _fileSystem),
                Throws.TypeOf<ArgumentException>());
        }

        [TestCase("../Config.json")]
        [TestCase("Config\\other.json")]
        [TestCase("C:Config.json")]
        [TestCase(".")]
        [TestCase("..")]
        public void ConfigurationNamesMustBeSingleFileNames(string name)
        {
            Assert.That(() => GetGlobalConfigurationPath(AddIndependentRoot(), name, _fileSystem), Throws.TypeOf<ArgumentException>());
        }

        [TestCase("keyboard")]
        [TestCase("controller")]
        public void ProfilePathsAreTypedRootContainedAndDoNotCreateDirectories(string type)
        {
            string root = AddIndependentRoot();
            AppDataManager.Initialize(root, _environment, _fileSystem);
            _fileSystem.Created.Clear();
            Assert.That(AppDataManager.GetInputProfilePath(type, "独立 profile"),
                Is.EqualTo(Path.Combine(root, "profiles", type, "独立 profile.json")));
            Assert.That(_fileSystem.Created, Is.Empty);
            _fileSystem.AddLink(Path.Combine(root, "profiles", type));
            Assert.That(() => AppDataManager.GetInputProfilePath(type, "profile"), Throws.TypeOf<IOException>());
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase(".")]
        [TestCase("..")]
        [TestCase("../old")]
        [TestCase("profile/name")]
        [TestCase("profile\\name")]
        [TestCase("/absolute")]
        [TestCase("C:profile")]
        [TestCase("bad\0name")]
        [TestCase("bad\nname")]
        [TestCase("profile.")]
        public void ProfileNamesCannotEscapeOrAliasOtherPaths(string name)
        {
            AppDataManager.Initialize(AddIndependentRoot(), _environment, _fileSystem);
            Assert.That(() => AppDataManager.GetInputProfilePath("controller", name), Throws.TypeOf<ArgumentException>());
        }

        [TestCase("../controller")]
        [TestCase("Controller")]
        [TestCase("mouse")]
        public void UnknownProfileTypesAreRejected(string type)
        {
            AppDataManager.Initialize(AddIndependentRoot(), _environment, _fileSystem);
            Assert.That(() => AppDataManager.GetInputProfilePath(type, "profile"), Throws.TypeOf<ArgumentException>());
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void EmptyCustomModsUseIndependentDefaults(string value)
        {
            string root = AddIndependentRoot();
            AppDataManager.Initialize(root, _environment, _fileSystem);
            AppDataManager.CustomModsPath = value;
            AppDataManager.CustomSdModsPath = value;
            Assert.That(AppDataManager.GetModsPath(), Is.EqualTo(Path.Combine(root, "mods")));
            Assert.That(AppDataManager.GetSdModsPath(), Is.EqualTo(Path.Combine(root, "sdcard", "atmosphere")));
        }

        [Test]
        public void CustomModOutputsCannotEscapeAndAreRevalidatedBeforeUse()
        {
            string root = AddIndependentRoot();
            AppDataManager.Initialize(root, _environment, _fileSystem);
            Assert.That(() => AppDataManager.CustomModsPath = "../outside", Throws.TypeOf<IOException>());
            Assert.That(() => AppDataManager.CustomSdModsPath = root + "-other", Throws.TypeOf<IOException>());
            AppDataManager.CustomModsPath = "custom-mods";
            AppDataManager.CustomSdModsPath = "custom-sd-mods";
            Assert.That(AppDataManager.GetModsPath(), Is.EqualTo(Path.Combine(root, "custom-mods")));
            Assert.That(AppDataManager.GetSdModsPath(), Is.EqualTo(Path.Combine(root, "custom-sd-mods")));
            _fileSystem.AddLink(Path.Combine(root, "custom-mods"));
            _fileSystem.AddLink(Path.Combine(root, "custom-sd-mods"));
            Assert.That(() => AppDataManager.GetModsPath(), Throws.TypeOf<IOException>());
            Assert.That(() => AppDataManager.GetSdModsPath(), Throws.TypeOf<IOException>());
        }

        [TestCase("-r")]
        [TestCase("--root-data-dir")]
        public void MissingRootArgumentIsNotSilentlyIgnored(string option)
        {
            Assert.That(() => ValidateRootArguments([option]), Throws.TypeOf<ArgumentException>());
            Assert.That(() => ValidateRootArguments([option, "--fullscreen"]), Throws.TypeOf<ArgumentException>());
            Assert.That(() => ValidateRootArguments([option, ""]), Throws.TypeOf<ArgumentException>());
            Assert.That(() => ValidateRootArguments([option, " "]), Throws.TypeOf<ArgumentException>());
            Assert.That(() => ValidateRootArguments([option, null]), Throws.TypeOf<ArgumentException>());
        }

        [TestCase("--root-data-dir=")]
        [TestCase("--root-data-dir=/independent")]
        [TestCase("-r=")]
        [TestCase("-r=/independent")]
        [TestCase("-rindependent")]
        [TestCase("--root-data-directory")]
        public void EqualsAndAmbiguousRootFormsAreRejected(string option)
        {
            Assert.That(() => ValidateRootArguments([option]), Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void DuplicateAliasesAndRootsAfterTerminatorAreRejected()
        {
            string[][] invalid = [["-r", "one", "-r", "one"], ["--root-data-dir", "one", "--root-data-dir", "two"],
                ["-r", "one", "--root-data-dir", "one"], ["--root-data-dir", "one", "-r", "two"],
                ["--", "-r", "one"], ["--", "--root-data-dir", "one"], [null]];
            foreach (string[] arguments in invalid)
            {
                Assert.That(() => ValidateRootArguments(arguments), Throws.TypeOf<ArgumentException>(), string.Join(" ", arguments));
            }
        }

        [Test]
        public void ExistingRenderDocShortAliasIsNotMistakenForAnAttachedRoot()
        {
            Assert.That(() => ValidateRootArguments(["-rdct", "{titleName}"]), Throws.Nothing);
            Assert.That(() => ValidateRootArguments(["-rdct", "{titleName}", "-r", "owned-root"]), Throws.Nothing);
            Assert.That(() => ValidateRootArguments(["--root-data-dir", "owned-root", "-rdct", "{titleName}"]), Throws.Nothing);
        }

        [Test]
        public void OneSeparatedRootAndUnrelatedArgumentsAreValid()
        {
            Assert.That(() => ValidateRootArguments([]), Throws.Nothing);
            Assert.That(() => ValidateRootArguments(["--fullscreen", "-r", "root with spaces", "game.nsp"]), Throws.Nothing);
            Assert.That(() => ValidateRootArguments(["--root-data-dir", "./-relative", "--", "game.nsp"]), Throws.Nothing);
            Assert.That(() => ValidateRootArguments(null), Throws.TypeOf<ArgumentNullException>());
        }

        private string AddIndependentRoot()
        {
            string root = Path.Combine(_environment.WorkingDirectory, "independent");
            _fileSystem.AddDirectory(root);
            return root;
        }

        private void AssertProtectedBeforeInspection(string requested)
        {
            _fileSystem.Inspections.Clear();
            Assert.That(() => AppDataManager.Initialize(requested, _environment, _fileSystem), Throws.TypeOf<IOException>(), requested);
            Assert.Multiple(() =>
            {
                Assert.That(_fileSystem.Inspections, Is.Empty, "Protection cannot read legacy contents to decide if a root is protected.");
                Assert.That(_fileSystem.WriteProbes, Is.Empty);
                Assert.That(_fileSystem.Created, Is.Empty);
            });
        }

        private sealed class FixtureFileSystem : IDataRootFileSystem
        {
            private readonly Dictionary<string, PathKind> _entries = new(StringComparer.Ordinal);
            public List<string> Inspections { get; } = [];
            public List<string> Enumerated { get; } = [];
            public List<string> Created { get; } = [];
            public List<string> WriteProbes { get; } = [];
            public string Unwritable { get; set; }

            public PathKind Kind(string path) => _entries.GetValueOrDefault(Normalize(path), PathKind.Missing);
            public void AddDirectory(string path)
            {
                path = Normalize(path);
                _entries[path] = PathKind.Directory;
                string parent = Path.GetDirectoryName(path);
                if (parent != null && !_entries.ContainsKey(parent))
                {
                    AddDirectory(parent);
                }
            }
            public void AddFile(string path) { AddDirectory(Path.GetDirectoryName(path)); _entries[Normalize(path)] = PathKind.File; }
            public void AddLink(string path) { AddDirectory(Path.GetDirectoryName(path)); _entries[Normalize(path)] = PathKind.Link; }
            public PathKind GetPathKind(string path) { Inspections.Add(Normalize(path)); return Kind(path); }
            public IEnumerable<string> EnumerateEntries(string directory)
            {
                Enumerated.Add(directory);
                return _entries.Keys.Where(path => Path.GetDirectoryName(path) == directory).ToArray();
            }
            public void VerifyDirectoryWritable(string directory)
            {
                WriteProbes.Add(directory);
                if (directory == Unwritable)
                {
                    throw new UnauthorizedAccessException("Fixture directory is unwritable.");
                }
            }
            public void CreateDirectory(string directory) { Created.Add(directory); AddDirectory(directory); }
        }
    }
}
