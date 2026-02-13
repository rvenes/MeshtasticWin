using Microsoft.UI.Xaml.Controls;
using System;
using System.Reflection;
using Windows.ApplicationModel;

namespace MeshtasticWin.Pages;

public sealed partial class AboutPage : Page
{
    public string VersionText { get; }

    public AboutPage()
    {
        InitializeComponent();
        VersionText = $"Version: {ResolveVersion()}";
    }

    private static string ResolveVersion()
    {
        // Prefer MSIX package version when available, otherwise fall back to assembly version.
        try
        {
            var v = Package.Current.Id.Version;
            if (v.Major != 0 || v.Minor != 0 || v.Build != 0 || v.Revision != 0)
                return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            // Unpackaged contexts may not support Package.Current.
        }

        try
        {
            var asm = typeof(AboutPage).GetTypeInfo().Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return info;

            return asm.GetName().Version?.ToString() ?? "—";
        }
        catch
        {
            return "—";
        }
    }
}
