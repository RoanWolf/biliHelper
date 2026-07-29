using System.Windows;
using System.Windows.Media;

namespace BiliHelperWpf;

/// <summary>
/// WPF 可视化树遍历工具方法。
/// </summary>
public static class WpfHelper
{
    /// <summary>
    /// 在可视化树中向下查找第一个指定类型的子元素。
    /// </summary>
    public static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
            return null;

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T typedChild)
                return typedChild;

            var deeper = FindVisualChild<T>(child);
            if (deeper != null)
                return deeper;
        }

        return null;
    }
}
