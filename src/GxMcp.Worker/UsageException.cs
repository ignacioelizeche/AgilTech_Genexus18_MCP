using System;

namespace GxMcp.Worker
{
    public class UsageException : Exception
    {
        public string Code { get; }

        public UsageException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}
