using Ryujinx.Common.Logging;
using Silk.NET.Core.Loader;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Vulkan.MoltenVK
{
    [SupportedOSPlatform("macos")]
    public static partial class MVKInitialization
    {
        private const string VulkanLib = "libvulkan.dylib";

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkGetMoltenVKConfigurationMVK(nint unusedInstance, out MVKConfiguration config, in nint configSize);

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkSetMoltenVKConfigurationMVK(nint unusedInstance, in MVKConfiguration config, in nint configSize);

        public static void Initialize()
        {
            nint configSize = (nint)Marshal.SizeOf<MVKConfiguration>();

            Result getResult = vkGetMoltenVKConfigurationMVK(nint.Zero, out MVKConfiguration config, configSize);

            if (getResult != Result.Success)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[MoltenVK] vkGetMoltenVKConfigurationMVK failed with {getResult}; keeping the default configuration.");
                return;
            }

            config.UseMetalArgumentBuffers = true;

            config.SemaphoreSupportStyle = MVKVkSemaphoreSupportStyle.MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE_SINGLE_QUEUE;
            config.SynchronousQueueSubmits = false;

            config.ResumeLostDevice = true;

            Result setResult = vkSetMoltenVKConfigurationMVK(nint.Zero, config, configSize);

            if (setResult != Result.Success)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[MoltenVK] vkSetMoltenVKConfigurationMVK failed with {setResult}; configuration was not applied.");
            }
        }

        private static string[] Resolver(string path)
        {
            if (path.EndsWith(VulkanLib))
            {
                path = path[..^VulkanLib.Length] + "libMoltenVK.dylib";
                return [path];
            }

            return [];
        }

        public static void InitializeResolver()
        {
            ((DefaultPathResolver)PathResolver.Default).Resolvers.Insert(0, Resolver);
        }
    }
}
