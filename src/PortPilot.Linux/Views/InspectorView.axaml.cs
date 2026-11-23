using Avalonia.Controls;
using Avalonia.Interactivity;
using PortPilot.App.ViewModels;

namespace PortPilot.Linux.Views;

public sealed partial class InspectorView : UserControl
{
    public InspectorView() => InitializeComponent();

    private void OnSiblingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PortRowViewModel row } && DataContext is MainViewModel vm)
        {
            vm.InspectCommand.Execute(row);
        }
    }
}
