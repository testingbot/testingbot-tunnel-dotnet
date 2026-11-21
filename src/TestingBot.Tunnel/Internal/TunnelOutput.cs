using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestingBot.Internal
{
    /// <summary>Reading what the tunnel writes.</summary>
    internal static class TunnelOutput
    {
        // The tunnel writes everything the TestingBot API refuses behind this prefix, spelled the way the tunnel spells it
        public const string TunnelErrorPrefix = "An error ocurred:";

        // How many lines of tunnel output to keep for a tunnel that fails to start
        public const int MaxRememberedLines = 5;

        // What TestingBot Tunnel 5.0 and up write when Java is too old for them
        public static readonly Regex JavaRequirement = new Regex(@"TestingBot Tunnel requires Java \d+ or higher", RegexOptions.CultureInvariant);

        // How TestingBot Tunnel 5.0 and up report the TestingBot API refusing to create a tunnel,
        // e.g. 'Failed : HTTP error code : 401 - {"error":"401 Unauthorized. ..."}'
        private static readonly Regex HttpError = new Regex(@"HTTP error code : (\d+)(?: - (.*))?$", RegexOptions.CultureInvariant);

        private static readonly Regex Checksum = new Regex(@"^([a-f0-9]{64})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Turn a line the tunnel wrote into the reason it could not start, null when the line is not an error.
        /// </summary>
        public static string? ClassifyTunnelError(string line)
        {
            if (line.IndexOf("401 Unauthorized", StringComparison.Ordinal) > -1)
            {
                return "Invalid credentials. Please supply the correct key/secret obtained from TestingBot.com";
            }

            if (line.IndexOf("minutes left", StringComparison.Ordinal) > -1)
            {
                return "You do not have any minutes left. Please upgrade your account at TestingBot.com";
            }

            if (line.StartsWith(TunnelErrorPrefix, StringComparison.Ordinal))
            {
                var reason = line.Substring(TunnelErrorPrefix.Length).Trim();
                return reason.Length > 0 ? reason : line;
            }

            // Tunnel 5.0 and up pass on the answer of the API, which explains itself
            var httpError = HttpError.Match(line);
            if (httpError.Success)
            {
                return DescribeApiRefusal(httpError.Groups[1].Value, httpError.Groups[2].Success ? httpError.Groups[2].Value : null);
            }

            // Reported without the prefix when the tunnel can not reach TestingBot at all.
            // Tunnel 5.0 and up write this first and the reason on a later line, which replaces it.
            if (line.StartsWith("Creating a new tunnel failed", StringComparison.Ordinal))
            {
                return line;
            }

            if (JavaRequirement.IsMatch(line))
            {
                return line;
            }

            return null;
        }

        /// <summary>The reason the TestingBot API gave for not creating a tunnel.</summary>
        public static string DescribeApiRefusal(string statusCode, string? body)
        {
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    using var document = JsonDocument.Parse(body!);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("error", out var error)
                        && error.ValueKind == JsonValueKind.String
                        && error.GetString()!.Trim().Length > 0)
                    {
                        return error.GetString()!.Trim();
                    }
                }
                catch (JsonException)
                {
                    // Not JSON, the answer is passed on as it is
                }
                return body!.Trim();
            }

            return $"TestingBot refused to create the tunnel, HTTP status {statusCode}";
        }

        /// <summary>
        /// The lines of a message the tunnel wrote. With LogFormat "json" tunnel 5.0 and up write
        /// every message as a JSON object, which can hold several lines.
        /// </summary>
        public static IReadOnlyList<string> ParseLogLine(string line)
        {
            if (!line.StartsWith("{", StringComparison.Ordinal))
            {
                return new[] { line };
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return Regex.Split(message.GetString()!, "\r?\n").Select(entry => entry.Trim()).ToArray();
                }
            }
            catch (JsonException)
            {
                // Not a log record, but output that happens to start with a brace
            }

            return new[] { line };
        }

        /// <summary>
        /// Describe why the tunnel did not start. Whatever it wrote last is added to the exit code,
        /// so the caller is not left with a number.
        /// </summary>
        public static string DescribeStartupFailure(string? error, int? exitCode, IEnumerable<string> output)
        {
            if (!string.IsNullOrEmpty(error))
            {
                return error!;
            }

            var message = $"Could not start TestingBot Tunnel. Exit code {(exitCode.HasValue ? exitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}";
            var lines = output.Where(line => line.Length > 0).ToList();
            return lines.Count == 0 ? message : message + "\n" + string.Join("\n", lines);
        }

        /// <summary>Replace the API key and secret with a placeholder.</summary>
        public static string Redact(string value, TestingBotCredentials? credentials)
        {
            if (credentials == null || string.IsNullOrEmpty(value))
            {
                return value;
            }

            // The secret first: a key that is part of the secret would otherwise leave the rest of it behind
            foreach (var secret in new[] { credentials.Secret, credentials.Key })
            {
                if (secret.Trim().Length > 0)
                {
                    value = value.Replace(secret, "***");
                }
            }
            return value;
        }

        /// <summary>Read the checksum from a .sha256 file, as written by sha256sum. Null when there is none.</summary>
        public static string? ParseChecksum(string contents)
        {
            var match = Checksum.Match((contents ?? string.Empty).Trim());
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }
    }
}
