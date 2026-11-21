using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TestingBot.Tests
{
    public class TunnelTests
    {
        private static bool ProcessIsGone(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private static string ReadyFileArgument(string javaLog)
        {
            var line = javaLog.Split('\n').Last(entry => entry.StartsWith("ARGS -jar", StringComparison.Ordinal) && entry.Contains(" -f "));
            var parts = line.Split(' ');
            return parts[Array.IndexOf(parts, "-f") + 1];
        }

        [UnixFact]
        public async Task StartsAndStopsATunnel()
        {
            using var setup = new TestSetup();
            setup.UseJava();
            setup.CacheJar();

            var tunnel = setup.Tunnel(options => options.TunnelIdentifier = "build-1");
            Assert.False(tunnel.IsRunning);
            await tunnel.StartAsync();

            Assert.True(tunnel.IsRunning);
            Assert.Equal("5.2", tunnel.TunnelVersion);
            Assert.Contains("Tunnel is ready", setup.Messages);
            var pid = tunnel.ProcessId!.Value;
            var readyFile = ReadyFileArgument(setup.JavaLog);
            Assert.True(File.Exists(readyFile));
            Assert.Contains("--tunnel-identifier build-1", setup.JavaLog);

            await tunnel.StopAsync();

            Assert.False(tunnel.IsRunning);
            Assert.True(ProcessIsGone(pid));
            Assert.Equal(0, tunnel.ExitCode);
            Assert.False(Directory.Exists(Path.GetDirectoryName(readyFile)));
            // Stopping again does nothing
            await tunnel.StopAsync();
            tunnel.Dispose();
        }

        [UnixFact]
        public async Task PassesTheCredentialsThroughTheEnvironment()
        {
            using var setup = new TestSetup();
            setup.UseJava();
            setup.CacheJar();

            await using var tunnel = setup.Tunnel(options => options.Verbose = true);
            await tunnel.StartAsync();

            var log = setup.JavaLog;
            Assert.Contains("KEY test-key", log);
            Assert.DoesNotContain("test-secret-123", log.Split('\n').Where(line => line.StartsWith("ARGS", StringComparison.Ordinal)).Aggregate(string.Empty, string.Concat));
            Assert.DoesNotContain(setup.Messages, message => message.Contains("test-secret-123") || message.Contains("test-key"));
            Assert.Contains(setup.Messages, message => message.StartsWith("Starting tunnel with options", StringComparison.Ordinal));
        }

        [UnixFact]
        public async Task UsesTheKeyAndSecretFromTheOptions()
        {
            using var setup = new TestSetup();
            setup.UseJava();
            setup.CacheJar();

            await using var tunnel = setup.Tunnel(options => { options.ApiKey = "option-key"; options.ApiSecret = "option-secret"; });
            await tunnel.StartAsync();

            Assert.Contains("KEY option-key", setup.JavaLog);
        }

        [UnixFact]
        public async Task GivesEveryTunnelItsOwnReadyfile()
        {
            using var setup = new TestSetup();
            setup.UseJava();
            setup.CacheJar();

            await using var first = setup.Tunnel(options => options.TunnelIdentifier = "first");
            await using var second = setup.Tunnel(options => options.TunnelIdentifier = "second");
            await Task.WhenAll(first.StartAsync(), second.StartAsync());

            var readyFiles = setup.JavaLog.Split('\n').Where(line => line.StartsWith("ARGS -jar", StringComparison.Ordinal) && line.Contains(" -f ")).Select(ReadyFileArgument).ToList();
            Assert.Equal(2, readyFiles.Distinct().Count());
            Assert.NotEqual(first.ProcessId, second.ProcessId);

            await first.StopAsync();
            Assert.True(second.IsRunning);
        }

        [UnixFact]
        public async Task ReportsWrongCredentials()
        {
            using var setup = new TestSetup();
            setup.UseJava(FakeJava.Unauthorized);
            setup.CacheJar();

            var tunnel = setup.Tunnel();
            var error = await Assert.ThrowsAsync<TunnelException>(() => tunnel.StartAsync());

            Assert.Equal("Invalid credentials. Please supply the correct key/secret obtained from TestingBot.com", error.Message);
            Assert.True(ProcessIsGone(tunnel.ProcessId!.Value));
            Assert.False(tunnel.IsRunning);
        }

        [UnixFact]
        public async Task PassesOnWhatTheApiRefuses()
        {
            using var setup = new TestSetup();
            setup.UseJava(FakeJava.TooManyTunnels);
            setup.CacheJar();

            var error = await Assert.ThrowsAsync<TunnelException>(() => setup.Tunnel().StartAsync());

            Assert.Equal("You already have 2 tunnels active - please close another tunnel first", error.Message);
        }

        [UnixFact]
        public async Task UnderstandsTheJsonLogFormat()
        {
            using var setup = new TestSetup();
            setup.UseJava(FakeJava.JsonUnauthorized);
            setup.CacheJar();

            var error = await Assert.ThrowsAsync<TunnelException>(() => setup.Tunnel(options => options.LogFormat = "json").StartAsync());

            Assert.Equal("Invalid credentials. Please supply the correct key/secret obtained from TestingBot.com", error.Message);
        }

        [UnixFact]
        public async Task AddsTheLastOutputToAnExitCode()
        {
            using var setup = new TestSetup();
            setup.UseJava(FakeJava.Crash);
            setup.CacheJar();

            var error = await Assert.ThrowsAsync<TunnelException>(() => setup.Tunnel().StartAsync());

            Assert.Equal("Could not start TestingBot Tunnel. Exit code 3\nUnrecognized option: --bogus\nRun with --help to see the options", error.Message);
        }

        [UnixFact]
        public async Task StopsATunnelThatDoesNotStartInTime()
        {
            using var setup = new TestSetup();
            setup.UseJava(FakeJava.NeverReady);
            setup.CacheJar();

            var tunnel = setup.Tunnel(options => options.StartTimeout = TimeSpan.FromSeconds(1));
            var error = await Assert.ThrowsAsync<TunnelException>(() => tunnel.StartAsync());

            Assert.Equal("Tunnel failed to launch in 1 seconds.", error.Message);
            Assert.True(ProcessIsGone(tunnel.ProcessId!.Value));
        }

        [UnixFact]
        public async Task KillsATunnelThatIgnoresTheInterrupt()
        {
            using var setup = new TestSetup();
            setup.UseJava(FakeJava.IgnoresInterrupt);
            setup.CacheJar();

            var tunnel = setup.Tunnel();
            await tunnel.StartAsync();
            var stopwatch = Stopwatch.StartNew();
            await tunnel.StopAsync();

            Assert.True(ProcessIsGone(tunnel.ProcessId!.Value));
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1.5), "waited for the grace period");
        }

        [UnixFact]
        public async Task KeepsRunningWhenAnApiCallFailsOnceReady()
        {
            using var setup = new TestSetup();
            setup.UseJava(FakeJava.ReadyThenApiError);
            setup.CacheJar();

            await using var tunnel = setup.Tunnel();
            await tunnel.StartAsync();
            await Task.Delay(2000);

            Assert.True(tunnel.IsRunning);
        }

        [UnixFact]
        public async Task StopsAStartingTunnelWhenCancelled()
        {
            using var setup = new TestSetup();
            setup.UseJava(FakeJava.NeverReady);
            setup.CacheJar();

            var tunnel = setup.Tunnel();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tunnel.StartAsync(cancel.Token));

            Assert.True(ProcessIsGone(tunnel.ProcessId!.Value));
        }

        [UnixFact]
        public async Task CanNotBeStartedTwice()
        {
            using var setup = new TestSetup();
            setup.UseJava();
            setup.CacheJar();

            await using var tunnel = setup.Tunnel();
            await tunnel.StartAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => tunnel.StartAsync());
        }

        [Fact]
        public async Task ExplainsMissingCredentials()
        {
            using var setup = new TestSetup();
            setup.Variables.Remove("TB_KEY");
            setup.Variables.Remove("TB_SECRET");

            var error = await Assert.ThrowsAsync<TunnelException>(() => setup.Tunnel().StartAsync());

            Assert.StartsWith("No TestingBot credentials found.", error.Message);
        }

        [Fact]
        public async Task ValidatesTheOptionsBeforeDownloading()
        {
            using var setup = new TestSetup();
            await Assert.ThrowsAsync<ArgumentException>(() => setup.Tunnel(options => options.SshConnections = 0).StartAsync());
            Assert.Empty(setup.Http.Requests);
        }

        [UnixFact]
        public async Task RefusesOptionsTheTunnelItDownloadedDoesNotKnow()
        {
            using var setup = new TestSetup();
            setup.UseJava(tunnel: "4.9", runtime: 17);
            setup.CacheJar();

            var error = await Assert.ThrowsAsync<TunnelException>(() => setup.Tunnel(options => options.AllowHosts = "example.com").StartAsync());

            Assert.Equal("AllowHosts requires TestingBot Tunnel 5.0 or higher, but version 4.9 is used. Set TunnelVersion to \"5.0\" or higher.", error.Message);
        }

        [UnixFact]
        public async Task StopsRunningTunnelsWhenTheProcessExits()
        {
            using var setup = new TestSetup();
            setup.UseJava();
            setup.CacheJar();

            var tunnel = setup.Tunnel();
            await tunnel.StartAsync();
            Tunnel.StopAllWhileExiting();

            var stopwatch = Stopwatch.StartNew();
            while (!ProcessIsGone(tunnel.ProcessId!.Value) && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50);
            }
            Assert.True(ProcessIsGone(tunnel.ProcessId!.Value));
        }
    }
}
