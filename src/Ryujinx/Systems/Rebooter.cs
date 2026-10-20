using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Utilities;
using Ryujinx.Common.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Systems
{
    internal static class Rebooter
    {

        public static void RebootAppWithGame(string gamePath, List<string> args)
        {
            _ = Reboot(gamePath, args);

        }

        private static async Task Reboot(string gamePath, List<string> args)
        {

            bool shouldRestart = true;

            FATaskDialog taskDialog = new()
            {
                Header = LocaleManager.Instance[LocaleKeys.RyujinxRebooter],
                SubHeader = LocaleManager.Instance[LocaleKeys.DialogRebooterMessage],
                IconSource = new FASymbolIconSource { Symbol = FASymbol.Games },
                XamlRoot = RyujinxApp.MainWindow,
            };

            if (shouldRestart)
            {
                string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;

                _ = taskDialog.ShowAsync(true);
                await Task.Delay(500);

                // Find the process name.
                string ryuName = Path.GetFileName(Environment.ProcessPath) ?? string.Empty;

                // Fallback if the executable could not be found.
                if (ryuName.Length == 0 || !Path.Exists(Path.Combine(executableDirectory, ryuName)))
                {
                    ryuName = OperatingSystem.IsWindows() ? "Ryujinx.exe" : "Ryujinx";
                }

                ProcessStartInfo processStart = new(Path.Combine(executableDirectory, ryuName))
                {
                    UseShellExecute = false,
                    WorkingDirectory = executableDirectory,
                };

                string root = AppDataManager.ValidatePathWithinRoot(AppDataManager.BaseDirPath);
                foreach (string arg in CommandLineState.BuildRestartArguments(args, root))
                {
                    processStart.ArgumentList.Add(arg);
                }

                processStart.ArgumentList.Add(gamePath);

                Process.Start(processStart);

                Environment.Exit(0);
            }
        }
    }
}
