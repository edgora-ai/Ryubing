using Gommon;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Ava.Utilities
{
    public static class CommandLineState
    {
        public static string[] Arguments { get; private set; }
        public static int CountArguments { get; private set; }
        public static bool? OverrideDockedMode { get; private set; }
        public static bool? OverrideHardwareAcceleration { get; private set; }
        public static string OverrideGraphicsBackend { get; private set; }
        public static string OverrideBackendThreading { get; private set; }
        public static string OverrideBackendThreadingAfterReboot { get; private set; }
        public static string OverridePPTC { get; private set; }
        public static string OverrideMemoryManagerMode { get; private set; }
        public static string OverrideSystemRegion { get; private set; }
        public static string OverrideSystemLanguage { get; private set; }
        public static string OverrideHideCursor { get; private set; }
        public static string BaseDirPathArg { get; private set; }

        public static string RenderDocCaptureTitleFormat { get; private set; } =
            "{EmuVersion}\n{GuestName} {GuestVersion} {GuestTitleId} {GuestArch}";
        public static Optional<FilePath> FirmwareToInstallPathArg { get; set; }
        public static string Profile { get; private set; }
        public static string LaunchPathArg { get; private set; }
        public static string LaunchApplicationId { get; private set; }
        public static bool StartFullscreenArg { get; private set; }
        public static bool HideAvailableUpdates { get; private set; }
        public static bool OnlyLocalAmiibo { get; private set; }

        public static string GetRequestedDataRoot(string[] args)
        {
            RyubingCanaryLocalPolicy.ValidateRootArguments(args);
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index] is "-r" or "--root-data-dir")
                {
                    return args[index + 1];
                }
            }
            return null;
        }

        public static string[] BuildRestartArguments(IEnumerable<string> arguments, string resolvedRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resolvedRoot);
            if (!System.IO.Path.IsPathFullyQualified(resolvedRoot))
            {
                throw new ArgumentException("Restarts require the resolved absolute data root.", nameof(resolvedRoot));
            }
            string[] source = [.. arguments];
            RyubingCanaryLocalPolicy.ValidateRootArguments(source);
            List<string> result = ["--root-data-dir", RyubingCanaryLocalPolicy.Normalize(resolvedRoot)];
            for (int index = 0; index < source.Length; index++)
            {
                if (source[index] is "-r" or "--root-data-dir")
                {
                    index++;
                    continue;
                }
                result.Add(source[index]);
            }
            return result.ToArray();
        }

        public static void PinResolvedDataRoot()
        {
            string root = AppDataManager.ValidatePathWithinRoot(AppDataManager.BaseDirPath);
            Arguments = BuildRestartArguments(Arguments ?? [], root);
            BaseDirPathArg = root;
        }

        public static void ParseArguments(string[] args)
        {
            RyubingCanaryLocalPolicy.ValidateRootArguments(args);
            CountArguments = 0;
            BaseDirPathArg = null;
            OverrideDockedMode = null;
            OverrideHardwareAcceleration = null;
            OverrideGraphicsBackend = null;
            OverrideBackendThreading = null;
            OverrideBackendThreadingAfterReboot = null;
            OverridePPTC = null;
            OverrideMemoryManagerMode = null;
            OverrideSystemRegion = null;
            OverrideSystemLanguage = null;
            OverrideHideCursor = null;
            FirmwareToInstallPathArg = default;
            Profile = null;
            LaunchPathArg = null;
            LaunchApplicationId = null;
            StartFullscreenArg = false;
            HideAvailableUpdates = false;
            OnlyLocalAmiibo = false;
            List<string> arguments = [];

            // Parse Arguments.
            for (int i = 0; i < args.Length; ++i)
            {
                string arg = args[i];

                if (arg.Contains('-') || arg.Contains("--"))
                {
                    CountArguments++;
                }

                switch (arg)
                {
                    case "-r":
                    case "--root-data-dir":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        BaseDirPathArg = args[++i];

                        arguments.Add(arg);
                        arguments.Add(args[i]);
                        break;
                    case "-rdct":
                    case "--rd-capture-title-format":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        RenderDocCaptureTitleFormat = args[++i];

                        arguments.Add(arg);
                        arguments.Add(args[i]);
                        break;
                    case "--install-firmware":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        FirmwareToInstallPathArg = new FilePath(args[++i]);

                        arguments.Add(arg);
                        arguments.Add(args[i]);
                        break;
                    case "-p":
                    case "--profile":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        Profile = args[++i];

                        arguments.Add(arg);
                        arguments.Add(args[i]);
                        break;
                    case "-f":
                    case "--fullscreen":
                        StartFullscreenArg = true;

                        arguments.Add(arg);
                        break;
                    case "-g":
                    case "--graphics-backend":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        OverrideGraphicsBackend = args[++i];
                        break;
                    case "--backend-threading":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        OverrideBackendThreading = args[++i];
                        break;
                    case "--bt":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        OverrideBackendThreadingAfterReboot = args[++i];
                        break;
                    case "--pptc":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        OverridePPTC = args[++i];
                        break;
                    case "-la":
                    case "--local-only-amiibo":
                        OnlyLocalAmiibo = true;
                        break;
                    case "-m":
                    case "--memory-manager-mode":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        OverrideMemoryManagerMode = args[++i];
                        break;
                    case "--system-region":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        OverrideSystemRegion = args[++i];
                        break;
                    case "--system-language":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        OverrideSystemLanguage = args[++i];
                        break;
                    case "-i":
                    case "--application-id":
                        LaunchApplicationId = args[++i];
                        break;
                    case "--docked-mode":
                        OverrideDockedMode = true;
                        break;
                    case "--handheld-mode":
                        OverrideDockedMode = false;
                        break;
                    case "--hide-cursor":
                        if (i + 1 >= args.Length)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Invalid option '{arg}'");

                            continue;
                        }

                        OverrideHideCursor = args[++i];
                        break;
                    case "--hide-updates":
                        HideAvailableUpdates = true;
                        break;
                    case "--software-gui":
                        OverrideHardwareAcceleration = false;
                        break;
                    default:
                        LaunchPathArg = arg;
                        break;
                }
            }

            Arguments = arguments.ToArray();
        }
    }
}
