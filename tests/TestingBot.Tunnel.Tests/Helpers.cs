using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TestingBot.Internal;
using Xunit;

namespace TestingBot.Tests
{
    /// <summary>A test that only runs where a POSIX shell can stand in for java.</summary>
    public sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Skip = "Needs a POSIX shell to fake java";
            }
        }
    }

    /// <summary>A temporary directory, removed when disposed.</summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tb-tunnel-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    internal static class ArrayExtensions
    {
        public static string[] Concat(this string[] first, string[] second)
        {
            var result = new string[first.Length + second.Length];
            first.CopyTo(result, 0);
            second.CopyTo(result, first.Length);
            return result;
        }
    }

    /// <summary>
    /// A stand-in for java, so the tests can make a tunnel behave in ways the real one only does on a bad day.
    /// It understands what the launcher asks of java:
    ///   java -version                  reports <c>runtime</c>
    ///   java -jar tunnel.jar -h        exits 0
    ///   java -jar tunnel.jar --version reports <c>tunnel</c>
    ///   java -jar tunnel.jar ... -f F  behaves as <c>mode</c> says
    /// Every argument it is started with, and the TESTINGBOT_KEY it sees, is appended to <c>log</c>.
    /// </summary>
    internal static class FakeJava
    {
        public const string Ready = "touch \"$READY\"; trap 'exit 0' INT TERM; while true; do sleep 0.1; done";
        public const string NeverReady = "trap 'exit 0' INT TERM; while true; do sleep 0.1; done";
        public const string Unauthorized = "echo 'An error ocurred: 401 Unauthorized - {\"error\":\"401 Unauthorized\"}' >&2; trap 'exit 1' INT TERM; while true; do sleep 0.1; done";
        public const string TooManyTunnels = "echo 'Failed : HTTP error code : 403 - {\"error\":\"You already have 2 tunnels active - please close another tunnel first\"}' >&2; trap 'exit 1' INT TERM; while true; do sleep 0.1; done";
        public const string Crash = "echo 'Unrecognized option: --bogus' >&2; echo 'Run with --help to see the options' >&2; exit 3";
        public const string IgnoresInterrupt = "touch \"$READY\"; trap '' INT; while true; do sleep 0.1; done";
        public const string ReadyThenApiError = "touch \"$READY\"; sleep 1; echo 'Failed : HTTP error code : 503' >&2; trap 'exit 0' INT TERM; while true; do sleep 0.1; done";
        public const string JsonUnauthorized = "printf '%s\\n' '{\"level\":\"SEVERE\",\"message\":\"Creating a new tunnel failed\\nAn error ocurred: 401 Unauthorized\"}' >&2; trap 'exit 1' INT TERM; while true; do sleep 0.1; done";

        public static string Write(string directory, string mode = Ready, int runtime = 17, string tunnel = "5.2", string? log = null, string name = "java")
        {
            Directory.CreateDirectory(directory);
            var location = Path.Combine(directory, name);
            var script = string.Join("\n", new[]
            {
                "#!/bin/sh",
                log != null ? $"echo \"ARGS $*\" >> '{log}'; echo \"KEY ${{TESTINGBOT_KEY:-unset}}\" >> '{log}'" : ":",
                "if [ \"$1\" = \"-version\" ]; then",
                $"  echo 'openjdk version \"{runtime}.0.1\" 2024-10-15' >&2",
                "  exit 0",
                "fi",
                "for arg in \"$@\"; do",
                "  [ \"$arg\" = \"-h\" ] && exit 0",
                $"  [ \"$arg\" = \"--version\" ] && echo 'Version: testingbot-tunnel.jar {tunnel}' && exit 0",
                "done",
                "READY=\"\"",
                "while [ $# -gt 0 ]; do [ \"$1\" = \"-f\" ] && READY=\"$2\"; shift; done",
                mode,
                "",
            });
            File.WriteAllText(location, script);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(location, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return location;
        }
    }

    /// <summary>Answers HTTP requests from a table instead of the network.</summary>
    internal sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>>();

        public List<string> Requests { get; } = new List<string>();

        public FakeHttpHandler On(string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _routes[url] = respond;
            return this;
        }

        public FakeHttpHandler File(string url, byte[] contents) => On(url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contents) });

        public FakeHttpHandler Redirect(string url, string location, HttpStatusCode status = HttpStatusCode.Found)
            => On(url, _ =>
            {
                var response = new HttpResponseMessage(status);
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return response;
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add($"{request.Method} {url}");
            }
            return Task.FromResult(_routes.TryGetValue(url, out var respond)
                ? respond(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") });
        }
    }

    /// <summary>A stream that hands out some bytes and then fails or stalls.</summary>
    internal sealed class BrokenStream : Stream
    {
        private readonly bool _stall;
        private bool _sentFirst;

        public BrokenStream(bool stall) => _stall = stall;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!_sentFirst)
            {
                _sentFirst = true;
                buffer[offset] = 42;
                return 1;
            }
            if (_stall)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            throw new IOException("Connection reset");
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A launcher environment that only touches a temporary directory and the fake HTTP handler.</summary>
    internal sealed class TestSetup : IDisposable
    {
        private readonly TempDirectory _directory = new TempDirectory();

        public TestSetup()
        {
            Variables["TESTINGBOT_TUNNEL_CACHE_DIR"] = Cache;
            Variables["TB_KEY"] = "test-key";
            Variables["TB_SECRET"] = "test-secret-123";
            Environment = new TunnelEnvironment
            {
                GetEnvironmentVariable = name => Variables.TryGetValue(name, out var value) ? value : null,
                HomeDirectory = _directory.Combine("home"),
                HttpHandler = Http,
                JarUrl = "https://testingbot.example/tunnel/",
                KillGracePeriod = TimeSpan.FromSeconds(2),
                ReadyPollInterval = TimeSpan.FromMilliseconds(50),
                BundledPlatform = "linux-x64",
            };
            Variables["TESTINGBOT_TUNNEL_RELEASES_URL"] = "https://releases.example/tunnel/releases";
        }

        public Dictionary<string, string?> Variables { get; } = new Dictionary<string, string?>();

        public FakeHttpHandler Http { get; } = new FakeHttpHandler();

        public TunnelEnvironment Environment { get; }

        public string Root => _directory.Path;

        public string Cache => _directory.Combine("cache");

        public string Log => _directory.Combine("java.log");

        public List<string> Messages { get; } = new List<string>();

        public string Combine(params string[] parts) => _directory.Combine(parts);

        /// <summary>Put a fake java on the "PATH" of the launcher.</summary>
        public string UseJava(string mode = FakeJava.Ready, int runtime = 17, string tunnel = "5.2")
        {
            Environment.SystemJava = FakeJava.Write(_directory.Combine("bin"), mode, runtime, tunnel, Log);
            return Environment.SystemJava;
        }

        /// <summary>Put a jar in the cache, so nothing has to be downloaded.</summary>
        public string CacheJar(string name = "testingbot-tunnel.jar")
        {
            Directory.CreateDirectory(Cache);
            var jar = Path.Combine(Cache, name);
            System.IO.File.WriteAllText(jar, "jar");
            return jar;
        }

        public TunnelOptions Options(Action<TunnelOptions>? configure = null)
        {
            var options = new TunnelOptions
            {
                Log = message => { lock (Messages) { Messages.Add(message); } },
                StartTimeout = TimeSpan.FromSeconds(20),
            };
            configure?.Invoke(options);
            return options;
        }

        public Tunnel Tunnel(Action<TunnelOptions>? configure = null) => new Tunnel(Options(configure), Environment);

        public string JavaLog => System.IO.File.Exists(Log) ? System.IO.File.ReadAllText(Log) : string.Empty;

        public void Dispose() => _directory.Dispose();
    }
}
