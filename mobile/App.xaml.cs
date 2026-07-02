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
            var tabbed = services.GetRequiredService<MainTabbedPage>();
            return new Window(tabbed);
        }
        // Fallback (shouldn't happen)
        var vm = new MainPageViewModel(new XrayService());
        return new Window(new MainTabbedPage(vm));
    }
}
