using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamTranslate;

static class Log
{
    static readonly object Gate = new object();
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamTranslate");
    public static readonly string FilePath = Path.Combine(Dir, "log.txt");

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* лог не должен ронять программу */ }
    }
}

static class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamTranslate", "settings.json");

    public static string Lang = "ru";

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath, Encoding.UTF8));
            if (d.ContainsKey("lang") && d["lang"] is string s && s.Length > 0) Lang = s;
        }
        catch { /* битый файл: остаёмся на значениях по умолчанию */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(new { lang = Lang }), Encoding.UTF8);
        }
        catch (Exception e) { Log.Write("не удалось сохранить настройки: " + e.Message); }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, @"Local\SteamTranslate.Tray", out created))
        {
            if (!created) return; // уже запущена, иконка висит в трее
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("необработанная ошибка: " + e.ExceptionObject);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Settings.Load();
            Application.Run(new TrayContext());
        }
    }
}

sealed class TrayContext : ApplicationContext
{
    static readonly string[][] Languages =
    {
        new[] { "ru", "Русский" }, new[] { "uk", "Українська" }, new[] { "en", "English" },
        new[] { "de", "Deutsch" }, new[] { "es", "Español" }, new[] { "fr", "Français" },
        new[] { "it", "Italiano" }, new[] { "pl", "Polski" }, new[] { "pt", "Português" },
        new[] { "tr", "Türkçe" }, new[] { "zh-CN", "中文" }, new[] { "ja", "日本語" }, new[] { "ko", "한국어" },
    };

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "SteamTranslate";

    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _status = new ToolStripMenuItem("Запускаюсь…") { Enabled = false };
    readonly ToolStripMenuItem _autostart = new ToolStripMenuItem("Запускать вместе с Windows");
    readonly ToolStripMenuItem _restart = new ToolStripMenuItem("Перезапустить Steam с кнопкой перевода…");
    readonly List<ToolStripMenuItem> _langItems = new List<ToolStripMenuItem>();
    readonly Injector _injector = new Injector();
    readonly CancellationTokenSource _cts = new CancellationTokenSource();
    readonly SynchronizationContext _ui;
    bool _notified;
    bool _restartBalloon; // последнее уведомление было про «Steam без порта»: по клику предлагаем перезапуск
    int _restarting;

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        _ui = SynchronizationContext.Current;

        var langMenu = new ToolStripMenuItem("Язык перевода");
        foreach (var l in Languages)
        {
            var code = l[0];
            var item = new ToolStripMenuItem(l[1]) { Tag = code, Checked = code == Settings.Lang };
            item.Click += (s, e) => SetLang(code);
            _langItems.Add(item);
            langMenu.DropDownItems.Add(item);
        }

        var showLog = new ToolStripMenuItem("Показать лог");
        showLog.Click += (s, e) =>
        {
            try { Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); } catch { }
        };
        var exit = new ToolStripMenuItem("Выход");
        exit.Click += (s, e) => Quit();

        _autostart.Click += (s, e) => ToggleAutostart();
        _restart.Click += (s, e) => ConfirmAndRestart();

        menu.Items.AddRange(new ToolStripItem[]
        {
            _status, new ToolStripSeparator(), langMenu, _restart, _autostart,
            new ToolStripSeparator(), showLog, exit,
        });
        menu.Opening += (s, e) => RefreshChecks();

        _tray = new NotifyIcon
        {
            Icon = AppIcon(),
            Text = "Steam Translate",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (s, e) => { _restartBalloon = false; _tray.ShowBalloonTip(3000, "Steam Translate", _status.Text, ToolTipIcon.Info); };
        _tray.BalloonTipClicked += (s, e) => { if (_restartBalloon) { _restartBalloon = false; ConfirmAndRestart(); } };

        _injector.Lang = Settings.Lang;
        _injector.OnStatus = OnStatus;
        _injector.OnSteamWithoutPort = () => Post(() =>
        {
            _restartBalloon = true;
            _tray.ShowBalloonTip(10000, "Steam без кнопки перевода",
                "Steam запущен без порта отладки. Нажмите сюда, чтобы перезапустить его с кнопкой перевода.", ToolTipIcon.Info);
        });
        RefreshChecks();
        Log.Write("запуск, язык " + Settings.Lang);
        System.Threading.Tasks.Task.Run(() => _injector.RunAsync(_cts.Token));
    }

    static Icon AppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    void Post(Action act)
    {
        if (_ui != null) _ui.Post(_ => act(), null); else act();
    }

    void OnStatus(string text, bool connected)
    {
        Post(() =>
        {
            _status.Text = text;
            var tip = "Steam Translate: " + text;
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // у NotifyIcon лимит 63 символа
            if (connected && !_notified)
            {
                _notified = true;
                _restartBalloon = false;
                _tray.ShowBalloonTip(4000, "Steam Translate", "Подключился к Steam. Кнопка «Перевести» появится на страницах магазина и сообщества.", ToolTipIcon.Info);
            }
        });
    }

    void SetLang(string code)
    {
        Settings.Lang = code;
        Settings.Save();
        RefreshChecks();
        System.Threading.Tasks.Task.Run(() => _injector.SetLangAsync(code));
    }

    void RefreshChecks()
    {
        foreach (var i in _langItems) i.Checked = (string)i.Tag == Settings.Lang;
        _autostart.Checked = IsAutostart();
        _restart.Enabled = SteamLauncher.ExePath() != null;
    }

    // ---------- автозапуск ----------
    static bool IsAutostart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunName) != null;
    }

    void ToggleAutostart()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (IsAutostart()) k.DeleteValue(RunName, false);
            else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
        }
        RefreshChecks();
    }

    // ---------- перезапуск Steam с портом отладки (без SteamBooster) ----------
    void ConfirmAndRestart()
    {
        if (Interlocked.CompareExchange(ref _restarting, 1, 0) != 0) return;
        bool running = SteamLauncher.RunningPid() != 0;
        var text = running
            ? "Steam будет закрыт и запущен заново с кнопкой перевода.\nИдущие игры закроются, загрузки продолжатся после запуска.\n\nПродолжить?"
            : "Запустить Steam с кнопкой перевода?";
        if (MessageBox.Show(text, "Steam Translate", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            _restarting = 0;
            return;
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            string error;
            bool ok;
            try { ok = SteamLauncher.Restart(out error); }
            catch (Exception e) { ok = false; error = e.Message; Log.Write("перезапуск Steam: " + e); }
            _restarting = 0;
            if (!ok) Post(() => MessageBox.Show(error, "Steam Translate", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        });
    }

    void Quit()
    {
        _cts.Cancel();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
