using MauiRemoteAdb.Views;

namespace MauiRemoteAdb;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        MainPage = new NavigationPage(new ConnectPage());
    }
}
