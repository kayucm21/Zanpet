using Microsoft.Extensions.Logging;
using ZapretUI_Mobile.Services;
using ZapretUI_Mobile.ViewModels;

namespace ZapretUI_Mobile;

public partial class App : Application
{
    private readonly MainPageViewModel _viewModel;

    public App(MainPageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new MainPage(_viewModel);
        return new Window(page);
    }
}
