using Avalonia.Controls;
using System.ComponentModel;
using VariLab.ViewModels;

namespace VariLab.Views.Tabs;

public partial class CompStarsView : UserControl
{
    public CompStarsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CompStarsViewModel vm)
                vm.PropertyChanged += OnCompStarsViewModelPropertyChanged;
        };
    }

    // Same auto-scroll pattern as BatchView's Progress Log (issue #11) — read the length from
    // the ViewModel's own Log property, not LogTextBox.Text, since both this handler and the
    // XAML's {Binding Log} are subscribed to the same PropertyChanged event with no guaranteed
    // order; the ViewModel's property is always current by the time its own event fires.
    private void OnCompStarsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CompStarsViewModel.Log)) return;
        if (sender is CompStarsViewModel vm)
            LogTextBox.CaretIndex = vm.Log.Length;
    }
}
