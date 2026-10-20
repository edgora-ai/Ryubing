using NUnit.Framework;
using Ryujinx.Ava;
using Ryujinx.Ava.Utilities;
using Ryujinx.Common.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Ryubing.Canary.Local.Tests.Packaging
{
    [TestFixture]
    [NonParallelizable]
    public class ShortcutHelperTests
    {
        private const string ApplicationId = "0100ABCDEF123456";
        private TestDirectory _fixture;

        [SetUp]
        public void SetUp() => _fixture = new TestDirectory("shortcut-packaging");

        [TearDown]
        public void TearDown() => _fixture.Dispose();

        [Test]
        public void LocalDisplayIdentityDoesNotRenameTheResourceAssembly()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RyubingCanaryLocalPolicy.DisplayName, Is.EqualTo("Ryubing Canary Local"));
                Assert.That(RyubingCanaryLocalPolicy.BundleIdentifier, Is.EqualTo("local.ryubing.RyubingCanaryLocal"));
                Assert.That(RyujinxApp.FullAppName, Does.StartWith("Ryubing Canary Local"));
                Assert.That(typeof(RyujinxApp).Assembly.GetName().Name, Is.EqualTo("Ryujinx"));
            });
        }

        [TestCase("Ryubing-Canary-Local")]
        [TestCase("portable-ryubing-canary-local")]
        [TestCase("Custom root 中文 ' \" $x `literal` ; data")]
        public void DefaultPortableAndCustomRootsAreAlwaysExplicit(string directory)
        {
            string root = _fixture.Child(directory);
            string game = _fixture.Child("game.xci");
            IReadOnlyList<string> arguments = ShortcutHelper.BuildLaunchArguments(root, game, ApplicationId);
            Assert.That(arguments, Is.EqualTo(new[] { "--root-data-dir", Path.GetFullPath(root), "--application-id", ApplicationId, game }));
        }

        [Test]
        public void NspShortcutPreservesTypedArgumentsWithoutShellExpansion()
        {
            string root = _fixture.Child("local data");
            string game = _fixture.Child("游戏 $(not-executed).nsp");
            IReadOnlyList<string> arguments = ShortcutHelper.BuildLaunchArguments(root, game, ApplicationId, ["--fullscreen", "literal '$HOME'"]);
            Assert.That(arguments, Is.EqualTo(new[] { "--root-data-dir", root, "--fullscreen", "literal '$HOME'", game }));
        }

        [TestCase("-r")]
        [TestCase("-r/another-root")]
        [TestCase("-r=/another-root")]
        [TestCase("--root-data-dir")]
        [TestCase("--root-data-dir=/another-root")]
        [TestCase("--ROOT-DATA-DIR=/another-root")]
        [TestCase("--")]
        public void ExtraArgumentsCannotRedirectTheSelectedRoot(string argument)
        {
            Assert.That(() => ShortcutHelper.BuildLaunchArguments(_fixture.Path, _fixture.Child("game.nsp"), ApplicationId, [argument]),
                Throws.TypeOf<ArgumentException>());
        }

        [TestCase("")]
        [TestCase("relative-root")]
        [TestCase(null)]
        public void ShortcutRejectsAnUnresolvedDataRoot(string root)
        {
            Assert.That(() => ShortcutHelper.BuildLaunchArguments(root, _fixture.Child("game.nsp"), ApplicationId), Throws.TypeOf<ArgumentException>());
        }

        [TestCase("../escape")]
        [TestCase("0100")]
        [TestCase("0100ABCDEF12345Z")]
        public void ApplicationIdCannotEscapeShortcutDestinations(string applicationId)
        {
            Assert.That(() => ShortcutHelper.GetMacosShortcutPath(_fixture.Path, "Game", applicationId), Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void PosixQuotingPreservesApostrophesAndShellMetacharactersAsData()
        {
            Assert.That(ShortcutHelper.QuotePosixArgument("a'b $HOME `x`;\n中文"), Is.EqualTo("'a'\"'\"'b $HOME `x`;\n中文'"));
            Assert.That(ShortcutHelper.QuotePosixArgument(string.Empty), Is.EqualTo("''"));
            Assert.That(() => ShortcutHelper.QuotePosixArgument("bad\0argument"), Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void MacosLauncherPinsLocalExecutableAndResolvedRootWithoutSysctlOrOpen()
        {
            string executable = Path.Combine(_fixture.Path, "Ryubing Canary Local.app", "Contents", "MacOS", "Ryujinx");
            string root = _fixture.Child("portable-ryubing-canary-local ' 中文");
            string game = _fixture.Child("Game $(not-executed).xci");
            string launcher = ShortcutHelper.BuildMacosLauncher(executable, ShortcutHelper.BuildLaunchArguments(root, game, ApplicationId));
            Assert.Multiple(() =>
            {
                Assert.That(launcher, Does.Contain("exec " + ShortcutHelper.QuotePosixArgument(executable)));
                Assert.That(launcher, Does.Contain("'--root-data-dir' " + ShortcutHelper.QuotePosixArgument(root)));
                Assert.That(launcher, Does.Contain(ShortcutHelper.QuotePosixArgument(game)));
                Assert.That(launcher, Does.Not.Contain("open -a").And.Not.Contain("sysctl").And.Not.Contain("/Applications/Ryujinx.app"));
            });
        }

        [Test]
        public void MacosShortcutUsesItsOwnUserNamespaceAndEscapedPlistIdentity()
        {
            const string name = "游戏 <Title> & 'quotes' \"name\"";
            string path = ShortcutHelper.GetMacosShortcutPath(_fixture.Path, name, ApplicationId);
            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(Path.Combine(_fixture.Path, "Applications", "Ryubing Canary Local Shortcuts")));
            Dictionary<string, string> values = ReadPlistStrings(XDocument.Parse(ShortcutHelper.BuildMacosShortcutPlist(name, ApplicationId)));
            Assert.Multiple(() =>
            {
                Assert.That(values["CFBundleIdentifier"], Is.EqualTo("local.ryubing.RyubingCanaryLocal.shortcut.0100abcdef123456"));
                Assert.That(values["CFBundleExecutable"], Is.EqualTo("runner.sh"));
                Assert.That(values["CFBundleName"], Is.EqualTo("Ryubing Canary Local — " + name));
                Assert.That(values.ContainsKey("CFBundleURLTypes"), Is.False);
                Assert.That(values.ContainsKey("CFBundleDocumentTypes"), Is.False);
            });
        }

        [Test]
        public void NewFileHelperNeverOverwritesUnknownContent()
        {
            string path = _fixture.Child("unknown-file");
            byte[] original = Encoding.UTF8.GetBytes("owned fixture content must survive");
            using (FileStream file = new(path, FileMode.CreateNew))
            {
                file.Write(original);
            }
            Assert.That(() => ShortcutHelper.WriteNewFile(path, Encoding.UTF8.GetBytes("replacement")), Throws.TypeOf<IOException>());
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
        }

        [Test]
        public void MacosShortcutWritesAndReadsBackPinnedRootWithoutExecutingAnything()
        {
            RequireMacos();
            string root = _fixture.Child("data root ' 中文");
            string destination = ShortcutHelper.GetMacosShortcutPath(_fixture.Path, "Game", ApplicationId);
            string launcher = ShortcutHelper.BuildMacosLauncher(_fixture.Child("Ryujinx"),
                ShortcutHelper.BuildLaunchArguments(root, _fixture.Child("game.nsp"), ApplicationId));
            string plist = ShortcutHelper.BuildMacosShortcutPlist("Game", ApplicationId);
            byte[] icon = Encoding.UTF8.GetBytes("owned icon fixture, never decoded or executed");
            ShortcutHelper.WriteMacosShortcut(destination, launcher, plist, icon, _fixture.Path);
            string runner = Path.Combine(destination, "Contents", "MacOS", "runner.sh");
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(runner), Is.EqualTo(launcher));
                Assert.That(File.ReadAllText(Path.Combine(destination, "Contents", "Info.plist")), Is.EqualTo(plist));
                Assert.That(File.ReadAllBytes(Path.Combine(destination, "Contents", "Resources", "icon.png")), Is.EqualTo(icon));
                Assert.That(File.GetUnixFileMode(runner).HasFlag(UnixFileMode.UserExecute), Is.True);
                Assert.That(Directory.EnumerateFiles(destination, "runner.sh", SearchOption.AllDirectories).Single(), Is.EqualTo(runner));
            });
            Assert.That(() => ShortcutHelper.WriteMacosShortcut(destination, "replacement", plist, icon, _fixture.Path), Throws.TypeOf<IOException>());
            Assert.That(File.ReadAllText(runner), Is.EqualTo(launcher));
        }

        [Test]
        public void ExistingMacosShortcutDirectoryAndSymlinkAreNeverReplaced()
        {
            RequireMacos();
            string destination = ShortcutHelper.GetMacosShortcutPath(_fixture.Path, "Unknown", ApplicationId);
            Directory.CreateDirectory(destination);
            string sentinel = Path.Combine(destination, "keep");
            File.WriteAllText(sentinel, "unknown bundle content");
            Assert.That(() => ShortcutHelper.WriteMacosShortcut(destination, "launcher", "plist", [], _fixture.Path), Throws.TypeOf<IOException>());
            string link = ShortcutHelper.GetMacosShortcutPath(_fixture.Path, "Linked", ApplicationId);
            Directory.CreateSymbolicLink(link, destination);
            Assert.That(() => ShortcutHelper.WriteMacosShortcut(link, "launcher", "plist", [], _fixture.Path), Throws.TypeOf<IOException>());
            Assert.That(new DirectoryInfo(link).LinkTarget, Is.EqualTo(destination));
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("unknown bundle content"));
        }

        [Test]
        public void MacosWriterRejectsDestinationsOutsideTheDedicatedNamespaceBeforeAnyWrites()
        {
            RequireMacos();
            string outside = _fixture.Child("Outside.app");
            Assert.That(() => ShortcutHelper.WriteMacosShortcut(outside, "launcher", "plist", [], _fixture.Path), Throws.TypeOf<ArgumentException>());
            Assert.That(Directory.Exists(outside), Is.False);
            Assert.That(Directory.Exists(_fixture.Child("Applications")), Is.False);
        }

        private static void RequireMacos()
        {
            if (!OperatingSystem.IsMacOS())
            {
                Assert.Ignore("The real macOS Unix-permission writer fixture requires macOS; no app is launched.");
            }
        }

        private static Dictionary<string, string> ReadPlistStrings(XDocument plist)
        {
            XElement[] fields = plist.Root!.Element("dict")!.Elements().ToArray();
            Dictionary<string, string> values = [];
            for (int index = 0; index < fields.Length; index += 2)
            {
                values.Add(fields[index].Value, fields[index + 1].Value);
            }
            return values;
        }
    }
}
