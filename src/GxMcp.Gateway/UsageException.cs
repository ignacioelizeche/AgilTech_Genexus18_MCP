namespace GxMcp.Gateway
{
    public class UsageException : System.Exception
    {
        public string Code { get; }

        public UsageException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}
