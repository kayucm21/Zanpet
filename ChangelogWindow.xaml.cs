using System.Windows;
using ZapretUI.Services;

namespace ZapretUI;

public partial class ChangelogWindow : Window
{
    public ChangelogWindow(string version, string changelog)
    {
        InitializeComponent();
        VersionText.Text = $"Версия {version}";
        ChangelogText.Text = changelog;
        Owner = Application.Current.MainWindow;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
