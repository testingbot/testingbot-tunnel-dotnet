using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace TestingBot.Internal
{
    internal static class FileSystem
    {
        /// <summary>Move a file over another one.</summary>
        public static void Replace(string source, string destination)
        {
#if NETSTANDARD2_0
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            File.Move(source, destination);
#else
            File.Move(source, destination, overwrite: true);
#endif
        }

        public static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Nothing we can do about a file we can not remove
            }
            catch (UnauthorizedAccessException)
            {
                // Same
            }
        }

        public static void TryDeleteDirectory(string? directory)
        {
            if (directory == null)
            {
                return;
            }
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Nothing we can do about a directory we can not remove
            }
            catch (UnauthorizedAccessException)
            {
                // Same
            }
        }

        /// <summary>Whether files can be created in a directory, creating it when it does not exist.</summary>
        public static bool IsWritableDirectory(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, $".write-test.{Downloader.CurrentProcessId()}.{Downloader.RandomSuffix()}");
                using (File.Create(probe, 1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>A new, private directory in the temp directory.</summary>
        public static string CreateTempDirectory(string prefix)
        {
            while (true)
            {
                var directory = Path.Combine(Path.GetTempPath(), prefix + Downloader.RandomSuffix());
                if (Directory.Exists(directory))
                {
                    continue;
                }
                var info = Directory.CreateDirectory(directory);
#if NET8_0_OR_GREATER
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
#endif
                return info.FullName;
            }
        }

        public static async Task<string> Sha256Async(string file)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var sha = SHA256.Create();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha.Hash!).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
