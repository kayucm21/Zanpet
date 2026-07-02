using ZapretUI_Mobile.ViewModels;

namespace ZapretUI_Mobile.Pages;

public partial class HomePage : ContentPage
{
    public HomePage(MainPageViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
