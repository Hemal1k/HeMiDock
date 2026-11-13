// Written for HeMiDock in place of the logging of ManagedShell (its observers, file log and event arguments): the code
// taken from ManagedShell calls ShellLogger as before, and everything goes to the debug log of the app (debug mode only,
// like the rest of the app; errors with an exception also to log.txt). Nothing was attached to the old logger, so its
// messages were lost.

using System;

namespace ManagedShell.Common.Logging
{
    public static class ShellLogger
    {
        public static void Debug(string message) => HeMiDock.Diag.Write("ManagedShell", message);
        public static void Debug(string message, Exception exception) => HeMiDock.Diag.Write("ManagedShell", message + " " + exception?.Message);
        public static void DebugIf(bool condition, string message) { if (condition) Debug(message); }
        public static void DebugIf(bool condition, string message, Exception exception) { if (condition) Debug(message, exception); }
        public static void Info(string message) => Debug(message);
        public static void Info(string message, Exception exception) => Debug(message, exception);
        public static void Warning(string message) => Debug("warning: " + message);
        public static void Warning(string message, Exception exception) => Debug("warning: " + message, exception);
        public static void Error(string message) => Debug("error: " + message);
        public static void Error(string message, Exception exception) => HeMiDock.Log.Write("ManagedShell: " + message, exception);
        public static void Fatal(string message) => Error(message);
        public static void Fatal(string message, Exception exception) => Error(message, exception);
    }
}
