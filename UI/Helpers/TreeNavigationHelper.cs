using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Safe WPF visual/logical tree parent traversal helper.
/// Handles <see cref="ContentElement"/> (such as <see cref="System.Windows.Documents.Run"/>,
/// <see cref="System.Windows.Documents.Hyperlink"/>, and other <see cref="System.Windows.Documents.Inline"/>s),
/// <see cref="Visual"/>, <see cref="Visual3D"/>, and arbitrary non-visual <see cref="DependencyObject"/>s
/// without calling <see cref="VisualTreeHelper.GetParent"/> on non-visual elements.
/// </summary>
public static class TreeNavigationHelper
{
    public static DependencyObject? GetParent(DependencyObject? element)
    {
        if (element == null)
        {
            return null;
        }

        if (element is ContentElement contentElement)
        {
            DependencyObject? parent = ContentOperations.GetParent(contentElement);
            if (parent != null)
            {
                return parent;
            }

            return contentElement is FrameworkContentElement fce
                ? fce.Parent ?? LogicalTreeHelper.GetParent(fce)
                : LogicalTreeHelper.GetParent(contentElement);
        }

        if (element is Visual || element is Visual3D)
        {
            DependencyObject? visualParent = VisualTreeHelper.GetParent(element);
            if (visualParent != null)
            {
                return visualParent;
            }
        }

        if (element is FrameworkElement fe)
        {
            return fe.Parent ?? LogicalTreeHelper.GetParent(fe);
        }

        return LogicalTreeHelper.GetParent(element);
    }

    public static T? FindAncestor<T>(DependencyObject? origin) where T : DependencyObject
    {
        DependencyObject? current = origin;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }
            current = GetParent(current);
        }
        return null;
    }

    public static bool HasAncestor(DependencyObject? origin, Func<DependencyObject, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        DependencyObject? current = origin;
        while (current != null)
        {
            if (predicate(current))
            {
                return true;
            }
            current = GetParent(current);
        }
        return false;
    }
}
