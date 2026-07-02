using ZapretUI_Mobile.Services;
using ZapretUI_Mobile.ViewModels;
using ZapretUI_Mobile.Pages;

namespace ZapretUI_Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<XrayService>();
        builder.Services.AddSingleton<MainPageViewModel>();
        builder.Services.AddTransient<MainTabbedPage>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<StrategiesPage>();
        builder.Services.AddTransient<VpnPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<LogPage>();

        return builder.Build();
    }
}
