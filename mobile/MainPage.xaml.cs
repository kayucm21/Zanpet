using ZapretUI_Mobile.ViewModels;

namespace ZapretUI_Mobile;

public partial class MainPage : ContentPage
{
    private MainPageViewModel _viewModel;

    public MainPage(MainPageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;

        _viewModel.LogUpdated += OnLogUpdated;
    }

    private void OnLogUpdated(object? sender, string log)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LogScroll.ScrollToAsync(0, double.MaxValue, false);
        });
    }
}