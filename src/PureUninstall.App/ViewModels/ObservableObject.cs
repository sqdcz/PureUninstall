using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PureUninstall.App.ViewModels;

/// <summary>Minimal INotifyPropertyChanged base (no external MVVM packages).</summary>
public partial class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
