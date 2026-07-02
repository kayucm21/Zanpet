using ZapretUI_Mobile.Models;
using ZapretUI_Mobile.ViewModels;

namespace ZapretUI_Mobile.Pages;

public partial class VpnPage : ContentPage
{
    private readonly MainPageViewModel _vm;

    public VpnPage(MainPageViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        _vm = vm;
    }

    private async void OnVpnConnectClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.BindingContext is VpnServer server)
        {
            await _vm.VpnConnectFromServer(server);
        }
    }
}
