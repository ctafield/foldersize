using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FolderSize.App;

public sealed class FolderWebSearch(Func<string>? defaultBrowser = null, Action<ProcessStartInfo>? launch = null)
{
    public void Search(string folderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        var browser = defaultBrowser is not null ? defaultBrowser() : ResolveDefaultBrowser();
        var executable = Path.GetFileName(browser).ToLowerInvariant();
        var info = new ProcessStartInfo { FileName = browser, UseShellExecute = false };
        var query = $"{folderName} folder";
        switch (executable)
        {
            case "firefox.exe":
                info.ArgumentList.Add("-search");
                info.ArgumentList.Add(query);
                break;
            case "chrome.exe":
            case "msedge.exe":
            case "brave.exe":
            case "opera.exe":
            case "vivaldi.exe":
            case "chromium.exe":
                info.ArgumentList.Add($"? {query}");
                break;
            default:
                throw new NotSupportedException($"Default-search-engine launching is not supported for {executable}.");
        }
        if (launch is not null) launch(info);
        else
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Windows did not start the default web browser.");
        }
    }

    private static string ResolveDefaultBrowser()
    {
        const uint isProtocol = 0x1000;
        const uint executable = 2;
        uint length = 0;
        var result = AssocQueryStringW(isProtocol, executable, "https", "open", null, ref length);
        Marshal.ThrowExceptionForHR(result);
        if (length == 0) throw new InvalidOperationException("Windows did not identify the default web browser.");
        var buffer = new StringBuilder(checked((int)length));
        result = AssocQueryStringW(isProtocol, executable, "https", "open", buffer, ref length);
        Marshal.ThrowExceptionForHR(result);
        if (result != 0 || buffer.Length == 0)
            throw new InvalidOperationException("Windows could not resolve the default web browser executable.");
        return buffer.ToString();
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint str, string association, string extra,
        StringBuilder? output, ref uint length);
}
