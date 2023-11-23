using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

using WindowsDesktop;

namespace VirtualDesktopShowcase
{
    internal struct WINDOWINFO
    {
        public uint ownerpid;
        public uint childpid;
    }

    public class WindowTrackerEvent : EventArgs
    {
        public WindowTrackerEvent(IntPtr hwnd)
        {
            Hwnd = hwnd;
        }

        public IntPtr Hwnd { get; }
    }

    internal partial class WindowTracker
    {
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        delegate bool EnumWindowsProc(IntPtr hwnd, int lParam);

        [LibraryImport("user32.dll")]
        private static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr
           hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess,
           uint idThread, uint dwFlags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnhookWinEvent(IntPtr hWinEventHook);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumWindows(EnumWindowsProc enumFunc, int lParam);

        [LibraryImport("Oleacc.dll")]
        private static partial IntPtr GetProcessHandleFromHwnd(IntPtr hwnd);

        [LibraryImport("kernel32.dll")]
        private static partial int GetProcessId(IntPtr handle);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetShellWindow();

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial int GetWindowTextLengthW(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindow(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsZoomed(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(IntPtr hwnd);

        const uint OBJID_WINDOW = 0;
        const uint CHILDID_SELF = 0;
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        const uint EVENT_OBJECT_DESTROY = 0x8001;
        const uint WINEVENT_OUTOFCONTEXT = 0;

        readonly HashSet<IntPtr> maxWindows = new();
        readonly List<IntPtr> hhooks = new();

        public delegate void EventHandler(object sender, WindowTrackerEvent e);
        public event EventHandler<WindowTrackerEvent>? FloatWindowEvent;
        public event EventHandler<WindowTrackerEvent>? MaximizeEvent;
        public event EventHandler<WindowTrackerEvent>? UnmaximizeEvent;
        public event EventHandler<WindowTrackerEvent>? MinimizeEvent;
        public event EventHandler<WindowTrackerEvent>? CloseEvent;

        public WindowTracker()
        {
            hhooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                FloatWinEventHandler, 0, 0, WINEVENT_OUTOFCONTEXT));

            hhooks.Add(SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
                MaxUnmaxWinEventHandler, 0, 0, WINEVENT_OUTOFCONTEXT));

            hhooks.Add(SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero,
                MinWinEventHandler, 0, 0, WINEVENT_OUTOFCONTEXT));

            hhooks.Add(SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY, IntPtr.Zero,
                CloseWinEventHandler, 0, 0, WINEVENT_OUTOFCONTEXT));
        }

        ~WindowTracker()
        {
            foreach (var hhook in hhooks)
            {
                UnhookWinEvent(hhook);
            }
        }

        public void SortCurrentWindows()
        {
            var shellWindow = GetShellWindow();
            EnumWindows(delegate (IntPtr hwnd, int lParam)
            {
                if (hwnd != shellWindow)
                {
                    if (IsZoomed(hwnd))
                    {
                        maxWindows.Add(hwnd);
                        MaximizeEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
                    }
                    else if (!IsIconic(hwnd))
                    {
                        FloatWindowEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
                    }
                }

                return true;
            }, 0);
        }

        void FloatWinEventHandler(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject == OBJID_WINDOW &&
                idChild == CHILDID_SELF &&
               !IsZoomed(hwnd) &&
               IsWindowVisible(hwnd) &&
               GetWindowTextLengthW(hwnd) > 0)
            {
                FloatWindowEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
            }
        }

        void MaxUnmaxWinEventHandler(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject == OBJID_WINDOW && IsWindowVisible(hwnd))
            {
                if (!maxWindows.Contains(hwnd) &&
                   IsZoomed(hwnd) &&
                   GetWindowTextLengthW(hwnd) > 0)
                {
                    maxWindows.Add(hwnd);
                    MaximizeEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
                }
                else if (idObject == OBJID_WINDOW &&
                            idChild == CHILDID_SELF &&
                          maxWindows.Contains(hwnd) &&
                          !IsZoomed(hwnd) &&
                          !IsIconic(hwnd))
                {
                    maxWindows.Remove(hwnd);
                    UnmaximizeEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
                }
            }
        }

        void MinWinEventHandler(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (maxWindows.Contains(hwnd))
            {
                maxWindows.Remove(hwnd);
                MinimizeEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
            }
        }

        void CloseWinEventHandler(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject == OBJID_WINDOW &&
                idChild == CHILDID_SELF &&
               maxWindows.Contains(hwnd))
            {
                System.Threading.Thread.Sleep(100);
                if (!IsWindow(hwnd))
                {
                    maxWindows.Remove(hwnd);
                    CloseEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
                }
            }
        }

        public const string UnNamableWindowName = "Administrator Window";

        public static string GetProcessDescriptionByHwnd(IntPtr hwnd)
        {
            var procHandle = GetProcessHandleFromHwnd(hwnd);
            if (procHandle == IntPtr.Zero)
            {
                return UnNamableWindowName;
            }

            var pid = GetProcessId(procHandle);
            if (pid <= 0)
            {
                return UnNamableWindowName;
            }

            var process = Process.GetProcessById(pid);
            if (process.MainWindowTitle.Length > 0 && process.MainWindowTitle.Length <= 30)
            {
                return process.MainWindowTitle;
            }

            var mainModule = process.MainModule;
            if (mainModule == null)
            {
                return UnNamableWindowName;
            }

            string? fileDescription = mainModule.FileVersionInfo.FileDescription;
            if ("Application Frame Host".Equals(fileDescription) || fileDescription == null || fileDescription.Length == 0)
            {
                return new DirectoryInfo(process.ProcessName).Name;
            }

            return fileDescription;
        }
    }
}
