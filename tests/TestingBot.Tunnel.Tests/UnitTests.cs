using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TestingBot.Internal;
using Xunit;

namespace TestingBot.Tests
{
    public class CredentialsTests
    {
        private static Func<string, string?> Env(Dictionary<string, string?> variables) => name => variables.TryGetValue(name, out var value) ? value : null;

        [Fact]
        public void UsesTheKeyAndSecretGiven()
        {
            var credentials = TestingBotCredentials.Resolve("key", "secret", Env(new() { ["TB_KEY"] = "other", ["TB_SECRET"] = "other" }), null)!;
            Assert.Equal("key", credentials.Key);
            Assert.Equal("secret", credentials.Secret);
            Assert.Equal("options", credentials.Source);
        }

        [Fact]
        public void PrefersTbKeyOverTestingbotKey()
        {
            var credentials = TestingBotCredentials.Resolve(null, null, Env(new()
            {
                ["TB_KEY"] = "tb", ["TB_SECRET"] = "tb-secret",
                ["TESTINGBOT_KEY"] = "testingbot", ["TESTINGBOT_SECRET"] = "testingbot-secret",
            }), null)!;
            Assert.Equal("tb", credentials.Key);
            Assert.Equal("TB_KEY/TB_SECRET", credentials.Source);
        }

        [Fact]
        public void FallsBackToTestingbotKey()
        {
            var credentials = TestingBotCredentials.Resolve(null, null, Env(new() { ["TB_KEY"] = "only-a-key", ["TESTINGBOT_KEY"] = "k", ["TESTINGBOT_SECRET"] = "s" }), null)!;
            Assert.Equal("k", credentials.Key);
            Assert.Equal("TESTINGBOT_KEY/TESTINGBOT_SECRET", credentials.Source);
        }

        [Fact]
        public void FallsBackToTheTestingbotFile()
        {
            using var home = new TempDirectory();
            File.WriteAllText(home.Combine(".testingbot"), "file-key:file-secret\n");
            var credentials = TestingBotCredentials.Resolve(null, null, Env(new()), home.Path)!;
            Assert.Equal("file-key", credentials.Key);
            Assert.Equal("file-secret", credentials.Secret);
            Assert.Equal("~/.testingbot", credentials.Source);
        }

        [Fact]
        public void ReturnsNullWithoutCredentials()
        {
            using var home = new TempDirectory();
            Assert.Null(TestingBotCredentials.Resolve(null, null, Env(new()), home.Path));
            File.WriteAllText(home.Combine(".testingbot"), "no separator");
            Assert.Null(TestingBotCredentials.Resolve(null, null, Env(new()), home.Path));
        }

        [Fact]
        public void RefusesHalfOrEmptyCredentials()
        {
            Assert.Equal("ApiKey and ApiSecret must be given together", Assert.Throws<ArgumentException>(() => TestingBotCredentials.Resolve("key", null, Env(new()), null)).Message);
            Assert.Equal("ApiKey cannot be empty", Assert.Throws<ArgumentException>(() => TestingBotCredentials.Resolve(" ", "secret", Env(new()), null)).Message);
            Assert.Equal("ApiSecret cannot be empty", Assert.Throws<ArgumentException>(() => TestingBotCredentials.Resolve("key", "", Env(new()), null)).Message);
        }

        [Fact]
        public void DoesNotShowTheSecret()
        {
            Assert.Equal("key:***", new TestingBotCredentials("key", "secret").ToString());
        }
    }

    public class VersionTests
    {
        [Theory]
        [InlineData("4.9", "5.0", -1)]
        [InlineData("5.1", "5.0", 1)]
        [InlineData("5.0", "5", 0)]
        [InlineData("5.10", "5.9", 1)]
        public void ComparesVersions(string a, string b, int sign)
        {
            Assert.Equal(sign, Math.Sign(TunnelVersions.CompareVersions(a, b)!.Value));
        }

        [Fact]
        public void CanNotCompareWhatIsNotAVersion()
        {
            Assert.Null(TunnelVersions.CompareVersions("latest", "5.0"));
            Assert.Null(TunnelVersions.CompareVersions(null, "5.0"));
        }

        [Fact]
        public void KnowsVersion5()
        {
            Assert.True(TunnelVersions.IsVersion5OrUp("5.0"));
            Assert.True(TunnelVersions.IsVersion5OrUp("10.1"));
            Assert.False(TunnelVersions.IsVersion5OrUp("4.9"));
            Assert.Null(TunnelVersions.IsVersion5OrUp(null));
            Assert.Null(TunnelVersions.IsVersion5OrUp("latest"));
        }

        [Fact]
        public void NeedsJava17ForTunnel5AndJava11Before()
        {
            Assert.Equal(17, TunnelVersions.RequiredJavaVersion("5.1"));
            Assert.Equal(11, TunnelVersions.RequiredJavaVersion("4.9"));
            Assert.Equal(11, TunnelVersions.RequiredJavaVersion(null));
        }

        [Theory]
        [InlineData("java version \"1.8.0_292\"", 1)]
        [InlineData("openjdk version \"11.0.11\" 2021-04-20", 11)]
        [InlineData("openjdk version \"17.0.1\" 2021-10-19", 17)]
        [InlineData("openjdk version \"23\" 2024-09-17", 23)]
        public void ParsesJavaVersions(string output, int version)
        {
            Assert.Equal(version, TunnelVersions.ParseJavaVersion(output));
        }

        [Fact]
        public void ValidatesTheJavaVersion()
        {
            Assert.Equal("Java 11 is installed, but Java 17 or higher is required for testingbot-tunnel.", TunnelVersions.ValidateJavaVersion("openjdk version \"11.0.2\"", 17).Error);
            Assert.Null(TunnelVersions.ValidateJavaVersion("openjdk version \"21.0.2\"", 17).Error);
            var unknown = TunnelVersions.ValidateJavaVersion("garbage", 17);
            Assert.Null(unknown.Version);
            Assert.Equal("Could not determine Java version. Please ensure Java 17 or higher is installed for testingbot-tunnel.", unknown.Error);
        }

        [Fact]
        public void ReadsTheJarVersion()
        {
            Assert.Equal("5.2", TunnelVersions.ParseJarVersion("Version: testingbot-tunnel.jar 5.2\n"));
            Assert.Null(TunnelVersions.ParseJarVersion("Usage: ..."));
        }

        [Fact]
        public void KnowsWhichVersionAddedAnOption()
        {
            Assert.Equal("5.0", TunnelVersions.MinimumTunnelVersion("bind-address"));
            Assert.Equal("5.1", TunnelVersions.MinimumTunnelVersion("ssh-connections"));
            Assert.Null(TunnelVersions.MinimumTunnelVersion("se-port"));
        }

        [Fact]
        public void RefusesOptionsTheTunnelVersionDoesNotKnow()
        {
            var error = Assert.Throws<TunnelException>(() => TunnelVersions.ValidateOptionsForVersion(new TunnelOptions { BindAddress = "0.0.0.0" }, "4.9"));
            Assert.Equal("BindAddress requires TestingBot Tunnel 5.0 or higher, but version 4.9 is used. Set TunnelVersion to \"5.0\" or higher.", error.Message);

            error = Assert.Throws<TunnelException>(() => TunnelVersions.ValidateOptionsForVersion(new TunnelOptions { SshConnections = 2 }, "5.0"));
            Assert.Equal("SshConnections requires TestingBot Tunnel 5.1 or higher, but version 5.0 is used. Set TunnelVersion to \"5.1\" or higher.", error.Message);

            TunnelVersions.ValidateOptionsForVersion(new TunnelOptions { SshConnections = 2, BindAddress = "0.0.0.0" }, "5.1");
            TunnelVersions.ValidateOptionsForVersion(new TunnelOptions { BindAddress = "0.0.0.0" }, null);
        }

        [Fact]
        public void ValidatesOptions()
        {
            Assert.Equal("SshConnections must be a whole number from 1 to 8", Assert.Throws<ArgumentException>(() => TunnelVersions.ValidateOptions(new TunnelOptions { SshConnections = 9 })).Message);
            Assert.Equal("StartTimeout must be positive", Assert.Throws<ArgumentException>(() => TunnelVersions.ValidateOptions(new TunnelOptions { StartTimeout = TimeSpan.Zero })).Message);
            Assert.Equal("ApiKey cannot be empty", Assert.Throws<ArgumentException>(() => TunnelVersions.ValidateOptions(new TunnelOptions { ApiKey = "" })).Message);
            Assert.Equal("SePort must be a port number from 1 to 65535", Assert.Throws<ArgumentException>(() => TunnelVersions.ValidateOptions(new TunnelOptions { SePort = 0 })).Message);
            Assert.Equal("JavaRuntime must be one of Auto, System, Bundled", Assert.Throws<ArgumentException>(() => TunnelVersions.ValidateOptions(new TunnelOptions { JavaRuntime = (TunnelJavaRuntime)7 })).Message);
            TunnelVersions.ValidateOptions(new TunnelOptions { SshConnections = 8, SePort = 4445 });
        }
    }

    public class OutputTests
    {
        [Fact]
        public void RecognisesWrongCredentials()
        {
            Assert.Equal("Invalid credentials. Please supply the correct key/secret obtained from TestingBot.com", TunnelOutput.ClassifyTunnelError("An error ocurred: 401 Unauthorized"));
        }

        [Fact]
        public void RecognisesAnAccountWithoutMinutes()
        {
            Assert.Equal("You do not have any minutes left. Please upgrade your account at TestingBot.com", TunnelOutput.ClassifyTunnelError("You have no minutes left"));
        }

        [Fact]
        public void PassesOnWhatTheApiRefuses()
        {
            Assert.Equal("You already have 2 tunnels active - please close another tunnel first", TunnelOutput.ClassifyTunnelError("An error ocurred: You already have 2 tunnels active - please close another tunnel first"));
            Assert.Equal("You already have 2 tunnels active", TunnelOutput.ClassifyTunnelError("Failed : HTTP error code : 403 - {\"error\":\"You already have 2 tunnels active\"}"));
            Assert.Equal("Service Unavailable", TunnelOutput.ClassifyTunnelError("Failed : HTTP error code : 503 - Service Unavailable"));
            Assert.Equal("TestingBot refused to create the tunnel, HTTP status 503", TunnelOutput.ClassifyTunnelError("Failed : HTTP error code : 503"));
            Assert.Equal("Creating a new tunnel failed", TunnelOutput.ClassifyTunnelError("Creating a new tunnel failed"));
            Assert.Equal("TestingBot Tunnel requires Java 17 or higher", TunnelOutput.ClassifyTunnelError("TestingBot Tunnel requires Java 17 or higher"));
        }

        [Fact]
        public void IgnoresOrdinaryOutput()
        {
            Assert.Null(TunnelOutput.ClassifyTunnelError("INFO: Starting tunnel"));
        }

        [Fact]
        public void UnpacksJsonLogRecords()
        {
            Assert.Equal(new[] { "first", "second" }, TunnelOutput.ParseLogLine("{\"level\":\"INFO\",\"message\":\"first\\nsecond\"}"));
            Assert.Equal(new[] { "{not json" }, TunnelOutput.ParseLogLine("{not json"));
            Assert.Equal(new[] { "plain" }, TunnelOutput.ParseLogLine("plain"));
        }

        [Fact]
        public void DescribesAStartupFailure()
        {
            Assert.Equal("Invalid credentials", TunnelOutput.DescribeStartupFailure("Invalid credentials", 1, new[] { "x" }));
            Assert.Equal("Could not start TestingBot Tunnel. Exit code 3", TunnelOutput.DescribeStartupFailure(null, 3, Array.Empty<string>()));
            Assert.Equal("Could not start TestingBot Tunnel. Exit code 3\nUnknown option\nsee --help", TunnelOutput.DescribeStartupFailure(null, 3, new[] { "Unknown option", "", "see --help" }));
        }

        [Fact]
        public void RedactsTheCredentials()
        {
            var credentials = new TestingBotCredentials("tbkey123", "tbsecret456");
            Assert.Equal("key=*** secret=***", TunnelOutput.Redact("key=tbkey123 secret=tbsecret456", credentials));
            Assert.Equal("untouched", TunnelOutput.Redact("untouched", null));
        }

        [Fact]
        public void ReadsChecksums()
        {
            var checksum = new string('a', 64);
            Assert.Equal(checksum, TunnelOutput.ParseChecksum($"{checksum}  testingbot-tunnel-linux-x64.tar.gz\n"));
            Assert.Equal(checksum, TunnelOutput.ParseChecksum($"{checksum.ToUpperInvariant()} *testingbot-tunnel-windows-x64.zip"));
            Assert.Null(TunnelOutput.ParseChecksum("<html>not found</html>"));
            Assert.Null(TunnelOutput.ParseChecksum(""));
        }
    }

    public class ArgumentTests
    {
        [Fact]
        public void PassesOptionsTheWayTheTunnelSpellsThem()
        {
            var options = new TunnelOptions
            {
                ApiKey = "key",
                ApiSecret = "secret",
                Verbose = true,
                TunnelVersion = "5.2",
                TunnelIdentifier = "build-1",
                Shared = true,
                SePort = 4446,
                NoBump = true,
                NoCache = true,
                NoProxy = true,
                HubPort = 4444,
                LocalProxy = 8088,
                FastFailRegexps = ".*\\.google\\.com",
                Headers = { "X-Test: 1", "-Cookie" },
                CacertFiles = { "/etc/ssl/a.pem" },
                SshConnections = 4,
                LogFormat = "json",
                Proxy = "  ",
                AdditionalArguments = { "--some-new-option", "value" },
            };

            Assert.Equal(new[]
            {
                "-jar", "tunnel.jar",
                "--tunnel-identifier", "build-1",
                "--shared",
                "--se-port", "4446",
                "--localproxy", "8088",
                "--hubport", "4444",
                "--noproxy",
                "--nocache",
                "--nobump",
                "--fast-fail-regexps", ".*\\.google\\.com",
                "--cacert-file", "/etc/ssl/a.pem",
                "--header", "X-Test: 1",
                "--header", "-Cookie",
                "--log-format", "json",
                "--ssh-connections", "4",
                "--some-new-option", "value",
            }, Tunnel.CreateArguments(options, "tunnel.jar"));
        }

        [Fact]
        public void LeavesTheCredentialsAndLauncherOptionsOff()
        {
            var arguments = Tunnel.CreateArguments(new TunnelOptions { ApiKey = "key", ApiSecret = "secret", Verbose = true, TunnelVersion = "5.0", JavaRuntime = TunnelJavaRuntime.Bundled }, "tunnel.jar");
            Assert.Equal(new[] { "-jar", "tunnel.jar" }, arguments);
        }

        [Fact]
        public void QuotesArgumentsForOneCommandLine()
        {
            Assert.Equal("-jar \"C:\\Program Files\\t.jar\" --header \"X-Test: 1\" \"\" \"say \\\"hi\\\"\" trailing\\ \"with space\\\\\"",
                ProcessRunner.JoinArguments(new[] { "-jar", "C:\\Program Files\\t.jar", "--header", "X-Test: 1", "", "say \"hi\"", "trailing\\", "with space\\" }));
        }
    }

    public class PlatformTests
    {
        [Fact]
        public void NamesThePlatformsThereIsADownloadWithJavaIncludedFor()
        {
            Assert.Equal("macos-arm64", TunnelEnvironment.DetectBundledPlatform("darwin", Architecture.Arm64));
            Assert.Equal("macos-x64", TunnelEnvironment.DetectBundledPlatform("darwin", Architecture.X64));
            Assert.Equal("linux-x64", TunnelEnvironment.DetectBundledPlatform("linux", Architecture.X64));
            Assert.Equal("linux-arm64", TunnelEnvironment.DetectBundledPlatform("linux", Architecture.Arm64));
            Assert.Equal("windows-x64", TunnelEnvironment.DetectBundledPlatform("win32", Architecture.X64));
            Assert.Null(TunnelEnvironment.DetectBundledPlatform("win32", Architecture.Arm64));
            Assert.Null(TunnelEnvironment.DetectBundledPlatform("linux", Architecture.X86));
            Assert.Null(TunnelEnvironment.DetectBundledPlatform("freebsd", Architecture.X64));
        }

        [Fact]
        public void UsesAZipForWindowsAndATarGzOtherwise()
        {
            Assert.Equal("testingbot-tunnel-windows-x64.zip", TunnelEnvironment.BundledArchiveName("windows-x64"));
            Assert.Equal("testingbot-tunnel-linux-arm64.tar.gz", TunnelEnvironment.BundledArchiveName("linux-arm64"));
        }

        [Fact]
        public void SharesTheCacheDirectoryWithTheNodeLauncher()
        {
            var environment = new TunnelEnvironment { GetEnvironmentVariable = _ => null, HomeDirectory = "/home/me" };
            Assert.EndsWith("testingbot-tunnel-launcher", environment.CacheDirectory());
            environment.GetEnvironmentVariable = name => name == "TESTINGBOT_TUNNEL_CACHE_DIR" ? "/tmp/elsewhere" : null;
            Assert.Equal("/tmp/elsewhere", environment.CacheDirectory());
        }

        [Fact]
        public void ReadsTheReleasesUrlFromTheEnvironment()
        {
            var environment = new TunnelEnvironment { GetEnvironmentVariable = _ => null };
            Assert.Equal("https://github.com/testingbot/Testingbot-Tunnel/releases", environment.ReleasesUrl());
            environment.GetEnvironmentVariable = name => name == "TESTINGBOT_TUNNEL_RELEASES_URL" ? "https://mirror.example/releases/" : null;
            Assert.Equal("https://mirror.example/releases", environment.ReleasesUrl());
        }
    }
}
