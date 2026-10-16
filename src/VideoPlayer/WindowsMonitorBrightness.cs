using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace VideoPlayer;

internal sealed class WindowsMonitorBrightness : IDisposable
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    private readonly IntPtr _windowHandle;
    private readonly Channel<int> _brightnessChannel;
    private readonly CancellationTokenSource _cts;

    public bool IsSupported { get; private set; }

    public WindowsMonitorBrightness(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _cts = new CancellationTokenSource();
        _brightnessChannel = Channel.CreateBounded<int>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        IsSupported = CheckSupport();
        _ = Task.Run(ProcessBrightnessQueueAsync);
    }

    public int? GetCurrentBrightness()
    {
        if (!OperatingSystem.IsWindows() || _windowHandle == IntPtr.Zero) return null;

        try
        {
            var hMonitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
            if (hMonitor == IntPtr.Zero) return null;

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                return null;

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                return null;

            try
            {
                if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out var min, out var cur, out var max))
                {
                    if (max > min)
                    {
                        var percentage = (int)Math.Round((double)(cur - min) * 100.0 / (max - min));
                        return Math.Clamp(percentage, 0, 100);
                    }
                    return (int)cur;
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to get physical monitor brightness", ex);
        }

        return null;
    }

    public void RequestBrightness(int percentage)
    {
        if (!IsSupported) return;
        _brightnessChannel.Writer.TryWrite(Math.Clamp(percentage, 0, 100));
    }

    private bool CheckSupport() => GetCurrentBrightness().HasValue;

    private async Task ProcessBrightnessQueueAsync()
    {
        var reader = _brightnessChannel.Reader;
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(token).ConfigureAwait(false))
                    break;

                while (reader.TryRead(out var targetBrightness))
                {
                    ApplyPhysicalBrightness(targetBrightness);
                    await Task.Delay(40, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error in brightness worker loop", ex);
            }
        }
    }

    private void ApplyPhysicalBrightness(int percentage)
    {
        if (!OperatingSystem.IsWindows() || _windowHandle == IntPtr.Zero) return;

        try
        {
            var hMonitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
            if (hMonitor == IntPtr.Zero) return;

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                return;

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                return;

            try
            {
                for (var i = 0; i < count; i++)
                {
                    if (GetMonitorBrightness(monitors[i].hPhysicalMonitor, out var min, out _, out var max))
                    {
                        var actualValue = min + (uint)Math.Round((double)(max - min) * percentage / 100.0);
                        SetMonitorBrightness(monitors[i].hPhysicalMonitor, actualValue);
                    }
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to apply monitor brightness", ex);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _brightnessChannel.Writer.TryComplete();
        _cts.Dispose();
    }
}

