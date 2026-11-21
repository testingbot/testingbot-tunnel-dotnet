using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TestingBot.Internal
{
    /// <summary>The jar to run and the java to run it with.</summary>
    internal sealed class TunnelRuntime
    {
        public TunnelRuntime(string? version, string java, string jar, bool bundled)
        {
            Version = version;
            Java = java;
            Jar = jar;
            Bundled = bundled;
        }

        /// <summary>The tunnel version, null when it could not be determined.</summary>
        public string? Version { get; }
        public string Java { get; }
        public string Jar { get; }
        public bool Bundled { get; }
    }

    /// <summary>Finds, downloads and checks the tunnel and the Java to run it with.</summary>
    internal sealed class RuntimeResolver
    {
        private static readonly Regex ReleaseTag = new Regex(@"/tag/v?(\d+(?:\.\d+)*)/?$", RegexOptions.CultureInvariant);
        private static readonly Regex PlainVersion = new Regex(@"^\d+(\.\d+)*$", RegexOptions.CultureInvariant);

        private readonly TunnelEnvironment _environment;
        private readonly Downloader _downloader;
        private readonly Action<string> _log;

        public RuntimeResolver(TunnelEnvironment environment, Action<string> log)
        {
            _environment = environment;
            _downloader = new Downloader(environment.HttpHandler, environment.DownloadIdleTimeout);
            _log = log;
        }

        /// <summary>
        /// Find the jar to run and the java to run it with. With <see cref="TunnelJavaRuntime.Auto"/> the installed
        /// Java is used when it is recent enough for the tunnel, the download with Java included otherwise.
        /// </summary>
        public async Task<TunnelRuntime> PrepareAsync(TunnelOptions options, CancellationToken cancellationToken)
        {
            var mode = options.JavaRuntime;

            if (mode == TunnelJavaRuntime.Bundled)
            {
                return await DownloadBundledAsync(options.TunnelVersion, cancellationToken).ConfigureAwait(false);
            }

            if (!await ProcessRunner.CanStartAsync(_environment.SystemJava, new[] { "-version" }, cancellationToken).ConfigureAwait(false))
            {
                if (mode == TunnelJavaRuntime.Auto && CanUseBundledJava(options.TunnelVersion))
                {
                    _log("Java is not installed, using the TestingBot Tunnel download with Java included.");
                    return await DownloadBundledAsync(options.TunnelVersion, cancellationToken).ConfigureAwait(false);
                }
                // Throws with the reason, before a jar is downloaded that can not be run
                await CheckJavaAsync(TunnelVersions.RequiredJavaVersion(options.TunnelVersion), cancellationToken).ConfigureAwait(false);
            }

            var jar = await DownloadJarAsync(options.TunnelVersion, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(jar))
            {
                throw new TunnelException($"Tunnel jar file is not present in {jar}");
            }

            // Without a TunnelVersion the latest tunnel is downloaded, ask it which version that is
            var version = options.TunnelVersion ?? await ReadJarVersionAsync(jar, cancellationToken).ConfigureAwait(false);

            try
            {
                await CheckJavaAsync(TunnelVersions.RequiredJavaVersion(version), cancellationToken).ConfigureAwait(false);
            }
            catch (TunnelException error) when (mode == TunnelJavaRuntime.Auto && CanUseBundledJava(version))
            {
                _log($"{error.Message} Using the TestingBot Tunnel download with Java included instead.");
                return await DownloadBundledAsync(options.TunnelVersion, cancellationToken).ConfigureAwait(false);
            }

            return new TunnelRuntime(version, _environment.SystemJava, jar, bundled: false);
        }

        /// <summary>Whether a download with Java included can be used for a tunnel version (null: the latest).</summary>
        public bool CanUseBundledJava(string? tunnelVersion)
            => _environment.BundledPlatform != null && TunnelVersions.IsVersion5OrUp(tunnelVersion) != false;

        /// <summary>Check that the java on the PATH is recent enough.</summary>
        /// <returns>Its major version, null when it could not be determined (a warning is logged, the tunnel is tried anyway).</returns>
        public async Task<int?> CheckJavaAsync(int minVersion, CancellationToken cancellationToken)
        {
            ProcessResult result;
            try
            {
                result = await ProcessRunner.RunAsync(_environment.SystemJava, new[] { "-version" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception error)
            {
                throw new TunnelException($"Java might not be installed or not in $PATH. Java is necessary to use testingbot-tunnel {error.Message}", error);
            }

            var (version, message) = TunnelVersions.ValidateJavaVersion(result.Stderr + result.Stdout, minVersion);
            if (message == null)
            {
                return version;
            }
            if (version == null)
            {
                _log(message);
                return null;
            }
            throw new TunnelException(message);
        }

        /// <summary>
        /// Whether a cached jar can be run. Only the exit code is used: the JVM writes messages such as
        /// "Picked up JAVA_TOOL_OPTIONS" to stderr for a perfectly valid jar. A tunnel that refuses to
        /// run on the installed Java is not corrupt either, downloading it again would not help.
        /// </summary>
        public async Task<bool> IsJarValidAsync(string jar, CancellationToken cancellationToken)
        {
            try
            {
                var result = await ProcessRunner.RunAsync(_environment.SystemJava, new[] { "-jar", jar, "-h" }, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                return result.ExitCode == 0 || TunnelOutput.JavaRequirement.IsMatch(result.Stderr);
            }
            catch (Win32Exception)
            {
                return false;
            }
            catch (TunnelException)
            {
                return false;
            }
        }

        /// <summary>Ask a jar which version of the tunnel it is, null when that could not be determined.</summary>
        public async Task<string?> ReadJarVersionAsync(string jar, CancellationToken cancellationToken)
        {
            try
            {
                var result = await ProcessRunner.RunAsync(_environment.SystemJava, new[] { "-jar", jar, "--version" }, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                return TunnelVersions.ParseJarVersion(result.Stdout);
            }
            catch (Win32Exception)
            {
                return null;
            }
            catch (TunnelException)
            {
                return null;
            }
        }

        /// <summary>Download the tunnel jar, unless a working one is in the cache directory.</summary>
        public async Task<string> DownloadJarAsync(string? tunnelVersion, CancellationToken cancellationToken)
        {
            var jarName = tunnelVersion != null ? $"testingbot-tunnel-{tunnelVersion}.jar" : "testingbot-tunnel.jar";
            var directory = _environment.CacheDirectory();
            var location = Path.Combine(directory, jarName);

            if (File.Exists(location))
            {
                if (await IsJarValidAsync(location, cancellationToken).ConfigureAwait(false))
                {
                    return location;
                }
                _log($"Found a cached {jarName} file in {directory}, but it might be corrupt. Redownloading.");
            }

            if (!FileSystem.IsWritableDirectory(directory))
            {
                throw new TunnelException($"Could not write the tunnel jar to {directory}. Set TESTINGBOT_TUNNEL_CACHE_DIR to a directory that can be written to.");
            }

            try
            {
                await _downloader.DownloadAsync(_environment.JarUrl + jarName, location, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!(error is OperationCanceledException) && (error is TunnelException || error is IOException || error is System.Net.Http.HttpRequestException || error is UnauthorizedAccessException))
            {
                throw new TunnelException($"Could not download the tunnel from TestingBot - please check your connection. {error.Message}", error);
            }

            return location;
        }

        /// <summary>The newest version of the tunnel that has been published, e.g. "5.2".</summary>
        public async Task<string> LatestReleaseVersionAsync(CancellationToken cancellationToken)
        {
            var url = $"{_environment.ReleasesUrl()}/latest";
            Exception? failure = null;
            Uri? location = null;
            try
            {
                location = await _downloader.RedirectLocationAsync(url, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!(error is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                failure = error;
            }

            var match = location != null ? ReleaseTag.Match(location.AbsolutePath) : Match.Empty;
            if (failure != null || !match.Success)
            {
                throw new TunnelException($"Could not find the latest version of TestingBot Tunnel at {url}. {failure?.Message}".Trim(), failure!);
            }
            return match.Groups[1].Value;
        }

        /// <summary>The newest download with Java included that is already unpacked for a platform, null when there is none.</summary>
        public string? NewestCachedBundle(string platformName)
        {
            string[] entries;
            try
            {
                entries = Directory.GetDirectories(_environment.BundlesDirectory()).Select(Path.GetFileName).ToArray()!;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            var suffix = $"-{platformName}";
            return entries
                .Where(entry => entry.EndsWith(suffix, StringComparison.Ordinal) && _environment.IsBundleComplete(Path.Combine(_environment.BundlesDirectory(), entry)))
                .Select(entry => entry.Substring(0, entry.Length - suffix.Length))
                .Where(version => PlainVersion.IsMatch(version))
                .OrderBy(version => version, Comparer<string>.Create((a, b) => TunnelVersions.CompareVersions(a, b) ?? 0))
                .LastOrDefault();
        }

        /// <summary>
        /// Download and unpack the TestingBot Tunnel download with Java included, which needs no Java to be
        /// installed. These exist for tunnel 5.0 and up. Every download is checked against the checksum published next to it.
        /// </summary>
        public async Task<TunnelRuntime> DownloadBundledAsync(string? tunnelVersion, CancellationToken cancellationToken)
        {
            var platformName = _environment.BundledPlatform;
            if (platformName == null)
            {
                throw new TunnelException($"There is no TestingBot Tunnel download with Java included for {System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}. Install Java {TunnelVersions.MinJavaVersion5} or higher instead.");
            }

            if (tunnelVersion != null && TunnelVersions.IsVersion5OrUp(tunnelVersion) == false)
            {
                throw new TunnelException($"TestingBot Tunnel {tunnelVersion} has no download with Java included, those exist for 5.0 and up. Install Java {TunnelVersions.MinJavaVersion} or higher, or set TunnelVersion to \"5.0\" or higher.");
            }

            var version = tunnelVersion;
            if (version == null)
            {
                try
                {
                    version = await LatestReleaseVersionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (TunnelException)
                {
                    // Without a connection, a version that was downloaded before is still good to use
                    version = NewestCachedBundle(platformName);
                    if (version == null)
                    {
                        throw;
                    }
                }
            }

            var root = _environment.BundlesDirectory();
            var directory = Path.Combine(root, $"{version}-{platformName}");
            TunnelRuntime Result() => new TunnelRuntime(version, _environment.BundledJavaPath(directory), _environment.BundledJarPath(directory), bundled: true);

            if (_environment.IsBundleComplete(directory))
            {
                return Result();
            }

            if (!FileSystem.IsWritableDirectory(root))
            {
                throw new TunnelException($"Could not write the tunnel to {root}. Set TESTINGBOT_TUNNEL_CACHE_DIR to a directory that can be written to.");
            }

            var archiveName = TunnelEnvironment.BundledArchiveName(platformName);
            var url = $"{_environment.ReleasesUrl()}/download/v{version}/{archiveName}";
            // Unique names, so launchers unpacking next to each other do not get in each other's way
            var unique = $"{Downloader.CurrentProcessId()}.{Downloader.RandomSuffix()}";
            var archive = Path.Combine(root, $"{unique}.{archiveName}");
            var checksumFile = archive + ".sha256";
            var extractDirectory = Path.Combine(root, $"{unique}.unpacking");

            _log($"Downloading TestingBot Tunnel {version} with Java included for {platformName}");

            try
            {
                try
                {
                    await Task.WhenAll(
                        _downloader.DownloadAsync(url, archive, cancellationToken),
                        _downloader.DownloadAsync(url + ".sha256", checksumFile, cancellationToken)).ConfigureAwait(false);
                }
                catch (Exception error) when (!(error is OperationCanceledException))
                {
                    throw new TunnelException($"Could not download TestingBot Tunnel {version} with Java included from {url} - please check your connection. {error.Message}", error);
                }

                var expected = TunnelOutput.ParseChecksum(File.ReadAllText(checksumFile));
                var actual = await FileSystem.Sha256Async(archive).ConfigureAwait(false);
                if (expected == null || expected != actual)
                {
                    throw new TunnelException($"The download of {url} does not match its published checksum, it was not used.");
                }

                Directory.CreateDirectory(extractDirectory);
                await ExtractAsync(archive, extractDirectory, cancellationToken).ConfigureAwait(false);

                // The archives hold a single directory, such as testingbot-tunnel-macos-arm64/
                var unpacked = extractDirectory;
                if (!_environment.IsBundleComplete(unpacked))
                {
                    unpacked = Directory.GetDirectories(extractDirectory).FirstOrDefault(_environment.IsBundleComplete) ?? extractDirectory;
                }
                if (!_environment.IsBundleComplete(unpacked))
                {
                    throw new TunnelException($"The download of {url} does not hold the tunnel and Java where they were expected.");
                }

                try
                {
                    Directory.Move(unpacked, directory);
                }
                catch (IOException) when (_environment.IsBundleComplete(directory))
                {
                    // Another launcher was first, its copy is just as good
                }
            }
            finally
            {
                FileSystem.TryDelete(archive);
                FileSystem.TryDelete(checksumFile);
                FileSystem.TryDeleteDirectory(extractDirectory);
            }

            return Result();
        }

        private async Task ExtractAsync(string archive, string directory, CancellationToken cancellationToken)
        {
            ProcessResult result;
            try
            {
                result = await ProcessRunner.RunAsync(_environment.Tar(), new[] { "-xf", archive, "-C", directory }, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception error)
            {
                throw new TunnelException($"Could not unpack {Path.GetFileName(archive)}, tar is needed to do so. {error.Message}", error);
            }

            if (result.ExitCode != 0)
            {
                var output = result.Stderr.Trim();
                throw new TunnelException($"Could not unpack {Path.GetFileName(archive)}: {(output.Length > 0 ? output : $"tar exited with code {result.ExitCode}")}");
            }
        }
    }
}
