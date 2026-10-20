using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PureUninstall.App.ViewModels;
using PureUninstall.Core.Models;

namespace PureUninstall.App.ViewModels;

/// <summary>UI wrapper around AppEntry with an extracted icon and formatted subtitle.</summary>
public sealed class AppItemViewModel : ObservableObject
{
    public AppEntry Entry { get; }

    public AppItemViewModel(AppEntry entry)
    {
        Entry = entry;
    }

    public string DisplayName => Entry.DisplayName;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Entry.DisplayVersion)) parts.Add("v" + Entry.DisplayVersion);
            if (!string.IsNullOrWhiteSpace(Entry.Publisher)) parts.Add(Entry.Publisher);
            if (Entry.InstallDate is not null) parts.Add(Entry.InstallDate.Value.ToString("yyyy-MM-dd"));
            return string.Join(" · ", parts);
        }
    }

    public string SizeText => Entry.SizeText;
    public string KindText => Entry.KindText;

    private ImageSource? _icon;
    public ImageSource? IconImage
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }
}
