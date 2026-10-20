using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;

namespace Ryubing.Canary.Local.Tests.Packaging;

[TestFixture]
[NonParallelizable]
public class PackagingCliTests
{
    private TestDirectory _fixture;
    private string _source;
    private string _publish;
    private string _output;

    [SetUp]
    public void SetUp()
    {
        _fixture = new TestDirectory("packaging-cli");
        _source = Environment.GetEnvironmentVariable("RYUBING_CANARY_LOCAL_SOURCE_ROOT");
        Assert.That(_source, Is.Not.Null.And.Not.Empty, "Set RYUBING_CANARY_LOCAL_SOURCE_ROOT to the new checkout.");
        Assert.That(Path.IsPathFullyQualified(_source), Is.True);
        Assert.That(File.Exists(Path.Combine(_source, "distribution", "macos", "create_local_app_bundle.py")), Is.True);
        _publish = _fixture.Child("publish");
        _output = _fixture.Child("new-output");
        Directory.CreateDirectory(_publish);
    }

    [Test]
    public void ExistingUnknownOutputIsRefusedBeforeAnyPackagingOrSigning()
    {
        Directory.CreateDirectory(_output);
        string sentinel = Path.Combine(_output, "unknown-content");
        File.WriteAllText(sentinel, "must be retained");
        var result = RunPackager();
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Not.EqualTo(0));
            Assert.That(result.Error, Does.Contain("must not exist"));
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("must be retained"));
            Assert.That(Directory.GetFiles(_output), Is.EqualTo(new[] { sentinel }));
        });
    }

    [Test]
    public void AnOfficialBundleIdCannotBeRequestedFromTheLocalPackager()
    {
        var result = RunPackager("--bundle-identifier", "org.ryujinx.Ryujinx");
        Assert.That(result.ExitCode, Is.Not.EqualTo(0));
        Assert.That(result.Error, Does.Contain("Local bundle identity"));
        Assert.That(Directory.Exists(_output), Is.False);
    }

    [Test]
    public void ThePackagerCannotBeRepointedAtAnotherSourceRevision()
    {
        var result = RunPackager("--revision", "0000000000000000000000000000000000000000");
        Assert.That(result.ExitCode, Is.Not.EqualTo(0));
        Assert.That(result.Error, Does.Contain("pinned to"));
        Assert.That(Directory.Exists(_output), Is.False);
    }

    [Test]
    public void AReadOnlyPreflightRejectsMissingPublishInputsWithoutCreatingOutput()
    {
        var result = RunPackager();
        Assert.That(result.ExitCode, Is.Not.EqualTo(0));
        Assert.That(result.Error, Does.Contain("executable Ryujinx is missing"));
        Assert.That(Directory.Exists(_output), Is.False);
        Assert.That(Directory.GetFileSystemEntries(_publish), Is.Empty);
    }

    [Test]
    public void ADirectUpdaterScriptInvocationCannotReplaceThePassedInstallation()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Ignore("The direct macOS updater shell refusal fixture requires /bin/bash.");
        }

        string installed = _fixture.Child("owned-existing-installation");
        string incoming = _fixture.Child("owned-incoming-installation");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(incoming);
        string sentinel = Path.Combine(installed, "keep");
        File.WriteAllText(sentinel, "unknown installation fixture");
        var result = Run("/bin/bash", Path.Combine(_source, "distribution", "macos", "updater.sh"), installed, incoming, "0");
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(78));
            Assert.That(result.Error, Does.Contain("automatic installation replacement is disabled"));
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("unknown installation fixture"));
            Assert.That(Directory.GetFileSystemEntries(installed), Is.EqualTo(new[] { sentinel }));
            Assert.That(Directory.Exists(incoming), Is.True);
        });
    }

    private (int ExitCode, string Output, string Error) RunPackager(params string[] extra)
    {
        string python = Environment.GetEnvironmentVariable("RYUBING_CANARY_LOCAL_PYTHON") ?? "python3";
        string[] arguments = ["-B", Path.Combine(_source, "distribution", "macos", "create_local_app_bundle.py"),
            _publish, _output, Path.Combine(_source, "distribution", "macos", "entitlements.xml"),
            "--source-root", _source, "--version", "1.3.351", "--revision", "475615f0431b4995b03c930c5089d20f6f3570e0", "--validate-only", .. extra];
        return Run(python, arguments);
    }

    private (int ExitCode, string Output, string Error) Run(string executable, params string[] arguments)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = _fixture.Path,
        };
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("Owned offline packaging fixture exceeded its 10-second limit.");
        }

        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        File.WriteAllText(_fixture.Child("stdout.txt"), output);
        File.WriteAllText(_fixture.Child("stderr.txt"), error);
        return (process.ExitCode, output, error);
    }
}
