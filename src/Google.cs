using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SteamTranslate;

/// <summary>
/// Перевод через тот же сервис, что встроен в Chrome (translate-pa.googleapis.com/v1/translateHtml).
/// Принимает HTML-фрагменты с метками &lt;a i=N&gt; и возвращает их переведёнными: так Google видит
/// предложение целиком. Публичный ключ сервиса берётся из скрипта самого Google Translate.
/// </summary>
static class Google
{
    const string ScriptUrl = "https://translate.googleapis.com/_/translate_http/_/js/k=translate_http.tr.en_US.YusFYy3P_ro.O/am=AAg/d=1/exm=el_conf/ed=1/rs=AN8SPfq1Hb8iJRleQqQc8zhdzXmF9E56eQ/m=el_main";
    const string ApiUrl = "https://translate-pa.googleapis.com/v1/translateHtml";
    static readonly Regex KeyRe = new Regex(@"['""]x-goog-api-key['""]\s*:\s*['""](\w{39})['""]", RegexOptions.IgnoreCase);

    static readonly HttpClient Http = CreateClient();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly SemaphoreSlim KeyLock = new SemaphoreSlim(1, 1);
    static string _key;
    static DateTime _keyTime;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return c;
    }

    static async Task<string> GetKeyAsync(bool force)
    {
        await KeyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!force && _key != null && DateTime.UtcNow - _keyTime < TimeSpan.FromHours(6)) return _key;
            var js = await Http.GetStringAsync(ScriptUrl).ConfigureAwait(false);
            var m = KeyRe.Match(js);
            if (!m.Success) throw new Exception("не нашёл ключ сервиса перевода в скрипте Google");
            _key = m.Groups[1].Value;
            _keyTime = DateTime.UtcNow;
            Log.Write("получен ключ сервиса перевода");
            return _key;
        }
        finally { KeyLock.Release(); }
    }

    /// <summary>Переводит HTML-фрагменты; ответ — те же фрагменты на языке tl (разбирает их страница).</summary>
    public static async Task<List<string>> TranslateHtmlAsync(IList<string> pieces, string tl)
    {
        var body = Json.Serialize(new object[] { new object[] { new List<string>(pieces), "auto", tl }, "te" });
        bool keyRefreshed = false;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var key = await GetKeyAsync(false).ConfigureAwait(false);
            var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json+protobuf"),
            };
            req.Headers.TryAddWithoutValidation("X-goog-api-key", key);

            using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
            {
                if (resp.IsSuccessStatusCode)
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var arr = (IList)Json.DeserializeObject(text);
                    var res = new List<string>();
                    foreach (var item in (IList)arr[0]) res.Add((string)item);
                    if (res.Count != pieces.Count)
                        throw new Exception("Google вернул " + res.Count + " фрагментов вместо " + pieces.Count);
                    return res;
                }
                var code = (int)resp.StatusCode;
                if ((code == 400 || code == 401 || code == 403) && !keyRefreshed)
                {
                    keyRefreshed = true; // ключ мог устареть
                    await GetKeyAsync(true).ConfigureAwait(false);
                    continue;
                }
                if (code != 429 && code < 500) throw new Exception("Google HTTP " + code);
            }
            await Task.Delay(600 * (1 << attempt)).ConfigureAwait(false);
        }
        throw new Exception("Google: слишком много повторов (429?)");
    }
}
