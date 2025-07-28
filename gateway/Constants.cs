namespace Gateway
{
    public static class Constants
    {
        public const string DatabaseSocket = "/sockets/database.sock";
        public const string DefaultProcessorUrl = "http://payment-processor-default:8080/payments";
        public const string FallbackProcessorUrl = "http://payment-processor-fallback:8080/payments";
    }
}
