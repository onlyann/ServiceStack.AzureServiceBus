using ServiceStack.Configuration;
using System;

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

        public static TimeSpan ServerWaitTime = AppSettings.Get("ServerWaitTime", TimeSpan.FromSeconds(30));
    }
}
