// Based on ShellManager of ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0.
// Changed for HeMiDock: only the notification area, the windows of apps, hiding the taskbar of Windows and the AppBar
// messages; no full-screen helper and no AppBar windows (the app has its own). The thread it is made on gets the work of
// the shell (ShellDispatcher).

using System;
using System.Threading;
using ManagedShell.AppBar;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;

namespace ManagedShell
{
    public sealed partial class ShellManager : IDisposable
    {
        /// <param name="pinnedNotifyIcons">Icons of the notification area shown on the taskbar (GUIDs or paths).</param>
        /// <param name="taskIconSize">The size of the icons of windows.</param>
        public ShellManager(string[] pinnedNotifyIcons, IconSize taskIconSize)
        {
            ShellDispatcher.Use(SynchronizationContext.Current);

            TrayService = new TrayService();
            ExplorerTrayService = new ExplorerTrayService();
            NotificationArea = new NotificationArea(pinnedNotifyIcons, TrayService, ExplorerTrayService);

            TasksService = new TasksService(taskIconSize);
            Tasks = new Tasks(TasksService);

            ExplorerHelper = new ExplorerHelper(NotificationArea);
            AppBarManager = new AppBarManager(ExplorerHelper);

            NotificationArea.Initialize();
        }

        public NotificationArea NotificationArea { get; }
        public TrayService TrayService { get; }
        public ExplorerTrayService ExplorerTrayService { get; }
        public TasksService TasksService { get; }
        public Tasks Tasks { get; }
        public AppBarManager AppBarManager { get; }
        public ExplorerHelper ExplorerHelper { get; }

        public void Dispose()
        {
            IconHelper.DisposeIml();
            AppBarManager.Dispose();
            NotificationArea.Dispose();
            Tasks.Dispose();
        }
    }
}
