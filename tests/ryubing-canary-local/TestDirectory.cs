using System;
using System.IO;

namespace Ryubing.Canary.Local.Tests
{
    /// <summary>
    /// Every fixture is retained inside the explicit owned test root, including failed cases.
    /// Disposal never cleans earlier test evidence or follows links.
    /// </summary>
    public sealed class TestDirectory : IDisposable
    {
        public string Path { get; }
        public string Root => Path;
        public string DirectoryPath => Path;

        public TestDirectory(string prefix = "fixture")
        {
            string root = Environment.GetEnvironmentVariable("RYUBING_CANARY_LOCAL_TEST_ROOT");
            if (string.IsNullOrWhiteSpace(root) || !System.IO.Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            {
                throw new InvalidOperationException("An existing, task-owned RYUBING_CANARY_LOCAL_TEST_ROOT is required.");
            }
            if (string.IsNullOrWhiteSpace(prefix) || prefix.Contains('/') || prefix.Contains('\\') || prefix is "." or "..")
            {
                throw new ArgumentException("A single fixture name is required.", nameof(prefix));
            }

            Path = System.IO.Path.Combine(root, prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Child(params string[] components)
        {
            string candidate = Path;
            foreach (string component in components)
            {
                if (string.IsNullOrEmpty(component) || System.IO.Path.IsPathRooted(component) ||
                    component is "." or ".." || component.Contains('/') || component.Contains('\\'))
                {
                    throw new ArgumentException("A child component must be a single relative name.", nameof(components));
                }
                candidate = System.IO.Path.Combine(candidate, component);
            }
            return candidate;
        }

        public void Dispose()
        {
            // Fixtures are intentionally retained for readback and failed-attempt investigation.
            GC.SuppressFinalize(this);
        }
    }
}
