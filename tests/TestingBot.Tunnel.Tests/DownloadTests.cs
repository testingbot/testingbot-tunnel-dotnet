using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TestingBot.Internal;
using Xunit;

namespace TestingBot.Tests
{
    public class DownloaderTests
    {
        private const string Url = "https://testingbot.example/tunnel/testingbot-tunnel.jar";

        [Fact]
        public async Task DownloadsAFile()
        {
            using var directory = new TempDirectory();
            var http = new FakeHttpHandler().File(Url, Encoding.UTF8.GetBytes("jar contents"));
            var destination = directory.Combine("t.jar");

            await new Downloader(http).DownloadAsync(Url, destination, CancellationToken.None);

            Assert.Equal("jar contents", File.ReadAllText(destination));
            Assert.Single(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task ReportsABadStatusCode()
        {
            using var directory = new TempDirectory();
            var error = await Assert.ThrowsAsync<TunnelException>(() => new Downloader(new FakeHttpHandler()).DownloadAsync(Url, directory.Combine("t.jar"), CancellationToken.None));
            Assert.Equal($"Could not download {Url}, statusCode: 404", error.Message);
        }

        [Fact]
        public async Task FollowsRelativeAndAbsoluteRedirects()
        {
            using var directory = new TempDirectory();
            var http = new FakeHttpHandler()
                .Redirect(Url, "/moved.jar")
                .Redirect("https://testingbot.example/moved.jar", "https://cdn.example/final.jar", HttpStatusCode.MovedPermanently)
                .File("https://cdn.example/final.jar", Encoding.UTF8.GetBytes("final"));

            await new Downloader(http).DownloadAsync(Url, directory.Combine("t.jar"), CancellationToken.None);

            Assert.Equal("final", File.ReadAllText(directory.Combine("t.jar")));
        }

        [Fact]
        public async Task StopsAfterFiveRedirects()
        {
            using var directory = new TempDirectory();
            var http = new FakeHttpHandler().Redirect(Url, Url);
            var error = await Assert.ThrowsAsync<TunnelException>(() => new Downloader(http).DownloadAsync(Url, directory.Combine("t.jar"), CancellationToken.None));
            Assert.Equal($"Too many redirects (max 5) for {Url}", error.Message);
            Assert.Equal(6, http.Requests.Count);
        }

        [Fact]
        public async Task RefusesARedirectFromHttpsToHttp()
        {
            using var directory = new TempDirectory();
            var http = new FakeHttpHandler().Redirect(Url, "http://insecure.example/t.jar");
            var error = await Assert.ThrowsAsync<TunnelException>(() => new Downloader(http).DownloadAsync(Url, directory.Combine("t.jar"), CancellationToken.None));
            Assert.Equal("Refusing to follow the redirect from https://testingbot.example to http://insecure.example, the download would no longer be encrypted", error.Message);
            Assert.False(File.Exists(directory.Combine("t.jar")));
        }

        [Fact]
        public void AllowsARedirectThatStaysOnHttp()
        {
            Assert.Equal("http://b.example/x", Downloader.ResolveRedirect(new Uri("http://a.example/y"), "http://b.example/x").ToString());
        }

        [Fact]
        public async Task LeavesNothingBehindWhenADownloadIsInterrupted()
        {
            using var directory = new TempDirectory();
            var http = new FakeHttpHandler().On(Url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(stall: false)) });

            await Assert.ThrowsAnyAsync<Exception>(() => new Downloader(http).DownloadAsync(Url, directory.Combine("t.jar"), CancellationToken.None));

            Assert.Empty(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task TimesOutOnAStalledDownload()
        {
            using var directory = new TempDirectory();
            var http = new FakeHttpHandler().On(Url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(stall: true)) });

            var error = await Assert.ThrowsAsync<TunnelException>(() => new Downloader(http, TimeSpan.FromMilliseconds(200)).DownloadAsync(Url, directory.Combine("t.jar"), CancellationToken.None));

            Assert.Equal($"Timed out after 200ms while downloading {Url}", error.Message);
            Assert.Empty(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task DownloadsNextToEachOtherDoNotWriteOverEachOther()
        {
            using var directory = new TempDirectory();
            var big = new byte[512 * 1024];
            new Random(1).NextBytes(big);
            var http = new FakeHttpHandler().File(Url, big);
            var destination = directory.Combine("t.jar");

            var downloader = new Downloader(http);
            await Task.WhenAll(downloader.DownloadAsync(Url, destination, CancellationToken.None), downloader.DownloadAsync(Url, destination, CancellationToken.None));

            Assert.Equal(big, File.ReadAllBytes(destination));
            Assert.Single(Directory.GetFiles(directory.Path));
        }
    }

    public class JarDownloadTests
    {
        [UnixFact]
        public async Task DownloadsTheJarIntoTheCacheDirectory()
        {
            using var setup = new TestSetup();
            setup.UseJava();
            setup.Http.File("https://testingbot.example/tunnel/testingbot-tunnel-5.2.jar", Encoding.UTF8.GetBytes("jar"));

            var jar = await new RuntimeResolver(setup.Environment, _ => { }).DownloadJarAsync("5.2", CancellationToken.None);

            Assert.Equal(Path.Combine(setup.Cache, "testingbot-tunnel-5.2.jar"), jar);
            Assert.True(File.Exists(jar));
        }

        [UnixFact]
        public async Task UsesAWorkingJarFromTheCache()
        {
            using var setup = new TestSetup();
            setup.UseJava();
            var cached = setup.CacheJar();

            var jar = await new RuntimeResolver(setup.Environment, _ => { }).DownloadJarAsync(null, CancellationToken.None);

            Assert.Equal(cached, jar);
            Assert.Empty(setup.Http.Requests);
        }

        [UnixFact]
        public async Task DownloadsACorruptJarAgain()
        {
            using var setup = new TestSetup();
            // A java that exits 1 for everything, as it does for a corrupt jar
            setup.Environment.SystemJava = FakeJava.Write(setup.Combine("broken"), "exit 1");
            File.WriteAllText(setup.Environment.SystemJava, "#!/bin/sh\necho 'Error: Invalid or corrupt jarfile' >&2\nexit 1\n");
            setup.CacheJar();
            setup.Http.File("https://testingbot.example/tunnel/testingbot-tunnel.jar", Encoding.UTF8.GetBytes("new jar"));
            var messages = new System.Collections.Generic.List<string>();

            var jar = await new RuntimeResolver(setup.Environment, messages.Add).DownloadJarAsync(null, CancellationToken.None);

            Assert.Equal("new jar", File.ReadAllText(jar));
            Assert.Contains(messages, message => message.StartsWith("Found a cached testingbot-tunnel.jar file in", StringComparison.Ordinal) && message.EndsWith("but it might be corrupt. Redownloading.", StringComparison.Ordinal));
        }

        [UnixFact]
        public async Task ExplainsAFailedDownload()
        {
            using var setup = new TestSetup();
            setup.UseJava();

            var error = await Assert.ThrowsAsync<TunnelException>(() => new RuntimeResolver(setup.Environment, _ => { }).DownloadJarAsync("0.1", CancellationToken.None));

            Assert.Equal("Could not download the tunnel from TestingBot - please check your connection. Could not download https://testingbot.example/tunnel/testingbot-tunnel-0.1.jar, statusCode: 404", error.Message);
        }
    }

    public class BundledDownloadTests
    {
        private const string Releases = "https://releases.example/tunnel/releases";
        private const string Archive = "testingbot-tunnel-linux-x64.tar.gz";

        /// <summary>Build a download with Java included that holds a fake java, the way TestingBot lays it out.</summary>
        private static byte[] BuildArchive(TestSetup setup, string mode = FakeJava.Ready)
        {
            var staging = setup.Combine("staging", "testingbot-tunnel-linux-x64");
            FakeJava.Write(Path.Combine(staging, "runtime", "bin"), mode, log: setup.Log);
            Directory.CreateDirectory(Path.Combine(staging, "lib"));
            File.WriteAllText(Path.Combine(staging, "lib", "testingbot-tunnel.jar"), "jar");
            var archive = setup.Combine("archive.tar.gz");
            using (var tar = Process.Start(new ProcessStartInfo("tar", $"-czf \"{archive}\" -C \"{setup.Combine("staging")}\" testingbot-tunnel-linux-x64") { UseShellExecute = false })!)
            {
                tar.WaitForExit();
                Assert.Equal(0, tar.ExitCode);
            }
            return File.ReadAllBytes(archive);
        }

        private static string Sha256(byte[] contents)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(contents)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void Publish(TestSetup setup, string version, byte[] archive, string? checksum = null)
        {
            setup.Http.File($"{Releases}/download/v{version}/{Archive}", archive);
            setup.Http.File($"{Releases}/download/v{version}/{Archive}.sha256", Encoding.UTF8.GetBytes($"{checksum ?? Sha256(archive)}  {Archive}\n"));
        }

        [UnixFact]
        public async Task DownloadsTheLatestVersionAndUnpacksItInTheCacheDirectory()
        {
            using var setup = new TestSetup();
            setup.Http.Redirect($"{Releases}/latest", "https://releases.example/tunnel/releases/tag/v5.2");
            Publish(setup, "5.2", BuildArchive(setup));

            var runtime = await new RuntimeResolver(setup.Environment, _ => { }).DownloadBundledAsync(null, CancellationToken.None);

            var directory = Path.Combine(setup.Cache, "bundles", "5.2-linux-x64");
            Assert.Equal("5.2", runtime.Version);
            Assert.Equal(Path.Combine(directory, "runtime", "bin", "java"), runtime.Java);
            Assert.Equal(Path.Combine(directory, "lib", "testingbot-tunnel.jar"), runtime.Jar);
            Assert.True(File.Exists(runtime.Java));
            // Nothing but the unpacked download is left behind
            Assert.Equal(new[] { directory }, Directory.GetFileSystemEntries(Path.Combine(setup.Cache, "bundles")));
        }

        [UnixFact]
        public async Task UsesADownloadThatIsAlreadyUnpacked()
        {
            using var setup = new TestSetup();
            Publish(setup, "5.1", BuildArchive(setup));
            var resolver = new RuntimeResolver(setup.Environment, _ => { });
            await resolver.DownloadBundledAsync("5.1", CancellationToken.None);
            setup.Http.Requests.Clear();

            await resolver.DownloadBundledAsync("5.1", CancellationToken.None);

            Assert.Empty(setup.Http.Requests);
        }

        [UnixFact]
        public async Task UsesTheNewestDownloadItHasWhenTheLatestVersionCanNotBeLookedUp()
        {
            using var setup = new TestSetup();
            var archive = BuildArchive(setup);
            Publish(setup, "5.0", archive);
            Publish(setup, "5.1", archive);
            var resolver = new RuntimeResolver(setup.Environment, _ => { });
            await resolver.DownloadBundledAsync("5.0", CancellationToken.None);
            await resolver.DownloadBundledAsync("5.1", CancellationToken.None);

            var runtime = await resolver.DownloadBundledAsync(null, CancellationToken.None);

            Assert.Equal("5.1", runtime.Version);
        }

        [UnixFact]
        public async Task FailsWhenTheLatestVersionCanNotBeLookedUpAndNothingWasDownloaded()
        {
            using var setup = new TestSetup();
            var error = await Assert.ThrowsAsync<TunnelException>(() => new RuntimeResolver(setup.Environment, _ => { }).DownloadBundledAsync(null, CancellationToken.None));
            Assert.StartsWith($"Could not find the latest version of TestingBot Tunnel at {Releases}/latest.", error.Message);
        }

        [UnixFact]
        public async Task RefusesADownloadThatDoesNotMatchItsChecksum()
        {
            using var setup = new TestSetup();
            Publish(setup, "5.2", BuildArchive(setup), checksum: new string('0', 64));

            var error = await Assert.ThrowsAsync<TunnelException>(() => new RuntimeResolver(setup.Environment, _ => { }).DownloadBundledAsync("5.2", CancellationToken.None));

            Assert.Equal($"The download of {Releases}/download/v5.2/{Archive} does not match its published checksum, it was not used.", error.Message);
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(setup.Cache, "bundles")));
        }

        [Fact]
        public async Task RefusesTunnelsOlderThan5()
        {
            using var setup = new TestSetup();
            var error = await Assert.ThrowsAsync<TunnelException>(() => new RuntimeResolver(setup.Environment, _ => { }).DownloadBundledAsync("4.9", CancellationToken.None));
            Assert.Equal("TestingBot Tunnel 4.9 has no download with Java included, those exist for 5.0 and up. Install Java 11 or higher, or set TunnelVersion to \"5.0\" or higher.", error.Message);
        }

        [Fact]
        public async Task ExplainsAPlatformWithoutADownload()
        {
            using var setup = new TestSetup();
            setup.Environment.BundledPlatform = null;
            var error = await Assert.ThrowsAsync<TunnelException>(() => new RuntimeResolver(setup.Environment, _ => { }).DownloadBundledAsync("5.2", CancellationToken.None));
            Assert.StartsWith("There is no TestingBot Tunnel download with Java included for", error.Message);
            Assert.EndsWith("Install Java 17 or higher instead.", error.Message);
        }

        [UnixFact]
        public async Task ReportsAVersionThatWasNotPublished()
        {
            using var setup = new TestSetup();
            var error = await Assert.ThrowsAsync<TunnelException>(() => new RuntimeResolver(setup.Environment, _ => { }).DownloadBundledAsync("5.9", CancellationToken.None));
            Assert.StartsWith($"Could not download TestingBot Tunnel 5.9 with Java included from {Releases}/download/v5.9/{Archive} - please check your connection.", error.Message);
        }

        [UnixFact]
        public async Task StartsTheTunnelWithTheJavaItIncludesForBundled()
        {
            using var setup = new TestSetup();
            Publish(setup, "5.2", BuildArchive(setup));

            await using var tunnel = setup.Tunnel(options => { options.JavaRuntime = TunnelJavaRuntime.Bundled; options.TunnelVersion = "5.2"; });
            await tunnel.StartAsync();

            Assert.True(tunnel.IsRunning);
            Assert.Equal(Path.Combine(setup.Cache, "bundles", "5.2-linux-x64", "runtime", "bin", "java"), tunnel.JavaPath);
        }

        [UnixFact]
        public async Task UsesTheDownloadWithJavaIncludedWhenJavaIsNotInstalled()
        {
            using var setup = new TestSetup();
            setup.Environment.SystemJava = setup.Combine("no-such-java");
            setup.Http.Redirect($"{Releases}/latest", "/tunnel/releases/tag/v5.2");
            Publish(setup, "5.2", BuildArchive(setup));

            await using var tunnel = setup.Tunnel();
            await tunnel.StartAsync();

            Assert.True(tunnel.IsRunning);
            Assert.Equal("5.2", tunnel.TunnelVersion);
            Assert.Contains("Java is not installed, using the TestingBot Tunnel download with Java included.", setup.Messages);
        }

        [UnixFact]
        public async Task DoesNotFallBackForSystem()
        {
            using var setup = new TestSetup();
            setup.Environment.SystemJava = setup.Combine("no-such-java");

            var error = await Assert.ThrowsAsync<TunnelException>(() => setup.Tunnel(options => options.JavaRuntime = TunnelJavaRuntime.System).StartAsync());

            Assert.StartsWith("Java might not be installed or not in $PATH. Java is necessary to use testingbot-tunnel", error.Message);
            Assert.Empty(setup.Http.Requests);
        }

        [UnixFact]
        public async Task UsesTheDownloadWithJavaIncludedWhenTheInstalledJavaIsTooOld()
        {
            using var setup = new TestSetup();
            setup.UseJava(runtime: 11, tunnel: "5.2");
            setup.CacheJar("testingbot-tunnel-5.2.jar");
            Publish(setup, "5.2", BuildArchive(setup));

            await using var tunnel = setup.Tunnel(options => options.TunnelVersion = "5.2");
            await tunnel.StartAsync();

            Assert.Contains(Path.Combine("bundles", "5.2-linux-x64"), tunnel.JavaPath);
            Assert.Contains("Java 11 is installed, but Java 17 or higher is required for testingbot-tunnel. Using the TestingBot Tunnel download with Java included instead.", setup.Messages);
        }

        [UnixFact]
        public async Task RefusesTooOldJavaForSystem()
        {
            using var setup = new TestSetup();
            setup.UseJava(runtime: 11, tunnel: "5.2");
            setup.CacheJar();

            var error = await Assert.ThrowsAsync<TunnelException>(() => setup.Tunnel(options => options.JavaRuntime = TunnelJavaRuntime.System).StartAsync());

            Assert.Equal("Java 11 is installed, but Java 17 or higher is required for testingbot-tunnel.", error.Message);
        }
    }
}
