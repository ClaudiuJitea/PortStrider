using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PortStrider.UI.ViewModels;

namespace PortStrider.UI.Views;

public partial class WifiView : UserControl
{
    public WifiView()
    {
        InitializeComponent();
        SpectrumChart.NetworkSelected += (_, network) =>
        {
            if (DataContext is WifiViewModel vm) vm.SelectedNetwork = network;
        };
    }
    private void ZoomIn(object? sender, RoutedEventArgs e) => SpectrumChart.ZoomIn();
    private void ZoomOut(object? sender, RoutedEventArgs e) => SpectrumChart.ZoomOut();
    private void FitNetworks(object? sender, RoutedEventArgs e) => SpectrumChart.FitNetworks();
    private void FullBand(object? sender, RoutedEventArgs e) => SpectrumChart.ShowFullBand();
    private void FocusSelected(object? sender, RoutedEventArgs e) => SpectrumChart.FocusSelected();
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is WifiViewModel vm) vm.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
