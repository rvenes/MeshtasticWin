using System;
using Windows.ApplicationModel.DataTransfer;

namespace MeshtasticWin.Services;

public static class ClipboardUtil
{
    public static bool TrySetText(string? text, bool flush = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);

            if (flush)
            {
                try { Clipboard.Flush(); }
                catch { /* Clipboard can be locked; ignore. */ }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

