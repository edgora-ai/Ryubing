using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging.Formatters;
using System;
using System.IO;

namespace Ryujinx.Common.Logging.Targets
{
    public class FileLogTarget : ILogTarget
    {
        private readonly StreamWriter _logWriter;
        private readonly DefaultLogFormatter _formatter;
        private readonly string _name;

        string ILogTarget.Name { get => _name; }

        public FileLogTarget(string name, FileStream fileStream)
        {
            _name = name;
            _logWriter = new StreamWriter(fileStream);
            _formatter = new DefaultLogFormatter();
        }

        public static FileStream PrepareLogFile(string path)
        {
            string selectedLogs = AppDataManager.ValidatePathWithinRoot(AppDataManager.LogsDirPath);
            path = AppDataManager.ValidatePathWithinRoot(path);
            if (!string.Equals(path, selectedLogs, OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new IOException("File logging is restricted to the selected independent data directory's Logs folder.");
            }

            selectedLogs = AppDataManager.GetOrCreateLogsDir();
            // Retain earlier runs rather than inspecting or deleting legacy logs. A unique exclusive leaf
            // cannot append through a planted link or overwrite another run, and there is no shared fallback.
            path = AppDataManager.ValidatePathWithinRoot(Path.Combine(selectedLogs,
                $"{RyubingCanaryLocalPolicy.DataDirectoryName}_{ReleaseInformation.Version}_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}.log"));
            try
            {
                FileStreamOptions options = new()
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                return new FileStream(path, options);
            }
            catch (UnauthorizedAccessException exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"Log file could not be created '{path}': {exception}");

                return null;
            }
            catch (IOException exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"Log file could not be created '{path}': {exception}");

                return null;
            }
        }

        public void Log(object sender, LogEventArgs args)
        {
            _logWriter.WriteLine(_formatter.Format(args));
            _logWriter.Flush();
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _logWriter.WriteLine("---- End of Log ----");
            _logWriter.Flush();
            _logWriter.Dispose();
        }
    }
}
