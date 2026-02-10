using Microsoft.UI.Xaml.Controls;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        ShowPowerMetricsToggle.IsOn = AppState.ShowPowerMetricsTab;
        ShowDetectionSensorToggle.IsOn = AppState.ShowDetectionSensorLogTab;
    }

    private void ShowPowerMetricsToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AppState.ShowPowerMetricsTab = ShowPowerMetricsToggle.IsOn;
    }

    private void ShowDetectionSensorToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AppState.ShowDetectionSensorLogTab = ShowDetectionSensorToggle.IsOn;
    }
}
