using ZapretUI_Mobile.ViewModels;

namespace ZapretUI_Mobile.Pages;

public partial class StrategiesPage : ContentPage
{
    public StrategiesPage(MainPageViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
