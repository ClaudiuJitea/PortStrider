using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using PortStrider.UI.ViewModels;

namespace PortStrider.UI;

/// <summary>
/// Fallback view resolver for view models without an explicit DataTemplate.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = typeof(ViewModelBase).Assembly.GetType(name)
                   ?? Type.GetType($"{name}, {typeof(ViewModelBase).Assembly.FullName}");

        if (type is null)
            return new TextBlock { Text = "View not found: " + name };

        var control = (Control)Activator.CreateInstance(type)!;
        control.DataContext = param;
        return control;
    }

    public bool Match(object? data) => data is ViewModelBase;
}
