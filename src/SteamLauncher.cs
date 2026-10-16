using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace SteamTranslate;

/// <summary>
/// Запуск и перезапуск Steam с отладочным портом. Файла-маркера .cef-enable-debugging в современном Steam нет,
/// порт открывается только флагом запуска -cef-enable-debugging (localhost:8080).
/// </summary>
static class SteamLauncher
{
    public const string DebugFlag = "-cef-enable-debugging";

    public static string ExePath()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            if (k == null) return null;
            var exe = k.GetValue("SteamExe") as string;
            if (string.IsNullOrEmpty(exe))
            {
                var dir = k.GetValue("SteamPath") as string;
                if (string.IsNullOrEmpty(dir)) return null;
                exe = Path.Combine(dir, "steam.exe");
            }
            exe = exe.Replace('/', '\\');
            return File.Exists(exe) ? exe : null;
        }
    }

    public static int RunningPid()
    {
        foreach (var p in Process.GetProcessesByName("steam"))
        {
            int id = p.Id;
            p.Dispose();
            return id;
        }
        return 0;
    }

    /// <summary>Закрывает Steam штатно (steam.exe -shutdown) и запускает заново с флагом отладки. Блокирует поток.</summary>
    public static bool Restart(out string error)
    {
        error = null;
        var exe = ExePath();
        if (exe == null) { error = "Не нашёл steam.exe (нет записи Steam в реестре)."; return false; }

        if (RunningPid() != 0)
        {
            Log.Write("закрываю Steam (steam.exe -shutdown)");
            Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
            var sw = Stopwatch.StartNew();
            while (RunningPid() != 0 && sw.Elapsed.TotalSeconds < 90) Thread.Sleep(500);
            if (RunningPid() != 0)
            {
                error = "Steam не закрылся за 90 секунд (идёт игра или загрузка?). Закройте его сами, и я запущу его заново.";
                Log.Write(error);
                return false;
            }
            Thread.Sleep(2000);
        }

        Log.Write("запускаю Steam с " + DebugFlag);
        Process.Start(new ProcessStartInfo(exe, DebugFlag) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        return true;
    }
}
