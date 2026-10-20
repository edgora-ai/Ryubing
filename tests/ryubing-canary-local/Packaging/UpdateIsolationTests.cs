using NUnit.Framework;
using Ryujinx.Ava;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Ryubing.Canary.Local.Tests.Packaging;

[TestFixture]
[NonParallelizable]
public class UpdateIsolationTests
{
    private static Type Updater => typeof(RyujinxApp).Assembly.GetType("Ryujinx.Ava.Systems.Updater", throwOnError: true)!;

    [Test]
    public void PinnedLocalIdentityKeepsTheCanaryChannelAndNumericVersion()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReleaseInformation.IsValid, Is.True);
            Assert.That(ReleaseInformation.IsCanaryBuild, Is.True);
            Assert.That(ReleaseInformation.Version, Is.EqualTo("1.3.351"));
            Assert.That(ReleaseInformation.SourceRevision, Is.EqualTo("475615f0431b4995b03c930c5089d20f6f3570e0"));
            Assert.That(ReleaseInformation.LocalBuildMarker, Is.EqualTo("ryubing-canary-local+475615f.local1"));
            Assert.That(ReleaseInformation.ConfigName, Is.EqualTo("Config.json"));
            Assert.That(RyujinxApp.FullAppName, Is.EqualTo(RyubingCanaryLocalPolicy.DisplayName));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CanUpdateReportsDisabledWithoutNetworkOrAvalonia(bool showWarnings)
    {
        Assert.That(Invoke("CanUpdate", showWarnings), Is.False);
        string reason = (string)Updater.GetField("DisabledReason", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Assert.That(reason, Does.Contain("local developer build").And.Contain("disabled").And.Contain("manually").And.Contain("source"));
        Assert.That(reason, Does.Not.Contain("Up to date"));
        Assert.That(UpdateClient, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PublicChecksReturnNoUpdateResultWithoutInitializingTheClient(bool showVersionUpToDate)
    {
        await InvokeTask("BeginUpdateAsync", showVersionUpToDate);
        Task check = InvokeTask("CheckVersionAsync", showVersionUpToDate);
        await check;
        object optional = check.GetType().GetProperty("Result")!.GetValue(check)!;
        Assert.That(optional.GetType().GetProperty("HasValue")!.GetValue(optional), Is.False);
        Assert.That(UpdateClient, Is.Null);
    }

    [Test]
    public void DirectNetworkQueryRejectsBeforeCreatingAnUpdateClient()
    {
        Assert.ThrowsAsync<NotSupportedException>(async () => await InvokeTask("QueryLatestVersionAsync"));
        Assert.That(UpdateClient, Is.Null);
    }

    [Test]
    public void DirectDownloadRejectsBeforeTouchingItsArchiveOrTheNetwork()
    {
        using TestDirectory fixture = new("updater-download");
        string sentinel = fixture.Child("keep");
        File.WriteAllText(sentinel, "owned download fixture");
        Assert.ThrowsAsync<NotSupportedException>(async () => await InvokeTask("UpdateRyujinx", "https://untrusted.invalid/never-requested.zip"));
        Assert.That(File.ReadAllText(sentinel), Is.EqualTo("owned download fixture"));
        Assert.That(Directory.GetFileSystemEntries(fixture.Path), Is.EqualTo(new[] { sentinel }));
        Assert.That(UpdateClient, Is.Null);
    }

    [TestCaseSource(nameof(EffectEntries))]
    public void EveryWorkerInstallerAndExtractorRejectsBeforeUsingItsArguments(string method, object[] arguments)
    {
        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() => Invoke(method, arguments))!;
        Assert.That(exception.InnerException, Is.TypeOf<NotSupportedException>());
        Assert.That(exception.InnerException!.Message, Does.Contain("local developer build").And.Contain(method));
    }

    private static IEnumerable<TestCaseData> EffectEntries()
    {
        yield return new TestCaseData("ConstructHttpClient", Array.Empty<object>());
        yield return new TestCaseData("DoUpdateWithMultipleThreads", new object[] { null, "https://untrusted.invalid/", "not-written" });
        yield return new TestCaseData("DoUpdateWithSingleThreadWorker", new object[] { null, "https://untrusted.invalid/", "not-written" });
        yield return new TestCaseData("DoUpdateWithSingleThread", new object[] { null, "https://untrusted.invalid/", "not-written" });
        yield return new TestCaseData("InstallUpdate", new object[] { null, "not-opened" });
        yield return new TestCaseData("ExtractTarGzipFile", new object[] { "not-opened", "not-created" });
        yield return new TestCaseData("ExtractTarXzipFile", new object[] { "not-opened", "not-created" });
        yield return new TestCaseData("ExtractZipFile", new object[] { "not-opened", "not-created" });
        yield return new TestCaseData("Extract7ZipFile", new object[] { "not-opened", "not-created" });
        yield return new TestCaseData("EnumerateFilesToDelete", Array.Empty<object>());
        yield return new TestCaseData("MoveAllFilesOver", new object[] { "not-opened", "not-written", null });
    }

    [Test]
    public void StartupCleanupDoesNotScanOrDeleteInstallationLeftovers()
    {
        string fixtureRoot = Environment.GetEnvironmentVariable("RYUBING_CANARY_LOCAL_TEST_ROOT");
        Assert.That(fixtureRoot, Is.Not.Null.And.Not.Empty, "Use an explicit owned task-root fixture location.");
        Assert.That(Path.IsPathFullyQualified(fixtureRoot), Is.True);
        // Cleanup's installation root is the test host directory, not the selected app-data root.
        // Verify this is task-owned build output before placing a new random sentinel there.
        string testDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        string sourceRoot = Environment.GetEnvironmentVariable("RYUBING_CANARY_LOCAL_SOURCE_ROOT");
        Assert.That(sourceRoot, Is.Not.Null.And.Not.Empty);
        Assert.That(Path.IsPathFullyQualified(sourceRoot), Is.True);
        string taskRoot = Path.GetDirectoryName(Path.GetFullPath(sourceRoot))!;
        foreach (string candidate in new[] { fixtureRoot, testDirectory })
        {
            string relative = Path.GetRelativePath(taskRoot, candidate);
            Assert.That(Path.IsPathFullyQualified(relative), Is.False);
            Assert.That(relative.Split(Path.DirectorySeparatorChar)[0], Is.Not.EqualTo(".."));
        }
        string path = Path.Combine(testDirectory, $"ryubing-canary-local-cleanup-{Guid.NewGuid():N}.ryuold");
        byte[] content = Encoding.UTF8.GetBytes("retained owned fixture: startup cleanup must not delete this");
        using (FileStream file = new(path, FileMode.CreateNew))
        {
            file.Write(content);
        }

        Invoke("CleanupUpdate");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(content));
        Assert.That(Directory.GetFiles(testDirectory, Path.GetFileName(path)), Is.EqualTo(new[] { path }));
    }

    private static object UpdateClient => Updater.GetField("_updateClient", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

    private static object Invoke(string name, params object[] arguments) =>
        Updater.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);

    private static Task InvokeTask(string name, params object[] arguments) => (Task)Invoke(name, arguments)!;
}
