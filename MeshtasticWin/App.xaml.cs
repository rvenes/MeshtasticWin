using Microsoft.UI.Xaml;

namespace MeshtasticWin
{
    public partial class App : Application
    {
        public static MainWindow? MainWindowInstance { get; private set; }

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            Services.NodeLogArchive.EnsureBaseFolders();
            MainWindowInstance = new MainWindow();
            MainWindowInstance.Activate();
        }
    }
}
