using ZapretUI_Mobile.ViewModels;

namespace ZapretUI_Mobile.Pages;

public partial class LogPage : ContentPage
{
    private MainPageViewModel? _vm;

    public LogPage()
    {
        InitializeComponent();
    }

    public LogPage(MainPageViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        _vm = vm;
        _vm.LogUpdated += OnLogUpdated;
    }

    private void OnLogUpdated(object? sender, string log)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LogScroll.ScrollToAsync(0, double.MaxValue, false);
        });
    }
}
