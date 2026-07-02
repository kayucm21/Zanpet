using ZapretUI_Mobile.ViewModels;

namespace ZapretUI_Mobile.Pages;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsPage(MainPageViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
