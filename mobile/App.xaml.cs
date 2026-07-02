using ZapretUI_Mobile.Services;
using ZapretUI_Mobile.ViewModels;
using ZapretUI_Mobile.Pages;

namespace ZapretUI_Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        if (activationState?.Context.Services is { } services)
        {
            var vm = services.GetRequiredService<MainPageViewModel>();
            var home = new HomePage(vm);
            var strategies = new StrategiesPage(vm);
            var vpn = new VpnPage(vm);
            var settings = new SettingsPage(vm);
            var log = new LogPage(vm);

            var tabbed = new TabbedPage
            {
                BarBackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#0D1117"),
                BarTextColor = Microsoft.Maui.Graphics.Color.FromArgb("#60A5FA"),
                Children = { home, strategies, vpn, settings, log }
            };
            return new Window(tabbed);
        }

        var vm2 = new MainPageViewModel(new XrayService());
        var tabbed2 = new TabbedPage
        {
            BarBackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#0D1117"),
            BarTextColor = Microsoft.Maui.Graphics.Color.FromArgb("#60A5FA"),
            Children =
            {
                new HomePage(vm2),
                new StrategiesPage(vm2),
                new VpnPage(vm2),
                new SettingsPage(vm2),
                new LogPage(vm2)
            }
        };
        return new Window(tabbed2);
    }
}
