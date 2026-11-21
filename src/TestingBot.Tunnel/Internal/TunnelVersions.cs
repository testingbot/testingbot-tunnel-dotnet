using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TestingBot.Internal
{
    /// <summary>What each tunnel version needs and knows about.</summary>
    internal static class TunnelVersions
    {
        public const int MinJavaVersion = 11;

        // TestingBot Tunnel 5.0 and up are built for Java 17
        public const int MinJavaVersion5 = 17;

        public const int MaxSshConnections = 8;

        // Options the tunnel only knows about from version 5.0 on, as spelled on its command line
        private static readonly HashSet<string> Version5Options = new HashSet<string>(StringComparer.Ordinal)
        {
            "allow-hosts", "bind-address", "cacert-file", "config", "connect-to",
            "dns-round-robin", "dns-timeout", "header", "response-header",
            "http-dial-timeout", "http-idle-timeout", "krb5-hosts", "krb5-keytab",
            "krb5-principal", "localhost-policy", "log-format", "log-http", "log-level",
            "request-id-header", "nobump-domains", "pac-local", "pac-local-sha256",
            "proxy-auth-scheme", "proxy-spn", "proxy-testingbot", "proxy-testingbot-userpwd",
            "ssh-host-key", "ssh-host-key-policy", "ws-proxy-mode",
        };

        // Options the tunnel only knows about from version 5.1 on
        private static readonly HashSet<string> Version51Options = new HashSet<string>(StringComparer.Ordinal) { "ssh-connections" };

        private static readonly Regex LeadingVersion = new Regex(@"^\s*(\d+(?:\.\d+)*)", RegexOptions.CultureInvariant);
        private static readonly Regex JavaVersion = new Regex("version \"(\\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex JarVersion = new Regex(@"Version: \S+ (\d+(?:\.\d+)*)", RegexOptions.CultureInvariant);

        /// <summary>The first tunnel version that knows about an option, null when every version does.</summary>
        public static string? MinimumTunnelVersion(string optionName)
        {
            if (Version51Options.Contains(optionName))
            {
                return "5.1";
            }
            if (Version5Options.Contains(optionName))
            {
                return "5.0";
            }
            return null;
        }

        /// <summary>Below 0 when a is older than b, above 0 when newer, null when either is not a version.</summary>
        public static int? CompareVersions(string? a, string? b)
        {
            var left = Parse(a);
            var right = Parse(b);
            if (left == null || right == null)
            {
                return null;
            }

            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var difference = (i < left.Length ? left[i] : 0) - (i < right.Length ? right[i] : 0);
                if (difference != 0)
                {
                    return difference;
                }
            }
            return 0;
        }

        private static int[]? Parse(string? version)
        {
            if (version == null)
            {
                return null;
            }
            var match = LeadingVersion.Match(version);
            return match.Success
                ? match.Groups[1].Value.Split('.').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray()
                : null;
        }

        /// <summary>Whether a tunnel version is 5.0 or newer, null when it is not known.</summary>
        public static bool? IsVersion5OrUp(string? tunnelVersion)
        {
            if (tunnelVersion == null)
            {
                return null;
            }
            var match = Regex.Match(tunnelVersion, @"^\s*(\d+)");
            if (!match.Success)
            {
                return null;
            }
            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) >= 5;
        }

        /// <summary>The Java version a tunnel version needs.</summary>
        public static int RequiredJavaVersion(string? tunnelVersion)
            => IsVersion5OrUp(tunnelVersion) == true ? MinJavaVersion5 : MinJavaVersion;

        /// <summary>The major version in the output of java -version, null when there is none.</summary>
        public static int? ParseJavaVersion(string versionOutput)
        {
            var match = JavaVersion.Match(versionOutput ?? string.Empty);
            return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
        }

        /// <summary>Check the output of java -version against the Java version the tunnel needs.</summary>
        /// <returns>The error, null when the Java is recent enough.</returns>
        public static (int? Version, string? Error) ValidateJavaVersion(string versionOutput, int minVersion)
        {
            var version = ParseJavaVersion(versionOutput);
            if (version == null)
            {
                return (null, $"Could not determine Java version. Please ensure Java {minVersion} or higher is installed for testingbot-tunnel.");
            }
            if (version < minVersion)
            {
                return (version, $"Java {version} is installed, but Java {minVersion} or higher is required for testingbot-tunnel.");
            }
            return (version, null);
        }

        /// <summary>The tunnel version in the output of java -jar testingbot-tunnel.jar --version.</summary>
        public static string? ParseJarVersion(string output)
        {
            var match = JarVersion.Match(output ?? string.Empty);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Refuse options the tunnel does not know about yet. An older tunnel would refuse
        /// to start with them, with a less helpful message. Nothing is refused when the version is not known.
        /// </summary>
        public static void ValidateOptionsForVersion(TunnelOptions options, string? tunnelVersion)
        {
            if (IsVersion5OrUp(tunnelVersion) == null)
            {
                return;
            }

            foreach (var argument in options.TunnelArguments())
            {
                var minimum = MinimumTunnelVersion(argument.Name);
                if (minimum != null && CompareVersions(tunnelVersion, minimum) < 0)
                {
                    throw new TunnelException($"{argument.Property} requires TestingBot Tunnel {minimum} or higher, but version {tunnelVersion} is used. Set TunnelVersion to \"{minimum}\" or higher.");
                }
            }
        }

        /// <summary>Validate the options, before anything is downloaded.</summary>
        public static void ValidateOptions(TunnelOptions options)
        {
            if (options.ApiKey != null && options.ApiKey.Trim().Length == 0)
            {
                throw new ArgumentException("ApiKey cannot be empty");
            }
            if (options.ApiSecret != null && options.ApiSecret.Trim().Length == 0)
            {
                throw new ArgumentException("ApiSecret cannot be empty");
            }
            if (options.TunnelVersion != null && options.TunnelVersion.Trim().Length == 0)
            {
                throw new ArgumentException("TunnelVersion cannot be empty");
            }
            if (options.StartTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentException("StartTimeout must be positive");
            }
            if (!Enum.IsDefined(typeof(TunnelJavaRuntime), options.JavaRuntime))
            {
                throw new ArgumentException("JavaRuntime must be one of Auto, System, Bundled");
            }
            if (options.SshConnections.HasValue && (options.SshConnections < 1 || options.SshConnections > MaxSshConnections))
            {
                throw new ArgumentException($"SshConnections must be a whole number from 1 to {MaxSshConnections}");
            }
            foreach (var (name, port) in new[] { (nameof(options.SePort), options.SePort), (nameof(options.LocalProxy), options.LocalProxy), (nameof(options.HubPort), options.HubPort), (nameof(options.MetricsPort), options.MetricsPort) })
            {
                if (port.HasValue && (port < 1 || port > 65535))
                {
                    throw new ArgumentException($"{name} must be a port number from 1 to 65535");
                }
            }
        }
    }
}
