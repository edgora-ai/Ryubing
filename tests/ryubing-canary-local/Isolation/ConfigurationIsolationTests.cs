using NUnit.Framework;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.Systems.Configuration.UI;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Input;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using static Ryujinx.Common.Configuration.RyubingCanaryLocalPolicy;

namespace Ryubing.Canary.Local.Tests.Isolation
{
    [TestFixture]
    [NonParallelizable]
    public class ConfigurationIsolationTests
    {
        private TestDirectory _fixture;
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _fixture = new TestDirectory("configuration-isolation");
            _root = _fixture.Child("data");
            Directory.CreateDirectory(_root);
            AppDataManager.Initialize(_root, FixtureEnvironment(_fixture.Path), PhysicalDataRootFileSystem.Instance);
            TestContext.WriteLine($"Retained configuration fixture: {_fixture.Path}");
        }

        [TearDown]
        public void TearDown() => _fixture.Dispose();

        [Test]
        public void SaveReadBackAndDirectJsonParsingPreserveCanary73Fields()
        {
            string path = AppDataManager.GetGlobalConfigurationPath("Config.json");
            SampleConfiguration().SaveConfig(path);
            using JsonDocument stored = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.That(ConfigurationFileFormat.TryLoad(path, out ConfigurationFileFormat readBack), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(ConfigurationFileFormat.CurrentVersion, Is.EqualTo(73));
                Assert.That(stored.RootElement.GetProperty("version").GetInt32(), Is.EqualTo(73));
                Assert.That(stored.RootElement.GetProperty("language_code").GetString(), Is.EqualTo("zh_CN"));
                Assert.That(readBack.AudioBackend, Is.EqualTo(AudioBackend.SDL3));
                Assert.That(readBack.LanguageCode, Is.EqualTo("zh_CN"));
                Assert.That(readBack.InputConfig.Single(), Is.TypeOf<StandardControllerInputConfig>());
                Assert.That(readBack.PlayerInputAssignments.Single().PlayerIndex, Is.EqualTo(PlayerIndex.Player1));
                Assert.That(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories), Is.Empty);
                Assert.That(stored.RootElement.TryGetProperty("wallpaper_path", out _), Is.False);
                Assert.That(stored.RootElement.TryGetProperty("multiplayer_lan_play_server", out _), Is.False);
            });
        }

        [Test]
        public void Original70To73MigrationsLeaveCallerAndSerializedInputUnchanged()
        {
            ConfigurationFileFormat source = SampleConfiguration(70);
            source.AudioBackend = AudioBackend.SDL2;
            source.GCLowLatency = true;
            string path = Path.Combine(_root, "input-v70.json");
            source.SaveConfig(path);
            byte[] originalBytes = File.ReadAllBytes(path);
            ConfigurationFileFormat migrated = ConfigurationState.MigrateConfiguration(source);
            StandardControllerInputConfig originalInput = (StandardControllerInputConfig)source.InputConfig.Single();
            StandardControllerInputConfig migratedInput = (StandardControllerInputConfig)migrated.InputConfig.Single();
            Assert.Multiple(() =>
            {
                Assert.That(source.Version, Is.EqualTo(70));
                Assert.That(source.AudioBackend, Is.EqualTo(AudioBackend.SDL2));
                Assert.That(source.GCLowLatency, Is.True);
                Assert.That(migrated.Version, Is.EqualTo(73));
                Assert.That(migrated.AudioBackend, Is.EqualTo(AudioBackend.SDL3));
                Assert.That(migrated.GCLowLatency, Is.False);
                Assert.That(migrated.LanguageCode, Is.EqualTo("zh_CN"));
                Assert.That(migrated.WindowStartup.WindowSizeWidth, Is.EqualTo(1280));
                Assert.That(migratedInput, Is.Not.SameAs(originalInput));
                Assert.That(migratedInput.Motion, Is.Not.SameAs(originalInput.Motion));
                Assert.That(migratedInput.ControllerType, Is.EqualTo(ControllerType.JoyconPair));
                Assert.That(migrated.PlayerInputAssignments.Single().Devices.Single().Id, Is.EqualTo("fixture"));
                Assert.That(SHA256.HashData(File.ReadAllBytes(path)), Is.EqualTo(SHA256.HashData(originalBytes)));
            });
            migratedInput.Motion.Sensitivity = 42;
            Assert.That(originalInput.Motion.Sensitivity, Is.EqualTo(100));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(originalBytes));
        }

        [TestCase(AudioBackend.OpenAl)]
        [TestCase(AudioBackend.SDL3)]
        [TestCase(AudioBackend.Dummy)]
        public void OriginalAudioMigrationChangesOnlyTheLegacySDL2Preference(AudioBackend backend)
        {
            ConfigurationFileFormat source = SampleConfiguration(70);
            source.AudioBackend = backend;
            Assert.That(ConfigurationState.MigrateConfiguration(source).AudioBackend, Is.EqualTo(backend));
        }

        [Test]
        public void LoadCreatesExactTitleSpecificBackupBeforeMigrating()
        {
            string path = AppDataManager.GetTitleConfigurationPath("0100000000000001", "Config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ConfigurationFileFormat source = SampleConfiguration(70);
            source.AudioBackend = AudioBackend.SDL2;
            source.SaveConfig(path);
            byte[] original = File.ReadAllBytes(path);
            GetState().Load(source, path);
            string backup = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "Config.json.*.v70.bak").Single();
            Assert.That(File.ReadAllBytes(backup), Is.EqualTo(original));
            Assert.That(ConfigurationFileFormat.TryLoad(path, out ConfigurationFileFormat stored), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(stored.Version, Is.EqualTo(73));
                Assert.That(stored.AudioBackend, Is.EqualTo(AudioBackend.SDL3));
                Assert.That(source.Version, Is.EqualTo(70));
                Assert.That(source.AudioBackend, Is.EqualTo(AudioBackend.SDL2));
                Assert.That(Directory.EnumerateFiles(_root, "*.bak", SearchOption.TopDirectoryOnly), Is.Empty);
            });
        }

        [Test]
        public void BackupFailurePreventsMigrationAndPreservesPreviousFile()
        {
            string path = AppDataManager.GetGlobalConfigurationPath("Config.json");
            ConfigurationFileFormat source = SampleConfiguration(70);
            source.SaveConfig(path);
            byte[] original = File.ReadAllBytes(path);
            FaultingFileSystem fileSystem = new(Failure.BackupCreation);
            Assert.That(() => GetState().Load(source, path, fileSystem: fileSystem), Throws.TypeOf<UnauthorizedAccessException>());
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
                Assert.That(source.Version, Is.EqualTo(70));
                Assert.That(fileSystem.Commits, Is.Zero);
                Assert.That(Directory.EnumerateFiles(_root, "*.bak"), Is.Empty);
            });
        }

        [TestCase(Failure.Create)]
        [TestCase(Failure.PartialWrite)]
        [TestCase(Failure.Flush)]
        [TestCase(Failure.Commit)]
        public void AtomicSaveFailureNeverTruncatesPreviousFile(Failure failure)
        {
            string path = AppDataManager.GetGlobalConfigurationPath("Config.json");
            ConfigurationFileFormat previous = SampleConfiguration();
            previous.SaveConfig(path);
            byte[] original = File.ReadAllBytes(path);
            ConfigurationFileFormat changed = previous.DeepClone();
            changed.LanguageCode = "zh_TW";
            Assert.That(() => changed.SaveConfig(path, new FaultingFileSystem(failure)), Throws.TypeOf<IOException>());
            Assert.That(ConfigurationFileFormat.TryLoad(path, out ConfigurationFileFormat stored), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
                Assert.That(stored.LanguageCode, Is.EqualTo("zh_CN"));
                Assert.That(Directory.EnumerateFiles(_root, "*.tmp"), Is.Empty);
            });
        }

        [Test]
        public void CleanupFailureReportsBothErrorsAndRetainsPreviousConfig()
        {
            string path = AppDataManager.GetGlobalConfigurationPath("Config.json");
            SampleConfiguration().SaveConfig(path);
            byte[] original = File.ReadAllBytes(path);
            AggregateException exception = Assert.Throws<AggregateException>(() => SampleConfiguration().SaveConfig(path, new FaultingFileSystem(Failure.Cleanup)))!;
            Assert.That(exception.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
            Assert.That(Directory.EnumerateFiles(_root, "*.tmp").Count(), Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingOptionalTitleConfigReturnsFalseWithoutCreatingAnything(bool missingParent)
        {
            string path = AppDataManager.GetTitleConfigurationPath("0100000000000001", "Config.json");
            string parent = Path.GetDirectoryName(path)!;
            if (!missingParent)
            {
                Directory.CreateDirectory(parent);
            }
            string[] before = Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Order().ToArray();
            Assert.That(ConfigurationFileFormat.TryLoad(path, out ConfigurationFileFormat configuration), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(configuration, Is.Null);
                Assert.That(File.Exists(path), Is.False);
                Assert.That(Directory.Exists(parent), Is.EqualTo(!missingParent));
                Assert.That(Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Order().ToArray(), Is.EqualTo(before));
            });
        }

        [TestCase(Failure.Read)]
        [TestCase(Failure.ReadIo)]
        public void UnrelatedReadFailureIsNotMisreportedAsInvalidJson(Failure failure)
        {
            string path = AppDataManager.GetGlobalConfigurationPath("Config.json");
            SampleConfiguration().SaveConfig(path);
            byte[] original = File.ReadAllBytes(path);
            Assert.That(() => ConfigurationFileFormat.TryLoad(path, new FaultingFileSystem(failure), out _),
                failure == Failure.Read ? Throws.TypeOf<UnauthorizedAccessException>() : Throws.TypeOf<IOException>());
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
        }

        [Test]
        public void InvalidJsonIsPreservedExactlyBeforeAtomicRecovery()
        {
            string path = AppDataManager.GetGlobalConfigurationPath("Config.json");
            byte[] invalid = System.Text.Encoding.UTF8.GetBytes("{ invalid json");
            File.WriteAllBytes(path, invalid);
            Assert.That(ConfigurationFileFormat.TryLoad(path, out _), Is.False);
            string backup = ConfigurationFileFormat.RenameInvalidConfigFile(path);
            Assert.That(File.ReadAllBytes(backup), Is.EqualTo(invalid));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(invalid));
            SampleConfiguration().SaveConfig(path);
            Assert.That(ConfigurationFileFormat.TryLoad(path, out _), Is.True);
            Assert.That(File.ReadAllBytes(backup), Is.EqualTo(invalid));
        }

        [Test]
        public void InvalidBackupFailurePropagatesWithoutMovingOrReplacingTheOriginal()
        {
            string path = AppDataManager.GetGlobalConfigurationPath("Config.json");
            File.WriteAllText(path, "{ broken");
            byte[] original = File.ReadAllBytes(path);
            Assert.That(() => ConfigurationFileFormat.RenameInvalidConfigFile(path, new FaultingFileSystem(Failure.Flush)), Throws.TypeOf<IOException>());
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
            Assert.That(Directory.EnumerateFiles(_root, "*.invalid"), Is.Empty);
        }

        [Test]
        public void ConfigurationReadersAndWritersRejectOutsideRootAndChildLinks()
        {
            string external = _fixture.Child("external");
            Directory.CreateDirectory(external);
            string outside = Path.Combine(external, "Config.json");
            File.WriteAllText(outside, "owned fixture sentinel");
            string link = Path.Combine(_root, "Config.json");
            File.CreateSymbolicLink(link, outside);
            Assert.That(() => SampleConfiguration().SaveConfig(link), Throws.TypeOf<IOException>());
            Assert.That(() => ConfigurationFileFormat.TryLoad(link, out _), Throws.TypeOf<IOException>());
            Assert.That(() => SampleConfiguration().SaveConfig(outside), Throws.TypeOf<IOException>());
            // Read the known fixture path directly, never through the rejected link.
            Assert.That(File.ReadAllText(outside), Is.EqualTo("owned fixture sentinel"));
        }

        [TestCase("../escape")]
        [TestCase("010000000000000Z")]
        [TestCase("0100")]
        public void TitleConfigurationRequiresASixteenHexIdentity(string title)
        {
            Assert.That(() => AppDataManager.GetTitleConfigurationPath(title, "Config.json"), Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void ProgramReloadAndGlobalSaveKeepGlobalAndTitleIdentitiesSeparate()
        {
            string global = AppDataManager.GetGlobalConfigurationPath("Config.json");
            string title = AppDataManager.GetTitleConfigurationPath("0100000000000001", "Config.json");
            SampleConfiguration().SaveConfig(global);
            Directory.CreateDirectory(Path.GetDirectoryName(title)!);
            ConfigurationFileFormat titleConfig = SampleConfiguration();
            titleConfig.LanguageCode = "zh_TW";
            titleConfig.SaveConfig(title);
            byte[] titleBytes = File.ReadAllBytes(title);
            ConfigurationState state = GetState();
            Type program = typeof(ConfigurationState).Assembly.GetType("Ryujinx.Ava.Program", true)!;
            MethodInfo reload = program.GetMethod("ReloadConfig", BindingFlags.Static | BindingFlags.Public)!;
            MethodInfo selectTitle = program.GetMethod("GetDirGameUserConfig", BindingFlags.Static | BindingFlags.Public)!;
            reload.Invoke(null, [false]);
            selectTitle.Invoke(null, ["0100000000000001", true]);
            reload.Invoke(null, [true]);
            Assert.That(program.GetProperty("GlobalConfigurationPath")!.GetValue(null), Is.EqualTo(global));
            Assert.That(program.GetProperty("ConfigurationPath")!.GetValue(null), Is.EqualTo(title));
            state.UI.LanguageCode.Value = "en_US";
            SettingsViewModel.SaveConfiguration(state, global: true);
            Assert.That(ConfigurationFileFormat.TryLoad(global, out ConfigurationFileFormat globalReadBack), Is.True);
            Assert.That(globalReadBack.LanguageCode, Is.EqualTo("en_US"));
            Assert.That(File.ReadAllBytes(title), Is.EqualTo(titleBytes));
            reload.Invoke(null, [false]);
            Assert.That(program.GetProperty("ConfigurationPath")!.GetValue(null), Is.EqualTo(global));
            Assert.That(program.GetProperty("GlobalConfigurationPath")!.GetValue(null), Is.EqualTo(global));
            Assert.That(state.UI.LanguageCode.Value, Is.EqualTo("en_US"));
        }

        internal static PathEnvironment FixtureEnvironment(string fixture) => new(
            Path.Combine(fixture, "home"), Path.Combine(fixture, "appdata"),
            Path.Combine(fixture, "app", "Ryubing Canary Local.app", "Contents", "MacOS"), fixture,
            OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());

        internal static ConfigurationState GetState()
        {
            if (ConfigurationState.Instance == null)
            {
                ConfigurationState.Initialize();
            }
            return ConfigurationState.Instance;
        }

        internal static ConfigurationFileFormat SampleConfiguration(int version = 73) => new()
        {
            Version = version,
            EnableFileLog = false,
            AudioBackend = AudioBackend.SDL3,
            AudioVolume = 0.75f,
            LanguageCode = "zh_CN",
            SystemTimeZone = "UTC",
            GraphicsBackend = GraphicsBackend.Vulkan,
            GraphicsShadersDumpPath = string.Empty,
            GameDirs = [],
            AutoloadDirs = [],
            LoggingFilteredClasses = [],
            InputConfig = [InputConfigDefaults.CreateDefaultControllerConfiguration("fixture", "Joy-Con fixture", ControllerType.JoyconPair, PlayerIndex.Player1, true)],
            PlayerInputAssignments = [new PlayerInputAssignment
            {
                PlayerIndex = PlayerIndex.Player1,
                Devices = [new AssignedInputDevice { Type = AssignedInputDeviceType.Controller, Id = "fixture" }],
            }],
            GCLowLatency = false,
            WindowStartup = new WindowStartup { WindowSizeWidth = 1280, WindowSizeHeight = 760 },
        };

        public enum Failure { Create, Read, ReadIo, PartialWrite, Flush, Commit, BackupCreation, Cleanup }

        private sealed class FaultingFileSystem(Failure failure) : ConfigurationFileFormat.IConfigurationFileSystem
        {
            private readonly ConfigurationFileFormat.IConfigurationFileSystem _physical = ConfigurationFileFormat.PhysicalConfigurationFileSystem.Instance;
            public int Commits { get; private set; }
            public Stream OpenRead(string path) => failure switch
            {
                Failure.Read => throw new UnauthorizedAccessException("Fixture read failure."),
                Failure.ReadIo => throw new IOException("Fixture unrelated read I/O failure."),
                _ => _physical.OpenRead(path),
            };
            public Stream CreateNew(string path)
            {
                if (failure == Failure.Create)
                    throw new IOException("Fixture create failure.");
                if (failure == Failure.BackupCreation && path.EndsWith(".bak", StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Fixture backup creation failure.");
                Stream stream = _physical.CreateNew(path);
                return failure == Failure.PartialWrite ? new FailingWriteStream(stream) : stream;
            }
            public void Flush(Stream stream)
            {
                if (failure == Failure.Flush)
                    throw new IOException("Fixture flush failure.");
                _physical.Flush(stream);
            }
            public void Commit(string temporaryPath, string destinationPath)
            {
                Commits++;
                if (failure is Failure.Commit or Failure.Cleanup)
                    throw new IOException("Fixture atomic replacement failure.");
                _physical.Commit(temporaryPath, destinationPath);
            }
            public void Delete(string path)
            {
                if (failure == Failure.Cleanup)
                    throw new IOException("Fixture cleanup failure.");
                _physical.Delete(path);
            }
        }

        private sealed class FailingWriteStream(Stream inner) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                inner.Write(buffer[..Math.Min(buffer.Length, 4)]);
                throw new IOException("Fixture failure after a partial write.");
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
