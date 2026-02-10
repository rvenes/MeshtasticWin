using System;
using Microsoft.UI.Xaml;
using Microsoft.WindowsAppRuntime.Bootstrap;
using WinRT;

namespace MeshtasticWin;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Bootstrap.Initialize(0x00010008);
        ComWrappersSupport.InitializeComWrappers();

        try
        {
            Application.Start(_ =>
            {
                _ = new App();
            });
        }
        finally
        {
            Bootstrap.Shutdown();
        }
    }
}
