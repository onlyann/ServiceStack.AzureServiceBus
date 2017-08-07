using ServiceStack.Configuration;

namespace ServiceStack.AzureServiceBus.Tests
{
    public class Config
    {
        public const string ServiceStackBaseUri = "http://localhost:20000";
        public const string AbsoluteBaseUri = ServiceStackBaseUri + "/";
        public const string ListeningOn = ServiceStackBaseUri + "/";

        public static IAppSettings AppSettings = new MultiAppSettings(
                new EnvironmentVariableSettings(),
                new AppSettings()
            );

        public static string AzureBusConnectionString = AppSettings.GetString("AzureBusConnectionString");
    }
}
