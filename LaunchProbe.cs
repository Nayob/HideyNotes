using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HideyNotes
{
    /// <summary>
    /// Records the launch environment next to the exe when HIDEYNOTES_LOG=1 is set. Needed because
    /// some locked-down environments deny access to %LOCALAPPDATA% / %TEMP% and we must see why.
    /// Never runs on a normal start: the install folder stays clean.
    /// </summary>
    internal static class LaunchProbe
    {
        public static void Run(string[] args)
        {
            try
            {
                var lines = new List<string>
                {
                    $"[{DateTime.Now:O}]",
                    $"user={Environment.UserName} session={Process.GetCurrentProcess().SessionId} elevated={IsElevated()} integrity={GetIntegrityLevel()}",
                    $"args={string.Join(" ", args)} cwd={Environment.CurrentDirectory}",
                    $"writable localappdata={CanWrite(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HideyNotes-probe"))}",
                    $"writable temp={CanWrite(Path.Combine(Path.GetTempPath(), "HideyNotes-probe"))}",
                    $"writable exedir={CanWrite(Path.Combine(AppContext.BaseDirectory, "HideyNotes-probe"))}",
                    string.Empty
                };

                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "launch.txt"), string.Join(Environment.NewLine, lines));
            }
            catch
            {
                // diagnostics must never break startup
            }
        }

        private static bool CanWrite(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "probe.txt");
                File.WriteAllText(file, "probe");
                File.Delete(file);
                Directory.Delete(directory, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetIntegrityLevel()
        {
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var token)) return "?";

                GetTokenInformation(token, 25, IntPtr.Zero, 0, out var length);
                var buffer = Marshal.AllocHGlobal(length);

                try
                {
                    if (!GetTokenInformation(token, 25, buffer, length, out length)) return "?";

                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = Marshal.ReadByte(sid, 1);
                    var last = Marshal.ReadInt32(sid, 8 + (count - 1) * 4);

                    return last switch
                    {
                        0x0000 => "Untrusted",
                        0x1000 => "Low",
                        0x2000 => "Medium",
                        0x3000 => "High",
                        0x4000 => "System",
                        _ => "0x" + last.ToString("X")
                    };
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                    CloseHandle(token);
                }
            }
            catch
            {
                return "?";
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static bool IsElevated()        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}
