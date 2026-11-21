using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace TestingBot.Internal
{
    /// <summary>
    /// Everything the launcher takes from the machine it runs on.
    /// The tests replace parts of it; everyone else uses <see cref="Default"/>.
    /// </summary>
    internal sealed class TunnelEnvironment
    {
        // Where TestingBot Tunnel 5.0 and up are published with a Java runtime included.
        public const string DefaultReleasesUrl = "https://github.com/testingbot/Testingbot-Tunnel/releases";

        // Where the tunnel jar is published
        public const string DefaultJarUrl = "https://testingbot.com/tunnel/";

        public static TunnelEnvironment Default() => new TunnelEnvironment();

        /// <summary>The java on the PATH.</summary>
        public string SystemJava { get; set; } = "java";

        public Func<string, string?> GetEnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

        public string? HomeDirectory { get; set; } = TestingBotCredentials.HomeDirectory();

        public HttpMessageHandler? HttpHandler { get; set; }

        public TimeSpan DownloadIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

        public string JarUrl { get; set; } = DefaultJarUrl;

        /// <summary>The operating system and architecture, e.g. "macos-arm64". Null when there is no download with Java included for it.</summary>
        public string? BundledPlatform { get; set; } = DetectBundledPlatform(
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin"
                : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win32"
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "other",
            RuntimeInformation.OSArchitecture);

        public bool IsWindows { get; set; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>How long to wait for the tunnel to stop before it is killed.</summary>
        public TimeSpan KillGracePeriod { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>How often to look for the readyfile.</summary>
        public TimeSpan ReadyPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>The name TestingBot gives a platform in its downloads with Java included.</summary>
        public static string? DetectBundledPlatform(string platform, Architecture architecture)
        {
            var arch = architecture == Architecture.X64 ? "x64" : architecture == Architecture.Arm64 ? "arm64" : null;
            if (arch == null)
            {
                return null;
            }

            switch (platform)
            {
                case "darwin":
                    return $"macos-{arch}";
                case "linux":
                    return $"linux-{arch}";
                case "win32":
                    return arch == "x64" ? "windows-x64" : null;
                default:
                    return null;
            }
        }

        /// <summary>The file name of the download with Java included for a platform.</summary>
        public static string BundledArchiveName(string platformName)
            => $"testingbot-tunnel-{platformName}.{(platformName.StartsWith("windows", StringComparison.Ordinal) ? "zip" : "tar.gz")}";

        /// <summary>
        /// Where the tunnel is kept. The same directory as the Node.js launcher uses, so a tunnel
        /// downloaded by one is used by the other. TESTINGBOT_TUNNEL_CACHE_DIR overrides it.
        /// </summary>
        public string CacheDirectory()
        {
            var configured = GetEnvironmentVariable("TESTINGBOT_TUNNEL_CACHE_DIR");
            if (!string.IsNullOrEmpty(configured))
            {
                return configured!;
            }

            var home = HomeDirectory ?? Path.GetTempPath();
            string baseDirectory;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                baseDirectory = Path.Combine(home, "Library", "Caches");
            }
            else if (IsWindows)
            {
                baseDirectory = GetEnvironmentVariable("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local");
            }
            else
            {
                var xdg = GetEnvironmentVariable("XDG_CACHE_HOME");
                baseDirectory = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".cache") : xdg!;
            }

            return Path.Combine(baseDirectory, "testingbot-tunnel-launcher");
        }

        public string BundlesDirectory() => Path.Combine(CacheDirectory(), "bundles");

        /// <summary>Where the downloads with Java included are published. TESTINGBOT_TUNNEL_RELEASES_URL points it at a mirror.</summary>
        public string ReleasesUrl()
        {
            var configured = GetEnvironmentVariable("TESTINGBOT_TUNNEL_RELEASES_URL");
            return (string.IsNullOrEmpty(configured) ? DefaultReleasesUrl : configured!).TrimEnd('/');
        }

        public string BundledJavaPath(string directory) => Path.Combine(directory, "runtime", "bin", IsWindows ? "java.exe" : "java");

        public string BundledJarPath(string directory) => Path.Combine(directory, "lib", "testingbot-tunnel.jar");

        public bool IsBundleComplete(string directory) => File.Exists(BundledJavaPath(directory)) && File.Exists(BundledJarPath(directory));

        /// <summary>The tar that unpacks the downloads. Windows 10 and up ship bsdtar, which unpacks zip files too.</summary>
        public string Tar()
            => IsWindows
                ? Path.Combine(GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "System32", "tar.exe")
                : "tar";
    }
}
