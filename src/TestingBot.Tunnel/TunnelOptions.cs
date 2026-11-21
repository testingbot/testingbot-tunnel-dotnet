using System;
using System.Collections.Generic;

namespace TestingBot
{
    /// <summary>
    /// How the launcher finds the Java to run TestingBot Tunnel with.
    /// </summary>
    public enum TunnelJavaRuntime
    {
        /// <summary>
        /// The Java on the PATH when it is recent enough for the tunnel (Java 17 for tunnel 5.0 and up),
        /// the TestingBot Tunnel download with Java included otherwise.
        /// </summary>
        Auto,

        /// <summary>Always the Java on the PATH.</summary>
        System,

        /// <summary>Always the TestingBot Tunnel download with Java included (tunnel 5.0 and up).</summary>
        Bundled,
    }

    /// <summary>
    /// Options for <see cref="Tunnel"/>. The launcher options are handled by this package,
    /// every other property is passed on to TestingBot Tunnel as the command line option named in its documentation.
    /// See https://testingbot.com/support/tunnel/tunnelcommand for what each option does.
    /// </summary>
    public sealed class TunnelOptions
    {
        // ---- Launcher options, not passed on to the tunnel ----

        /// <summary>
        /// TestingBot API key. When not set, TB_KEY, TESTINGBOT_KEY and ~/.testingbot are used, in that order.
        /// It is handed to the tunnel through the environment, never on the command line.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// TestingBot API secret. When not set, TB_SECRET, TESTINGBOT_SECRET and ~/.testingbot are used, in that order.
        /// It is handed to the tunnel through the environment, never on the command line.
        /// </summary>
        public string? ApiSecret { get; set; }

        /// <summary>Write the command line (credentials redacted) and everything the tunnel writes to <see cref="Log"/>.</summary>
        public bool Verbose { get; set; }

        /// <summary>
        /// The tunnel version to run, for example "5.2". The latest version when not set.
        /// See https://testingbot.com/support/tunnel/changelog
        /// </summary>
        public string? TunnelVersion { get; set; }

        /// <summary>How long to wait for the tunnel to be ready. Default 90 seconds.</summary>
        public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(90);

        /// <summary>Which Java to run the tunnel with. Default <see cref="TunnelJavaRuntime.Auto"/>.</summary>
        public TunnelJavaRuntime JavaRuntime { get; set; } = TunnelJavaRuntime.Auto;

        /// <summary>
        /// Where the messages of the launcher go ("Tunnel is ready", downloads, and with <see cref="Verbose"/> the tunnel output).
        /// Defaults to <see cref="Console.WriteLine(string)"/>, set to null to silence it.
        /// </summary>
        public Action<string>? Log { get; set; } = Console.WriteLine;

        /// <summary>
        /// Arguments passed to the tunnel as they are, after the options above,
        /// for tunnel options this package does not know about yet.
        /// </summary>
        public IList<string> AdditionalArguments { get; set; } = new List<string>();

        // ---- Tunnel options ----

        /// <summary>--tunnel-identifier: tag the tunnel, and pick it in your capabilities with tb:options tunnel-identifier.</summary>
        public string? TunnelIdentifier { get; set; }

        /// <summary>--shared: share the tunnel with the other users of your team.</summary>
        public bool Shared { get; set; }

        /// <summary>--se-port: local port for the Selenium relay. Default 4445.</summary>
        public int? SePort { get; set; }

        /// <summary>--localproxy: port the local proxy listens on. Default 8087.</summary>
        public int? LocalProxy { get; set; }

        /// <summary>--hubport: port the tunnel server forwards Selenium traffic to, 80 or 4444.</summary>
        public int? HubPort { get; set; }

        /// <summary>--noproxy: do not start the local proxy.</summary>
        public bool NoProxy { get; set; }

        /// <summary>--nocache: bypass the caching proxy on the tunnel VM.</summary>
        public bool NoCache { get; set; }

        /// <summary>--nobump: do not perform SSL bumping.</summary>
        public bool NoBump { get; set; }

        /// <summary>--web: serve this local directory through the built-in webserver.</summary>
        public string? Web { get; set; }

        /// <summary>--proxy: upstream proxy, host:port, http://host:port or socks5://host:port.</summary>
        public string? Proxy { get; set; }

        /// <summary>--proxy-userpwd: credentials for <see cref="Proxy"/>, user:pwd.</summary>
        public string? ProxyUserpwd { get; set; }

        /// <summary>--fast-fail-regexps: comma-separated regular expressions of hosts the tunnel refuses.</summary>
        public string? FastFailRegexps { get; set; }

        /// <summary>--dns: DNS server(s) to resolve hostnames with.</summary>
        public string? Dns { get; set; }

        /// <summary>--auth: HTTP Basic Authentication for specific hosts, host:port:user:passwd.</summary>
        public string? Auth { get; set; }

        /// <summary>--pac: PAC URL for the remote browser.</summary>
        public string? Pac { get; set; }

        /// <summary>--extra-headers: request headers to add, as a JSON object.</summary>
        public string? ExtraHeaders { get; set; }

        /// <summary>--logfile: also write the log to this file.</summary>
        public string? Logfile { get; set; }

        /// <summary>--debug: debug logging.</summary>
        public bool Debug { get; set; }

        /// <summary>--metrics-port: port for the status, /metrics, /healthz and /readyz endpoints. Default 8003.</summary>
        public int? MetricsPort { get; set; }

        /// <summary>--metrics-auth: protect /metrics with HTTP Basic Authentication, user:password.</summary>
        public string? MetricsAuth { get; set; }

        // ---- Tunnel 5.0 and up ----

        /// <summary>--allow-hosts: only reach these hosts (HOST,*.HOST). Tunnel 5.0+.</summary>
        public string? AllowHosts { get; set; }

        /// <summary>--bind-address: 127.0.0.1 (default) or 0.0.0.0. Tunnel 5.0+.</summary>
        public string? BindAddress { get; set; }

        /// <summary>--cacert-file: extra CA certificates (PEM) to trust, passed once per file. Tunnel 5.0+.</summary>
        public IList<string> CacertFiles { get; set; } = new List<string>();

        /// <summary>--config: read options from a properties file. Tunnel 5.0+.</summary>
        public string? Config { get; set; }

        /// <summary>--connect-to: dial H2:P2 for requests to H1:P1 (H1:P1:H2:P2,...). Tunnel 5.0+.</summary>
        public string? ConnectTo { get; set; }

        /// <summary>--dns-round-robin: spread queries over the <see cref="Dns"/> servers. Tunnel 5.0+.</summary>
        public bool DnsRoundRobin { get; set; }

        /// <summary>--dns-timeout: timeout in seconds per DNS server. Tunnel 5.0+.</summary>
        public int? DnsTimeout { get; set; }

        /// <summary>--header: request header rules ("name: value", "name;", "-name", "-name*"), passed once per rule. Tunnel 5.0+.</summary>
        public IList<string> Headers { get; set; } = new List<string>();

        /// <summary>--response-header: response header rules, same grammar as <see cref="Headers"/>. Tunnel 5.0+.</summary>
        public IList<string> ResponseHeaders { get; set; } = new List<string>();

        /// <summary>--http-dial-timeout: TCP connect timeout in seconds. Tunnel 5.0+.</summary>
        public int? HttpDialTimeout { get; set; }

        /// <summary>--http-idle-timeout: close connections idle this many seconds. Tunnel 5.0+.</summary>
        public int? HttpIdleTimeout { get; set; }

        /// <summary>--krb5-hosts: send Kerberos credentials to these hosts. Tunnel 5.0+.</summary>
        public string? Krb5Hosts { get; set; }

        /// <summary>--krb5-keytab: keytab for unattended Kerberos, requires <see cref="Krb5Principal"/>. Tunnel 5.0+.</summary>
        public string? Krb5Keytab { get; set; }

        /// <summary>--krb5-principal: principal to log in with. Tunnel 5.0+.</summary>
        public string? Krb5Principal { get; set; }

        /// <summary>--localhost-policy: allow (default) or deny. Tunnel 5.0+.</summary>
        public string? LocalhostPolicy { get; set; }

        /// <summary>--log-format: text (default) or json. The launcher understands both. Tunnel 5.0+.</summary>
        public string? LogFormat { get; set; }

        /// <summary>--log-http: none, url, headers or errors (default), optionally per module. Tunnel 5.0+.</summary>
        public string? LogHttp { get; set; }

        /// <summary>--log-level: error, warn, info (default), debug or trace. Tunnel 5.0+.</summary>
        public string? LogLevel { get; set; }

        /// <summary>--request-id-header: correlation id header. Default X-Request-Id. Tunnel 5.0+.</summary>
        public string? RequestIdHeader { get; set; }

        /// <summary>--nobump-domains: do not bump SSL for these hosts. Tunnel 5.0+.</summary>
        public string? NoBumpDomains { get; set; }

        /// <summary>--pac-local: PAC file or URL choosing the egress of the tunnel itself. Tunnel 5.0+.</summary>
        public string? PacLocal { get; set; }

        /// <summary>--pac-local-sha256: SHA-256 the <see cref="PacLocal"/> document must have. Tunnel 5.0+.</summary>
        public string? PacLocalSha256 { get; set; }

        /// <summary>--proxy-auth-scheme: basic (default) or negotiate. Tunnel 5.0+.</summary>
        public string? ProxyAuthScheme { get; set; }

        /// <summary>--proxy-spn: service principal for negotiate. Tunnel 5.0+.</summary>
        public string? ProxySpn { get; set; }

        /// <summary>--proxy-testingbot: separate proxy for reaching TestingBot itself. Tunnel 5.0+.</summary>
        public string? ProxyTestingbot { get; set; }

        /// <summary>--proxy-testingbot-userpwd: credentials for <see cref="ProxyTestingbot"/>. Tunnel 5.0+.</summary>
        public string? ProxyTestingbotUserpwd { get; set; }

        /// <summary>--ssh-host-key: SHA-256 fingerprint(s) the tunnel server's SSH host key must have, passed once per key. Tunnel 5.0+.</summary>
        public IList<string> SshHostKeys { get; set; } = new List<string>();

        /// <summary>--ssh-host-key-policy: warn (default) or require. Tunnel 5.0+.</summary>
        public string? SshHostKeyPolicy { get; set; }

        /// <summary>--ws-proxy-mode: connect (default) or get. Tunnel 5.0+.</summary>
        public string? WsProxyMode { get; set; }

        // ---- Tunnel 5.1 and up ----

        /// <summary>--ssh-connections: SSH connections to open to the tunnel server, 1-8 (default 4). Tunnel 5.1+.</summary>
        public int? SshConnections { get; set; }

        /// <summary>
        /// The tunnel options that are set, as (property name, command line name, values).
        /// A flag has no values, a list option one value per entry.
        /// </summary>
        internal IEnumerable<TunnelArgument> TunnelArguments()
        {
            var arguments = new List<TunnelArgument>();

            void Text(string property, string name, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    arguments.Add(new TunnelArgument(property, name, new[] { value! }));
                }
            }

            void Number(string property, string name, int? value)
            {
                if (value.HasValue)
                {
                    arguments.Add(new TunnelArgument(property, name, new[] { value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                }
            }

            void Flag(string property, string name, bool value)
            {
                if (value)
                {
                    arguments.Add(new TunnelArgument(property, name, Array.Empty<string>()));
                }
            }

            void List(string property, string name, IList<string>? values)
            {
                if (values == null)
                {
                    return;
                }
                foreach (var value in values)
                {
                    Text(property, name, value);
                }
            }

            Text(nameof(TunnelIdentifier), "tunnel-identifier", TunnelIdentifier);
            Flag(nameof(Shared), "shared", Shared);
            Number(nameof(SePort), "se-port", SePort);
            Number(nameof(LocalProxy), "localproxy", LocalProxy);
            Number(nameof(HubPort), "hubport", HubPort);
            Flag(nameof(NoProxy), "noproxy", NoProxy);
            Flag(nameof(NoCache), "nocache", NoCache);
            Flag(nameof(NoBump), "nobump", NoBump);
            Text(nameof(Web), "web", Web);
            Text(nameof(Proxy), "proxy", Proxy);
            Text(nameof(ProxyUserpwd), "proxy-userpwd", ProxyUserpwd);
            Text(nameof(FastFailRegexps), "fast-fail-regexps", FastFailRegexps);
            Text(nameof(Dns), "dns", Dns);
            Text(nameof(Auth), "auth", Auth);
            Text(nameof(Pac), "pac", Pac);
            Text(nameof(ExtraHeaders), "extra-headers", ExtraHeaders);
            Text(nameof(Logfile), "logfile", Logfile);
            Flag(nameof(Debug), "debug", Debug);
            Number(nameof(MetricsPort), "metrics-port", MetricsPort);
            Text(nameof(MetricsAuth), "metrics-auth", MetricsAuth);

            Text(nameof(AllowHosts), "allow-hosts", AllowHosts);
            Text(nameof(BindAddress), "bind-address", BindAddress);
            List(nameof(CacertFiles), "cacert-file", CacertFiles);
            Text(nameof(Config), "config", Config);
            Text(nameof(ConnectTo), "connect-to", ConnectTo);
            Flag(nameof(DnsRoundRobin), "dns-round-robin", DnsRoundRobin);
            Number(nameof(DnsTimeout), "dns-timeout", DnsTimeout);
            List(nameof(Headers), "header", Headers);
            List(nameof(ResponseHeaders), "response-header", ResponseHeaders);
            Number(nameof(HttpDialTimeout), "http-dial-timeout", HttpDialTimeout);
            Number(nameof(HttpIdleTimeout), "http-idle-timeout", HttpIdleTimeout);
            Text(nameof(Krb5Hosts), "krb5-hosts", Krb5Hosts);
            Text(nameof(Krb5Keytab), "krb5-keytab", Krb5Keytab);
            Text(nameof(Krb5Principal), "krb5-principal", Krb5Principal);
            Text(nameof(LocalhostPolicy), "localhost-policy", LocalhostPolicy);
            Text(nameof(LogFormat), "log-format", LogFormat);
            Text(nameof(LogHttp), "log-http", LogHttp);
            Text(nameof(LogLevel), "log-level", LogLevel);
            Text(nameof(RequestIdHeader), "request-id-header", RequestIdHeader);
            Text(nameof(NoBumpDomains), "nobump-domains", NoBumpDomains);
            Text(nameof(PacLocal), "pac-local", PacLocal);
            Text(nameof(PacLocalSha256), "pac-local-sha256", PacLocalSha256);
            Text(nameof(ProxyAuthScheme), "proxy-auth-scheme", ProxyAuthScheme);
            Text(nameof(ProxySpn), "proxy-spn", ProxySpn);
            Text(nameof(ProxyTestingbot), "proxy-testingbot", ProxyTestingbot);
            Text(nameof(ProxyTestingbotUserpwd), "proxy-testingbot-userpwd", ProxyTestingbotUserpwd);
            List(nameof(SshHostKeys), "ssh-host-key", SshHostKeys);
            Text(nameof(SshHostKeyPolicy), "ssh-host-key-policy", SshHostKeyPolicy);
            Text(nameof(WsProxyMode), "ws-proxy-mode", WsProxyMode);

            Number(nameof(SshConnections), "ssh-connections", SshConnections);

            return arguments;
        }
    }

    internal sealed class TunnelArgument
    {
        public TunnelArgument(string property, string name, IReadOnlyList<string> values)
        {
            Property = property;
            Name = name;
            Values = values;
        }

        /// <summary>The name of the <see cref="TunnelOptions"/> property.</summary>
        public string Property { get; }

        /// <summary>The name on the command line of the tunnel, without dashes.</summary>
        public string Name { get; }

        /// <summary>Empty for a flag.</summary>
        public IReadOnlyList<string> Values { get; }
    }
}
