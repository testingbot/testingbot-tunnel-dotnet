using System;
using System.IO;

namespace TestingBot
{
    /// <summary>
    /// A TestingBot key and secret, as found by <see cref="Resolve(string?, string?)"/>.
    /// Use it to build the hub URL for your remote WebDriver with the same credentials the tunnel uses.
    /// </summary>
    public sealed class TestingBotCredentials
    {
        /// <summary>Create credentials.</summary>
        public TestingBotCredentials(string key, string secret)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("ApiKey cannot be empty", nameof(key));
            }
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new ArgumentException("ApiSecret cannot be empty", nameof(secret));
            }
            Key = key;
            Secret = secret;
        }

        /// <summary>The TestingBot API key.</summary>
        public string Key { get; }

        /// <summary>The TestingBot API secret.</summary>
        public string Secret { get; }

        /// <summary>Where the credentials were found, e.g. "TB_KEY/TB_SECRET" or "~/.testingbot".</summary>
        public string Source { get; private set; } = "options";

        /// <summary>The key, with the secret redacted.</summary>
        public override string ToString() => $"{Key}:***";

        /// <summary>
        /// Find the TestingBot credentials: the key and secret given, then the TB_KEY/TB_SECRET
        /// and TESTINGBOT_KEY/TESTINGBOT_SECRET environment variables, then ~/.testingbot holding "key:secret".
        /// </summary>
        /// <returns>null when there are none.</returns>
        /// <exception cref="ArgumentException">Only one of key and secret is given, or one is empty.</exception>
        public static TestingBotCredentials? Resolve(string? apiKey = null, string? apiSecret = null)
            => Resolve(apiKey, apiSecret, Environment.GetEnvironmentVariable, HomeDirectory());

        internal static TestingBotCredentials? Resolve(string? apiKey, string? apiSecret, Func<string, string?> getEnvironmentVariable, string? homeDirectory)
        {
            if (apiKey != null && apiKey.Trim().Length == 0)
            {
                throw new ArgumentException("ApiKey cannot be empty");
            }
            if (apiSecret != null && apiSecret.Trim().Length == 0)
            {
                throw new ArgumentException("ApiSecret cannot be empty");
            }
            if ((apiKey == null) != (apiSecret == null))
            {
                throw new ArgumentException("ApiKey and ApiSecret must be given together");
            }
            if (apiKey != null)
            {
                return new TestingBotCredentials(apiKey, apiSecret!);
            }

            foreach (var (keyVariable, secretVariable) in new[] { ("TB_KEY", "TB_SECRET"), ("TESTINGBOT_KEY", "TESTINGBOT_SECRET") })
            {
                var key = getEnvironmentVariable(keyVariable);
                var secret = getEnvironmentVariable(secretVariable);
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(secret))
                {
                    return new TestingBotCredentials(key!.Trim(), secret!.Trim()) { Source = $"{keyVariable}/{secretVariable}" };
                }
            }

            if (homeDirectory == null)
            {
                return null;
            }

            var file = Path.Combine(homeDirectory, ".testingbot");
            string contents;
            try
            {
                if (!File.Exists(file))
                {
                    return null;
                }
                contents = File.ReadAllText(file).Trim();
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            var separator = contents.IndexOf(':');
            if (separator <= 0 || separator == contents.Length - 1)
            {
                return null;
            }

            return new TestingBotCredentials(contents.Substring(0, separator).Trim(), contents.Substring(separator + 1).Trim()) { Source = "~/.testingbot" };
        }

        internal static string? HomeDirectory()
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            return string.IsNullOrEmpty(home) ? null : home;
        }
    }
}
