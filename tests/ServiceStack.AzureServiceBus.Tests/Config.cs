using ServiceStack.Configuration;

namespace ServiceStack.AzureServiceBus.Tests
{
    public class Config
    {
        public static IAppSettings AppSettings = new AppSettings();

        public static string AzureBusConnectionString = AppSettings.GetString("AzureBusConnectionString");
    }
}
