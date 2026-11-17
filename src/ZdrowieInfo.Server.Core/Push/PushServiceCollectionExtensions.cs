using Microsoft.Extensions.DependencyInjection;

namespace ZdrowieInfo.Server.Push;

public static class PushServiceCollectionExtensions
{
    public static IServiceCollection AddPush(this IServiceCollection services)
    {
        services.AddHttpClient(WebPushSender.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IPushSender, WebPushSender>();
        services.AddSingleton<VapidKeyManager>();
        services.AddSingleton<PushNotifier>();
        services.AddSingleton<NotificationDispatcher>();
        return services;
    }
}
