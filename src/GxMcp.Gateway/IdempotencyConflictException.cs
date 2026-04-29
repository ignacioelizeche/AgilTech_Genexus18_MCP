using System;

namespace GxMcp.Gateway
{
    public class IdempotencyConflictException : Exception
    {
        public IdempotencyConflictException(string message) : base(message) { }
    }
}
