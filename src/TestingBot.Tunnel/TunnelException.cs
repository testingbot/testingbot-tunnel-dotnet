using System;

namespace TestingBot
{
    /// <summary>
    /// TestingBot Tunnel could not be downloaded, started or stopped.
    /// The message says why, in the words of the tunnel when it gave a reason.
    /// </summary>
    public class TunnelException : Exception
    {
        /// <summary>Create the exception.</summary>
        public TunnelException(string message) : base(message)
        {
        }

        /// <summary>Create the exception with the error that caused it.</summary>
        public TunnelException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
