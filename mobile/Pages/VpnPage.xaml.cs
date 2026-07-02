using ZapretUI_Mobile.Models;
using ZapretUI_Mobile.ViewModels;

namespace ZapretUI_Mobile.Pages;

public partial class VpnPage : ContentPage
{
    private MainPageViewModel? _vm;

    public VpnPage()
    {
        InitializeComponent();
    }

    public VpnPage(MainPageViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        _vm = vm;
    }

    private async void OnVpnConnectClicked(object? sender, EventArgs e)
    {
        if (_vm != null && sender is Button btn && btn.BindingContext is VpnServer server)
        {
            await _vm.VpnConnectFromServer(server);
        }
    }
}
