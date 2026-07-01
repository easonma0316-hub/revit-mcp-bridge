using System;

namespace RevitMCP.Addin
{
    /// <summary>
    /// A tool error carrying a stable, machine-readable code alongside the human
    /// message. The MCP client (the LLM) can branch on the code instead of parsing
    /// prose, which is a core MCP best practice: tell the agent *what to do next*,
    /// not just that something failed.
    /// </summary>
    public class McpException : Exception
    {
        public string Code { get; }

        public McpException(string code, string message) : base(message)
        {
            Code = code;
        }

        public McpException(string code, string message, Exception inner) : base(message, inner)
        {
            Code = code;
        }

        // Common codes, kept in one place so C# and the docs stay in sync.
        public const string NoDocument      = "NO_DOCUMENT";
        public const string NotFound        = "NOT_FOUND";
        public const string BadRequest      = "BAD_REQUEST";
        public const string ReadOnly        = "READ_ONLY";
        public const string ReadOnlyParam   = "READ_ONLY_PARAM";
        public const string Cancelled       = "CANCELLED";
        public const string UnknownCommand  = "UNKNOWN_COMMAND";
        public const string Unsupported     = "UNSUPPORTED";
        public const string Internal        = "INTERNAL";
    }
}
