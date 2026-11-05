using Windows.UI;
using Microsoft.UI.Xaml.Media;
using PureUninstall.Core.Models;

namespace PureUninstall.App.ViewModels;

/// <summary>
/// 持久化项的界面包装。
/// 「是不是可疑」的判断在 Core 里做，这里只负责把结论翻译成颜色和一行说明。
/// </summary>
public sealed class PersistenceRow
{
    public PersistenceItem Item { get; }

    public PersistenceRow(PersistenceItem item) => Item = item;

    public string Name => Item.Name;
    public string KindText => Item.KindText;
    public string Command => Item.Command;
    public string SignerText => "签名：" + Item.SignerText;
    public string RiskText => Item.RiskText;
    public bool Selected { get; set; }

    public string ReasonText => Item.Reasons.Count == 0
        ? "未发现异常特征"
        : string.Join("；", Item.Reasons);

    public SolidColorBrush RiskBg => Item.Risk switch
    {
        RiskLevel.High => new SolidColorBrush(Color.FromArgb(255, 0xFD, 0xE7, 0xE9)),
        RiskLevel.Medium => new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xF4, 0xE5)),
        _ => new SolidColorBrush(Color.FromArgb(255, 0xF1, 0xF3, 0xF6)),
    };

    public SolidColorBrush RiskFg => Item.Risk switch
    {
        RiskLevel.High => new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x2B, 0x1C)),
        RiskLevel.Medium => new SolidColorBrush(Color.FromArgb(255, 0x9D, 0x5D, 0x00)),
        _ => new SolidColorBrush(Color.FromArgb(255, 0x44, 0x4A, 0x55)),
    };
}
