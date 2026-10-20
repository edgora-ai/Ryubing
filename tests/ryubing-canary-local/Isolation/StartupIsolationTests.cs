using CommandLine;
using Gommon;
using NUnit.Framework;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.Utilities;
using Ryujinx.Common.Configuration;
using Ryujinx.Headless;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using static Ryujinx.Common.Configuration.RyubingCanaryLocalPolicy;

namespace Ryubing.Canary.Local.Tests.Isolation
{
    [TestFixture]
    [NonParallelizable]
    public class StartupIsolationTests
    {
        private TestDirectory _fixture;
        private string _root;
        private PathEnvironment _environment;
        private static Type ProgramType => typeof(ConfigurationState).Assembly.GetType("Ryujinx.Ava.Program", true)!;

        [SetUp]
        public void SetUp()
        {
            _fixture = new TestDirectory("startup-isolation");
            _root = _fixture.Child("data");
            Directory.CreateDirectory(_root);
            _environment = ConfigurationIsolationTests.FixtureEnvironment(_fixture.Path);
        }

        [TearDown]
        public void TearDown() => _fixture.Dispose();

        [TestCase("-r")]
        [TestCase("--root-data-dir")]
        public void GuiRootPreflightAndRestartArgumentsUseTheSameAbsoluteRoot(string option)
        {
            ProgramType.GetMethod("InitializeDataRoot", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [new[] { option, "data", "--fullscreen" }, _environment, PhysicalDataRootFileSystem.Instance]);
            Assert.Multiple(() =>
            {
                Assert.That(AppDataManager.BaseDirPath, Is.EqualTo(_root));
                Assert.That(CommandLineState.BaseDirPathArg, Is.EqualTo(_root));
                Assert.That(CommandLineState.Arguments, Is.EqualTo(new[] { "--root-data-dir", _root, "--fullscreen" }));
                Assert.That(File.Exists(Path.Combine(_root, "Config.json")), Is.False);
                Assert.That(Directory.EnumerateFiles(_root, "*.log", SearchOption.AllDirectories), Is.Empty);
            });
        }

        [TestCase("-r")]
        [TestCase("--root-data-dir")]
        public void HeadlessRootShortAndLongOptionsParseUniformly(string option)
        {
            string[] args = [option, _root, "--use-main-config", _fixture.Child("game.nsp")];
            Options parsed = Parser.Default.ParseArguments<Options>(args).MapResult(value => value,
                errors => throw new AssertionException(string.Join(",", errors.Select(error => error.Tag))));
            Assert.That(parsed.BaseDataDir, Is.EqualTo(_root));
            Assert.That(parsed.InheritConfig, Is.True);
        }

        [TestCase("-r")]
        [TestCase("--root-data-dir")]
        [TestCase("--root-data-dir=")]
        [TestCase("-r=")]
        public void MissingOrEqualsRootReturnsAnActualNonzeroMainAndHeadlessStatus(string argument)
        {
            int mainResult = (int)ProgramType.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [new[] { "--no-gui", argument }])!;
            Assert.That(mainResult, Is.Not.Zero);
            Assert.That(HeadlessRyujinx.Entrypoint([argument]), Is.Not.Zero);
            Assert.That(Directory.EnumerateFileSystemEntries(_root), Is.Empty);
        }

        [Test]
        public void RepeatedRootArgumentsAreRejectedBeforeAnyConfigOrLogger()
        {
            string[] args = ["-r", _root, "--root-data-dir", _root];
            Assert.That(() => CommandLineState.ParseArguments(args), Throws.TypeOf<ArgumentException>());
            Assert.That(HeadlessRyujinx.Entrypoint(args), Is.Not.Zero);
            Assert.That(Directory.EnumerateFileSystemEntries(_root), Is.Empty);
        }

        [Test]
        public void NonexistentRootHasANonzeroActualMainReturnWithoutFallbackWrites()
        {
            string missing = _fixture.Child("missing-data");
            int result = (int)ProgramType.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [new[] { "--no-gui", "-r", missing, _fixture.Child("game.nsp") }])!;
            Assert.That(result, Is.Not.Zero);
            Assert.That(Directory.Exists(missing), Is.False);
            Assert.That(Directory.EnumerateFileSystemEntries(_root), Is.Empty);
        }

        [Test]
        public void HeadlessInheritedConfigurationLoadsFromSelectedRootBeforeAccountsAndNativeServices()
        {
            AppDataManager.Initialize(_root, _environment, PhysicalDataRootFileSystem.Instance);
            ConfigurationFileFormat configuration = ConfigurationIsolationTests.SampleConfiguration();
            configuration.EnableKeyboard = true;
            configuration.DockedMode = false;
            configuration.SaveConfig(AppDataManager.GetGlobalConfigurationPath("Config.json"));
            string executableDirectory = _environment.ApplicationBaseDirectory;
            Directory.CreateDirectory(executableDirectory);
            string adjacent = Path.Combine(executableDirectory, "Config.json");
            File.WriteAllText(adjacent, "owned adjacent config must not become the selected authority");
            byte[] before = File.ReadAllBytes(adjacent);
            string[] args = ["-r", _root, "--use-main-config", _fixture.Child("game.nsp")];
            Options options = new() { BaseDataDir = _root, InheritConfig = true };
            HeadlessRyujinx.InitializeDataRootAndConfiguration(args, options, _environment, PhysicalDataRootFileSystem.Instance);
            Assert.Multiple(() =>
            {
                Assert.That(AppDataManager.BaseDirPath, Is.EqualTo(_root));
                Assert.That(options.BaseDataDir, Is.EqualTo(_root));
                Assert.That(options.AudioVolume, Is.EqualTo(0.75f));
                Assert.That(options.EnableKeyboard, Is.True);
                Assert.That(options.DisableDockedMode, Is.True);
                Assert.That(options.UserProfile, Is.Null, "A fresh selected root has no last-used account.");
                Assert.That(File.Exists(Path.Combine(_root, "system", "Profiles.json")), Is.False);
                Assert.That(ConfigurationState.Instance.UI.LanguageCode.Value, Is.EqualTo("zh_CN"));
                Assert.That(File.ReadAllBytes(adjacent), Is.EqualTo(before));
                Assert.That(Directory.EnumerateFiles(_root, "*.log", SearchOption.AllDirectories), Is.Empty);
            });
        }

        [Test]
        public void InvalidHeadlessRootFailsBeforeInheritedConfigurationCanRun()
        {
            string missing = _fixture.Child("missing-data");
            Options options = new() { BaseDataDir = missing, InheritConfig = true };
            Assert.That(() => HeadlessRyujinx.InitializeDataRootAndConfiguration(["-r", missing], options,
                _environment, PhysicalDataRootFileSystem.Instance), Throws.TypeOf<IOException>());
            Assert.That(Directory.Exists(missing), Is.False);
            Assert.That(Directory.EnumerateFileSystemEntries(_root), Is.Empty);
        }

        [Test]
        public void LateAccountResolutionFollowsTheCurrentlySelectedRootAndFreshRootHasNoLastUsedUser()
        {
            AppDataManager.Initialize(_root, _environment, PhysicalDataRootFileSystem.Instance);
            UserId id = new("00000000000000000000000000000001");
            ConcurrentDictionary<string, UserProfile> profiles = new();
            profiles[id.ToString()] = new UserProfile(id, "owned first-root user", [], 0);
            AccountSaveDataManager manager = new(new ConcurrentDictionary<string, UserProfile>()) { LastOpened = id };
            manager.Save(profiles);
            string firstFile = Path.Combine(_root, "system", "Profiles.json");
            byte[] firstBytes = File.ReadAllBytes(firstFile);
            Assert.That(AccountSaveDataManager.GetLastUsedUser().TryGet(out UserProfile first), Is.True);
            Assert.That(first.Name, Is.EqualTo("owned first-root user"));

            string secondRoot = _fixture.Child("second-data");
            Directory.CreateDirectory(secondRoot);
            AppDataManager.Initialize(secondRoot, _environment, PhysicalDataRootFileSystem.Instance);
            Assert.That(AccountSaveDataManager.GetLastUsedUser().TryGet(out _), Is.False);
            manager.Save(profiles);
            Assert.That(File.ReadAllBytes(Path.Combine(secondRoot, "system", "Profiles.json")), Is.EqualTo(firstBytes));
            Assert.That(File.ReadAllBytes(firstFile), Is.EqualTo(firstBytes));
        }

        [TestCase("../escape")]
        [TestCase("..\\escape")]
        [TestCase("/outside")]
        [TestCase(".")]
        [TestCase("..")]
        [TestCase("")]
        public void GuiAndHeadlessProfilePathBoundaryRejectsTraversal(string profileName)
        {
            AppDataManager.Initialize(_root, _environment, PhysicalDataRootFileSystem.Instance);
            Assert.That(() => AppDataManager.GetInputProfilePath("keyboard", profileName), Throws.TypeOf<ArgumentException>());
            Assert.That(() => AppDataManager.GetInputProfilePath("controller", profileName), Throws.TypeOf<ArgumentException>());
            ConfigurationIsolationTests.SampleConfiguration().SaveConfig(AppDataManager.GetGlobalConfigurationPath("Config.json"));
            Options options = new() { BaseDataDir = _root, InputProfile1Name = profileName };
            Assert.That(() => HeadlessRyujinx.InitializeDataRootAndConfiguration(["-r", _root], options,
                _environment, PhysicalDataRootFileSystem.Instance), Throws.TypeOf<ArgumentException>());
            Assert.That(File.Exists(_fixture.Child("escape.json")), Is.False);
        }

        [Test]
        public void ProfileFileLinksAreRejectedWithoutFollowingTheOwnedFixtureTarget()
        {
            AppDataManager.Initialize(_root, _environment, PhysicalDataRootFileSystem.Instance);
            string controller = Path.Combine(AppDataManager.ProfilesDirPath, "controller");
            Directory.CreateDirectory(controller);
            string target = _fixture.Child("outside-profile.json");
            File.WriteAllText(target, "owned sentinel");
            string link = Path.Combine(controller, "linked.json");
            File.CreateSymbolicLink(link, target);
            Assert.That(() => AppDataManager.GetInputProfilePath("controller", "linked"), Throws.TypeOf<IOException>());
            Assert.That(new FileInfo(link).LinkTarget, Is.EqualTo(target));
            Assert.That(File.ReadAllText(target), Is.EqualTo("owned sentinel"));
        }

        [Test]
        public void ExplicitHeadlessShaderOutputCannotEscapeTheSelectedRoot()
        {
            AppDataManager.Initialize(_root, _environment, PhysicalDataRootFileSystem.Instance);
            ConfigurationIsolationTests.SampleConfiguration().SaveConfig(AppDataManager.GetGlobalConfigurationPath("Config.json"));
            Options options = new() { BaseDataDir = _root, GraphicsShadersDumpPath = _fixture.Child("outside-dumps") };
            Assert.That(() => HeadlessRyujinx.InitializeDataRootAndConfiguration(["-r", _root], options,
                _environment, PhysicalDataRootFileSystem.Instance), Throws.TypeOf<IOException>());
            Assert.That(Directory.Exists(_fixture.Child("outside-dumps")), Is.False);
        }

        [Test]
        public void RestartTokensReplaceRelativeRootWithoutShellParsing()
        {
            string root = _fixture.Child("data 中文 ' $HOME");
            string[] args = CommandLineState.BuildRestartArguments(["-r", "relative-root", "--backend-threading", "off", "--fullscreen"], root);
            Assert.That(args, Is.EqualTo(new[] { "--root-data-dir", root, "--backend-threading", "off", "--fullscreen" }));
            Assert.That(() => CommandLineState.BuildRestartArguments([], "relative-root"), Throws.TypeOf<ArgumentException>());
        }
    }
}
