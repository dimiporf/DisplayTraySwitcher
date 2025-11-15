using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DisplayTraySwitcher
{
    /// <summary>
    /// Display manager based on the original v3 behaviour:
    /// - Uses the monitor layout (positions/resolutions) captured at startup as a baseline.
    /// - Only toggles monitors on/off; never rearranges or repositions them.
    /// - For each layout request it can try the same configuration a few times in one call,
    ///   because some drivers seem to only enable one or two displays per cycle.
    ///
    /// In addition it logs detailed information about what it asked Windows to do
    /// and what Windows reports afterwards. You can see this in the Visual Studio
    /// Output window (Debug) when running under the debugger.
    /// </summary>
    public class DisplayManager
    {
        #region Win32 interop

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int ENUM_REGISTRY_SETTINGS = -2;

        private const int DM_POSITION = 0x00000020;
        private const int DM_PELSWIDTH = 0x00080000;
        private const int DM_PELSHEIGHT = 0x00100000;
        private const int DM_DISPLAYFREQUENCY = 0x00400000;
        private const int DM_BITSPERPEL = 0x00040000;

        private const int CDS_UPDATEREGISTRY = 0x00000001;
        private const int CDS_NORESET = 0x10000000;

        private const int EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

        [Flags]
        private enum DisplayDeviceStateFlags : int
        {
            ATTACHED_TO_DESKTOP = 0x00000001,
            MULTI_DRIVER = 0x00000002,
            PRIMARY_DEVICE = 0x00000004,
            MIRRORING_DRIVER = 0x00000008,
            VGA_COMPATIBLE = 0x00000010,
            REMOVABLE = 0x00000020,
            MODESPRUNED = 0x08000000,
            REMOTE = 0x04000000,
            DISCONNECT = 0x02000000
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DISPLAY_DEVICE
        {
            public int cb;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;

            public DisplayDeviceStateFlags StateFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;

            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;

            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;

            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(
            string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplaySettingsEx(
            string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int ChangeDisplaySettingsEx(
            string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int ChangeDisplaySettingsEx(
            string lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        #endregion

        /// <summary>
        /// Baseline snapshot per physical device, captured at startup.
        /// </summary>
        private class BaselineDisplay
        {
            public string DeviceName;
            public bool IsPrimary;
            public Rectangle Bounds;
            public DEVMODE Mode;
        }

        private readonly List<BaselineDisplay> _baselineDisplays = new List<BaselineDisplay>();

        public class LayoutResult
        {
            public string LayoutName { get; set; }
            public bool Success { get; set; }
            public int ExpectedActive { get; set; }
            public int ActualActive { get; set; }
            public string Message { get; set; }
        }

        public DisplayManager()
        {
            CaptureBaseline();
        }

        /// <summary>
        /// Capture the baseline configuration (positions + modes) for all display
        /// devices when the tray app starts. This should reflect your "all screens"
        /// layout in Windows.
        /// </summary>
        private void CaptureBaseline()
        {
            _baselineDisplays.Clear();

            uint devNum = 0;
            while (true)
            {
                DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(dd);

                if (!EnumDisplayDevices(null, devNum, ref dd, EDD_GET_DEVICE_INTERFACE_NAME))
                    break;

                DEVMODE mode = new DEVMODE();
                mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                bool ok = EnumDisplaySettingsEx(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref mode, 0);
                if (!ok)
                {
                    ok = EnumDisplaySettingsEx(dd.DeviceName, ENUM_REGISTRY_SETTINGS, ref mode, 0);
                }

                if (ok)
                {
                    var bounds = new Rectangle(
                        mode.dmPositionX,
                        mode.dmPositionY,
                        mode.dmPelsWidth,
                        mode.dmPelsHeight);

                    var b = new BaselineDisplay
                    {
                        DeviceName = dd.DeviceName,
                        IsPrimary = dd.StateFlags.HasFlag(DisplayDeviceStateFlags.PRIMARY_DEVICE),
                        Bounds = bounds,
                        Mode = mode
                    };
                    _baselineDisplays.Add(b);

                    Debug.WriteLine($"[Baseline] {dd.DeviceName} primary={b.IsPrimary} bounds={bounds} " +
                                    $"res={mode.dmPelsWidth}x{mode.dmPelsHeight} bpp={mode.dmBitsPerPel} freq={mode.dmDisplayFrequency}");
                }

                devNum++;
            }

            Debug.WriteLine($"[Baseline] Captured {_baselineDisplays.Count} display(s).");
        }

        /// <summary>
        /// Runtime view of displays for validation.
        /// </summary>
        private class RuntimeDisplay
        {
            public string DeviceName;
            public bool AttachedToDesktop;
            public Rectangle Bounds;
            public int Width;
            public int Height;
            public int Bits;
            public int Frequency;
        }

        private IList<RuntimeDisplay> EnumerateRuntimeDisplays()
        {
            var result = new List<RuntimeDisplay>();

            uint devNum = 0;
            while (true)
            {
                DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(dd);

                if (!EnumDisplayDevices(null, devNum, ref dd, EDD_GET_DEVICE_INTERFACE_NAME))
                    break;

                bool attached = dd.StateFlags.HasFlag(DisplayDeviceStateFlags.ATTACHED_TO_DESKTOP);

                DEVMODE mode = new DEVMODE();
                mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                bool ok = EnumDisplaySettingsEx(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref mode, 0);
                if (!ok)
                {
                    ok = EnumDisplaySettingsEx(dd.DeviceName, ENUM_REGISTRY_SETTINGS, ref mode, 0);
                }

                if (!ok)
                {
                    devNum++;
                    continue;
                }

                var bounds = new Rectangle(
                    mode.dmPositionX,
                    mode.dmPositionY,
                    mode.dmPelsWidth,
                    mode.dmPelsHeight);

                result.Add(new RuntimeDisplay
                {
                    DeviceName = dd.DeviceName,
                    AttachedToDesktop = attached,
                    Bounds = bounds,
                    Width = mode.dmPelsWidth,
                    Height = mode.dmPelsHeight,
                    Bits = mode.dmBitsPerPel,
                    Frequency = mode.dmDisplayFrequency
                });

                devNum++;
            }

            return result;
        }

        private BaselineDisplay GetPrimaryBaseline()
        {
            var primary = _baselineDisplays.FirstOrDefault(d => d.IsPrimary);
            return primary ?? _baselineDisplays.FirstOrDefault();
        }

        /// <summary>
        /// Finds the display "above" the primary based on the baseline layout.
        /// </summary>
        private BaselineDisplay GetAboveBaseline(BaselineDisplay primary)
        {
            if (primary == null)
                return null;

            int primaryLeft = primary.Bounds.Left;
            int primaryRight = primary.Bounds.Right;
            int primaryTop = primary.Bounds.Top;

            var candidates = _baselineDisplays
                .Where(d => !ReferenceEquals(d, primary))
                .Select(d => new
                {
                    Display = d,
                    CenterX = d.Bounds.Left + d.Bounds.Width / 2.0,
                    CenterY = d.Bounds.Top + d.Bounds.Height / 2.0
                })
                .ToList();

            var verticalAbove = candidates
                .Where(c => c.CenterX >= primaryLeft && c.CenterX <= primaryRight && c.CenterY < primaryTop)
                .OrderByDescending(c => c.CenterY)
                .FirstOrDefault();

            if (verticalAbove != null)
                return verticalAbove.Display;

            var topMost = candidates
                .OrderBy(c => c.CenterY)
                .FirstOrDefault();

            return topMost?.Display;
        }

        public Task<LayoutResult> ApplyMainOnlyAsync()
        {
            return Task.Run(() =>
            {
                var primary = GetPrimaryBaseline();
                if (primary == null)
                {
                    return new LayoutResult
                    {
                        LayoutName = "Main screen only",
                        Success = false,
                        ExpectedActive = 1,
                        ActualActive = 0,
                        Message = "No primary display detected in baseline."
                    };
                }

                var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    primary.DeviceName
                };

                return ApplyLayoutByDeviceNames(active, "Main screen only");
            });
        }

        public Task<LayoutResult> ApplyMainAndAboveAsync()
        {
            return Task.Run(() =>
            {
                var primary = GetPrimaryBaseline();
                if (primary == null)
                {
                    return new LayoutResult
                    {
                        LayoutName = "Main + above",
                        Success = false,
                        ExpectedActive = 0,
                        ActualActive = 0,
                        Message = "No primary display detected in baseline."
                    };
                }

                var above = GetAboveBaseline(primary);

                var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            primary.DeviceName
        };
                if (above != null)
                {
                    active.Add(above.DeviceName);
                }

                // First try direct main+above as requested.
                var result = ApplyLayoutByDeviceNames(active, "Main + above");

                // Some drivers refuse to light the "above" monitor when coming
                // from a pure single-monitor (main only) state. In that case we
                // fall back to briefly enabling all screens and then re-applying
                // the main+above layout.
                if (!result.Success)
                {
                    var allActive = new HashSet<string>(
                        _baselineDisplays.Select(b => b.DeviceName),
                        StringComparer.OrdinalIgnoreCase);

                    // This may cause a very quick "flash" of all monitors.
                    ApplyLayoutByDeviceNames(allActive, "All screens (fallback)");

                    // And then try again to go back to the requested 2-screen setup.
                    result = ApplyLayoutByDeviceNames(active, "Main + above (retry)");
                }

                return result;
            });
        }


        public Task<LayoutResult> ApplyAllScreensAsync()
        {
            return Task.Run(() =>
            {
                var active = new HashSet<string>(_baselineDisplays.Select(b => b.DeviceName),
                    StringComparer.OrdinalIgnoreCase);

                return ApplyLayoutByDeviceNames(active, "All screens");
            });
        }

        /// <summary>
        /// Apply a layout by specifying which baseline device names should be active.
        /// We:
        /// - Reuse the baseline DEVMODE (positions & resolutions) when enabling.
        /// - Set width/height to 0 when disabling.
        /// - Do up to 3 attempts in one call, because in practice some drivers only
        ///   partially apply changes per cycle.
        /// - After each attempt, log the resulting runtime state.
        /// </summary>
        private LayoutResult ApplyLayoutByDeviceNames(HashSet<string> activeDevices, string layoutName)
        {
            const int maxAttempts = 3;
            const int defaultWidth = 1024;
            const int defaultHeight = 768;

            int expectedActive = activeDevices.Count;
            int actualActive = 0;
            bool success = false;

            Debug.WriteLine($"[{layoutName}] Requested active devices: {string.Join(", ", activeDevices)}");

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Debug.WriteLine($"[{layoutName}] Attempt {attempt} starting.");

                foreach (var b in _baselineDisplays)
                {
                    bool enable = activeDevices.Contains(b.DeviceName);

                    DEVMODE mode = b.Mode;

                    if (enable)
                    {
                        if (mode.dmPelsWidth == 0 || mode.dmPelsHeight == 0)
                        {
                            mode.dmPelsWidth = defaultWidth;
                            mode.dmPelsHeight = defaultHeight;
                        }
                    }
                    else
                    {
                        mode.dmPelsWidth = 0;
                        mode.dmPelsHeight = 0;
                    }

                    mode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;

                    Debug.WriteLine(
                        $"[{layoutName}] Dev={b.DeviceName} enable={enable} pos=({mode.dmPositionX},{mode.dmPositionY}) " +
                        $"res={mode.dmPelsWidth}x{mode.dmPelsHeight} bpp={mode.dmBitsPerPel} freq={mode.dmDisplayFrequency}");

                    ChangeDisplaySettingsEx(
                        b.DeviceName,
                        ref mode,
                        IntPtr.Zero,
                        CDS_UPDATEREGISTRY | CDS_NORESET,
                        IntPtr.Zero);
                }

                ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

                System.Threading.Thread.Sleep(300);

                var runtime = EnumerateRuntimeDisplays();
                actualActive = runtime.Count(r => r.AttachedToDesktop);

                Debug.WriteLine($"[{layoutName}] Runtime state after attempt {attempt}:");
                foreach (var r in runtime)
                {
                    Debug.WriteLine(
                        $"[{layoutName}]   Dev={r.DeviceName} attached={r.AttachedToDesktop} " +
                        $"bounds={r.Bounds} res={r.Width}x{r.Height} bpp={r.Bits} freq={r.Frequency}");
                }

                if (actualActive == expectedActive)
                {
                    success = true;
                    break;
                }
            }

            string msg;
            if (success)
            {
                msg = $"{layoutName}: {actualActive} display(s) active as expected.";
            }
            else
            {
                msg = $"{layoutName}: expected {expectedActive} active display(s), OS reports {actualActive}.";
            }

            Debug.WriteLine($"[{layoutName}] Result: {msg}");

            return new LayoutResult
            {
                LayoutName = layoutName,
                Success = success,
                ExpectedActive = expectedActive,
                ActualActive = actualActive,
                Message = msg
            };
        }
    }
}