using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VirtualDesktopShowcase {
    internal struct WINDOWINFO {
        public uint ownerpid;
        public uint childpid;
    }

    public class WindowTrackerEvent: EventArgs {
        public WindowTrackerEvent(IntPtr hwnd, string description = "") {
            this.hwnd = hwnd;
            this.description = description;
        }

        public IntPtr hwnd { get; }
        public string description { get; }
    }

    internal class WindowTracker {
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        delegate bool EnumWindowsProc(IntPtr hwnd, int lParam);

        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr
           hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess,
           uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc enumFunc, int lParam);

        [DllImport("Oleacc.dll")]
        static extern IntPtr GetProcessHandleFromHwnd(IntPtr hwnd);

        [DllImport("kernel32.dll")]
        static extern int GetProcessId(IntPtr handle);

        [DllImport("user32.dll")]
        static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hwnd);

        const uint OBJID_WINDOW = 0;
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

        public WindowTracker() {
            hhooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                FloatWinEventHandler, 0, 0, WINEVENT_OUTOFCONTEXT));

            hhooks.Add(SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
                MaxUnmaxWinEventHandler, 0, 0, WINEVENT_OUTOFCONTEXT));

            hhooks.Add(SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero,
                MinWinEventHandler, 0, 0, WINEVENT_OUTOFCONTEXT));

            hhooks.Add(SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY, IntPtr.Zero,
                CloseWinEventHandler, 0, 0, WINEVENT_OUTOFCONTEXT));
        }

        ~WindowTracker() {
            foreach(var hhook in hhooks) {
                UnhookWinEvent(hhook);
            }
        }

        public void SortCurrentWindows() {
            var shellWindow = GetShellWindow();
            EnumWindows(delegate (IntPtr hwnd, int lParam) {
                if(hwnd != shellWindow) {
                    if(IsZoomed(hwnd)) {
                        MaximizeEvent?.Invoke(this, new WindowTrackerEvent(hwnd, GetProcessDescriptionByHwnd(hwnd)));
                    } else if(!IsIconic(hwnd)) {
                        FloatWindowEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
                    }
                }

                return true;
            }, 0);
        }

        void FloatWinEventHandler(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime) {
            if(idObject == OBJID_WINDOW &&
               !IsZoomed(hwnd) &&
               IsWindowVisible(hwnd) &&
               GetWindowTextLength(hwnd) > 0) {
                FloatWindowEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
            }
        }

        void MaxUnmaxWinEventHandler(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime) {
            if(idObject == OBJID_WINDOW && IsWindowVisible(hwnd)) {
                if(!maxWindows.Contains(hwnd) &&
                   IsZoomed(hwnd) &&
                   GetWindowTextLength(hwnd) > 0) {
                    maxWindows.Add(hwnd);
                    MaximizeEvent?.Invoke(this, new WindowTrackerEvent(hwnd, GetProcessDescriptionByHwnd(hwnd)));
                } else if(idObject == OBJID_WINDOW &&
                          maxWindows.Contains(hwnd) &&
                          !IsZoomed(hwnd) &&
                          !IsIconic(hwnd)) {
                    maxWindows.Remove(hwnd);
                    UnmaximizeEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
                }
            }
        }

        void MinWinEventHandler(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime) {
            if(maxWindows.Contains(hwnd)) {
                maxWindows.Remove(hwnd);
                MinimizeEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
            }
        }

        void CloseWinEventHandler(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime) {
            if(idObject == OBJID_WINDOW &&
               maxWindows.Contains(hwnd) &&
               IsZoomed(hwnd)) {
                System.Threading.Thread.Sleep(100);
                if(!IsWindow(hwnd)) {
                    maxWindows.Remove(hwnd);
                    CloseEvent?.Invoke(this, new WindowTrackerEvent(hwnd));
                }
            }
        }

        static string GetProcessDescriptionByHwnd(IntPtr hwnd) {
            var procHandle = GetProcessHandleFromHwnd(hwnd);
            if(procHandle == IntPtr.Zero) {
                return hwnd.ToString();
            }

            var pid = GetProcessId(procHandle);
            if(pid <= 0) {
                return hwnd.ToString();
            }

            var process = Process.GetProcessById(pid);
            var mainModule = process.MainModule;
            if(mainModule == null) {
                return hwnd.ToString();
            }

            string? fileDescription = mainModule.FileVersionInfo.FileDescription;
            if("Application Frame Host".Equals(fileDescription)) {
                return process.MainWindowTitle;
            } else if(fileDescription == null) {
                return hwnd.ToString();
            }

            return fileDescription;
        }
    }
}
