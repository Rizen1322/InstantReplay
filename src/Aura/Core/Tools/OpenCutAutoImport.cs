using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aura.Core.Logging;

namespace Aura.Core.Tools;

/// <summary>
/// Автоматическая загрузка клипа в OpenCut (opencut.app) без участия человека.
///
/// ПОЧЕМУ ЭТО ВООБЩЕ ВОЗМОЖНО. Песочница браузера не даёт настольному приложению
/// подсунуть файл в чужую вкладку, а deep-link для автозагрузки у OpenCut нет:
/// импорт там делается через скрытый <c>input[type=file]</c> панели медиа
/// (accept="image/*,video/*,audio/*"), drag&drop и Ctrl+V. Но Chromium-браузер
/// можно запустить с портом DevTools-протокола, и тогда файл кладётся в этот
/// input напрямую командой DOM.setFileInputFiles — для React это обычный
/// change-эвент, редактор импортирует файл так же, как при ручном выборе.
///
/// СХЕМА: запускаем Chrome/Edge с выделенным профилем и портом отладки →
/// открываем /projects → выставляем localStorage-флаг онбординга → создаём
/// проект кликом по кнопке → ждём /editor/{id} → кладём файл в input.
///
/// ХРУПКОСТЬ. Скрипт опирается на устройство чужого сайта (маршруты, тексты
/// кнопок, наличие input), и OpenCut его может сломать обновлением. Каждый шаг
/// ждёт свой таймаут, и при любой неудаче метод возвращает false — вызывающий
/// код открывает сайт обычным способом и просит перетащить файл мышью.
/// </summary>
public static class OpenCutAutoImport
{
    private const string SiteRoot = "https://opencut.app";
    private const string ProjectsUrl = SiteRoot + "/projects";

    private static readonly HttpClient Http = new();

    /// <summary>Общий таймаут всей процедуры: после него — откат к ручному сценарию.</summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Попытаться загрузить клип в OpenCut автоматически.
    /// true — файл попал в медиатеку нового проекта; false — не вышло, нужен откат.
    /// </summary>
    public static async Task<bool> ImportAsync(string videoPath)
    {
        try
        {
            string? browser = FindChromiumBrowser();
            if (browser is null)
            {
                Log.Warn("OpenCut", "Chrome/Edge не найден — автозагрузка недоступна");
                return false;
            }

            string profile = GetProfileDir();
            int port = await EnsureDevToolsAsync(browser, profile);
            if (port <= 0)
            {
                Log.Warn("OpenCut", "Порт DevTools не поднялся");
                return false;
            }

            using var budget = new CancellationTokenSource(TotalBudget);
            CancellationToken ct = budget.Token;

            await using DevToolsClient? cdp = await DevToolsClient.AttachAsync(port, ct);
            if (cdp is null)
            {
                Log.Warn("OpenCut", "DevTools: вкладка браузера не найдена");
                return false;
            }

            await cdp.SendAsync("Page.enable", null, ct);
            await cdp.SendAsync("DOM.enable", null, ct);

            // Всегда начинаем со страницы проектов: вкладка могла остаться от прошлого раза
            await cdp.SendAsync("Page.navigate", new { url = ProjectsUrl }, ct);
            if (!await WaitForAsync(cdp, "document.readyState === 'complete'", TimeSpan.FromSeconds(15), ct))
            {
                Log.Warn("OpenCut", "Страница проектов не загрузилась");
                return false;
            }

            // Онбординг-диалог редактора живёт во флаге localStorage — гасим его заранее,
            // чтобы он не закрыл собой медиапанель со скрытым input'ом
            await cdp.EvaluateValueAsync(
                "(() => { try { localStorage.setItem('hasSeenOnboarding', 'true'); } catch (e) {} return 1; })()", ct);

            // 1. Ждём кнопку создания проекта (тексты у обоих состояний страницы)
            const string projectButton =
                "[...document.querySelectorAll('button')].some(x => " +
                "/new project|create your first project/i.test(x.textContent || ''))";
            if (!await WaitForAsync(cdp, projectButton, TimeSpan.FromSeconds(20), ct))
            {
                Log.Warn("OpenCut", "Кнопка создания проекта не найдена");
                return false;
            }

            // Кликаем ровно один раз: повторный клик до завершения навигации создал бы второй проект
            const string clickProject =
                "(() => { const b = [...document.querySelectorAll('button')].find(x => " +
                "/new project|create your first project/i.test(x.textContent || '')); " +
                "if (b) { b.click(); return true; } return false; })()";
            if (!await cdp.EvaluateBoolAsync(clickProject, ct)) return false;

            // 2. Редактор открылся. У развернутой версии маршрут /editor?project={id},
            //    у версии из репозитория — /editor/{id}: проверяем и так, и так
            if (!await WaitForAsync(cdp, "/^\\/editor(\\/|\\?|$)/.test(location.pathname)",
                    TimeSpan.FromSeconds(20), ct))
            {
                Log.Warn("OpenCut", "Редактор не открылся после создания проекта");
                return false;
            }

            // 3. Ждём скрытый input панели медиа. Заодно гасим онбординг, если флаг не сработал:
            //    у его диалога кнопки Next/Finish — жмём их, пока диалог не уйдёт.
            const string editorReady =
                "(() => { const d = document.querySelector('[role=\"dialog\"]'); if (d) { " +
                "const b = [...d.querySelectorAll('button')].find(x => " +
                "/^(next|finish)$/i.test((x.textContent || '').trim())); " +
                "if (b) { b.click(); return false; } } " +
                "return !!document.querySelector('input[type=file]'); })()";
            if (!await WaitForAsync(cdp, editorReady, TimeSpan.FromSeconds(30), ct))
            {
                Log.Warn("OpenCut", "Скрытый input импорта в редакторе не найден");
                return false;
            }

            if (!await WaitForAsync(cdp, "document.readyState === 'complete'", TimeSpan.FromSeconds(15), ct))
            {
                Log.Warn("OpenCut", "Редактор не догрузился");
                return false;
            }

            // 4. Файл напрямую в input. Пока редактор инициализируется, React может
            //    перерисовать панель и сделать nodeId недействительным («Could not find
            //    node») — берём свежий nodeId через getDocument+querySelector и повторяем.
            //
            //    Проверять результат по input.files нельзя: OpenCut после чтения файлов
            //    сразу очищает input (event.target.value = ""). Поэтому перед записью
            //    вешаем на input свой слушатель change и по нему видим, что файл дошёл
            //    до обработчика редактора.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    JsonElement document = await cdp.SendAsync("DOM.getDocument", new { depth = 1 }, ct);
                    int rootId = document.GetProperty("root").GetProperty("nodeId").GetInt32();

                    JsonElement node = await cdp.SendAsync("DOM.querySelector",
                        new { nodeId = rootId, selector = "input[type=file]" }, ct);
                    int nodeId = node.GetProperty("nodeId").GetInt32();
                    if (nodeId == 0) continue; // input ещё не в DOM

                    // Ловушка на change: живёт до перерисовки input'а, ставим каждый раз
                    await cdp.EvaluateValueAsync(
                        "(() => { const i = document.querySelector('input[type=file]'); " +
                        "if (!i) return 0; window.__auraFiles = 0; " +
                        "i.addEventListener('change', e => { window.__auraFiles = (e.target.files || []).length; }); " +
                        "return 1; })()", ct);

                    await cdp.SendAsync("DOM.setFileInputFiles",
                        new { files = new[] { videoPath }, nodeId }, ct);

                    // change прилетает моментально; пару секунд даём на перерисовку
                    DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                    while (DateTime.UtcNow < deadline)
                    {
                        JsonElement? fired = await cdp.EvaluateValueAsync("window.__auraFiles ?? 0", ct);
                        if (fired is { ValueKind: JsonValueKind.Number } && fired.Value.GetInt32() > 0)
                        {
                            Log.Info("OpenCut", $"Клип загружен автоматически: {Path.GetFileName(videoPath)}");
                            return true;
                        }
                        await Task.Delay(300, ct);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    // Узел устарел или input не готов — попробуем ещё раз
                    Log.Warn("OpenCut", $"Попытка {attempt + 1}: {ex.Message}");
                }

                await Task.Delay(1000, ct);
            }

            Log.Warn("OpenCut", "Файл не принят input'ом за отведённые попытки");
            return false;
        }
        catch (OperationCanceledException)
        {
            Log.Warn("OpenCut", "Автозагрузка не уложилась в таймаут");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("OpenCut", $"Автозагрузка не удалась: {ex.Message}");
            return false;
        }
    }

    /// <summary>Ожидание, пока JS-выражение на странице не станет истинным.</summary>
    private static async Task<bool> WaitForAsync(DevToolsClient cdp, string js, TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // Навигация роняет контекст выполнения — это не провал, а «ещё не готово»
            try { if (await cdp.EvaluateBoolAsync(js, ct)) return true; }
            catch { }
            await Task.Delay(400, ct);
        }
        return false;
    }

    // ---------------- Браузер и порт DevTools ----------------

    /// <summary>Chrome предпочтительнее (OpenCut сам просит Chrome), Edge есть в каждой Windows.</summary>
    private static string? FindChromiumBrowser()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string?[] candidates =
        [
            Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Выделенный профиль: не трогаем браузер пользователя, localStorage и проекты живут отдельно.</summary>
    private static string GetProfileDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aura", "OpenCutProfile");

    /// <summary>
    /// Порт запоминаем рядом с профилем: если окно OpenCut от прошлого раза ещё
    /// открыто, второй запуск браузера просто откроет вкладку в нём — порт остаётся
    /// прежним, и к нему нужно подключаться, а не поднимать новый.
    /// </summary>
    private static async Task<int> EnsureDevToolsAsync(string browser, string profile)
    {
        Directory.CreateDirectory(profile);
        string portFile = Path.Combine(profile, "devtools.port");

        int port = File.Exists(portFile) && int.TryParse(File.ReadAllText(portFile), out int saved) ? saved : 0;
        if (port > 0 && await PingAsync(port)) return port;

        port = GetFreePort();
        try { await File.WriteAllTextAsync(portFile, port.ToString()); }
        catch { /* без файла автозагрузка просто не переживёт перезапуск браузера */ }

        var psi = new ProcessStartInfo(browser) { UseShellExecute = false };
        foreach (string arg in new[]
                 {
                     $"--remote-debugging-port={port}",
                     $"--user-data-dir={profile}",
                     "--no-first-run",
                     "--no-default-browser-check",
                     ProjectsUrl,
                 }) psi.ArgumentList.Add(arg);
        Process.Start(psi);

        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            if (await PingAsync(port)) return port;
        }
        return -1;
    }

    /// <summary>DevTools отвечает только на запросы с Host-заголовком localhost/IP — 127.0.0.1 подходит.</summary>
    private static async Task<bool> PingAsync(int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(600);
            using HttpResponseMessage _ = await Http.GetAsync($"http://127.0.0.1:{port}/json/version", cts.Token);
            return true;
        }
        catch { return false; }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    // ---------------- Клиент DevTools-протокола ----------------

    /// <summary>
    /// Минимальный клиент CDP поверх WebSocket. Запросы идут строго по одному
    /// (послали — читаем ответы, пока не придёт наш id), события протокола пропускаем.
    /// </summary>
    private sealed class DevToolsClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private int _nextId;

        public static async Task<DevToolsClient?> AttachAsync(int port, CancellationToken ct)
        {
            string? wsUrl = null;
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

            while (wsUrl is null && DateTime.UtcNow < deadline)
            {
                try
                {
                    using HttpResponseMessage response =
                        await Http.GetAsync($"http://127.0.0.1:{port}/json/list", ct);
                    using var doc = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync(ct));

                    string? fallback = null;
                    foreach (JsonElement target in doc.RootElement.EnumerateArray())
                    {
                        string type = target.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
                        string url = target.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
                        string? socket = target.TryGetProperty("webSocketDebuggerUrl", out JsonElement w)
                            ? w.GetString()
                            : null;
                        if (type != "page" || socket is null) continue;

                        if (url.StartsWith(SiteRoot, StringComparison.Ordinal)) { wsUrl = socket; break; }
                        fallback ??= socket;
                    }
                    wsUrl ??= fallback;
                }
                catch { /* браузер ещё поднимает DevTools-сервер */ }

                if (wsUrl is null) await Task.Delay(300, ct);
            }

            if (wsUrl is null) return null;

            var client = new DevToolsClient();
            await client._ws.ConnectAsync(new Uri(wsUrl), ct);
            return client;
        }

        public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref _nextId);
            string payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
            await _ws.SendAsync(new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(payload)),
                WebSocketMessageType.Text, true, ct);

            while (true)
            {
                using JsonDocument doc = JsonDocument.Parse(await ReceiveTextAsync(ct));
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("id", out JsonElement idEl) || idEl.GetInt32() != id)
                    continue; // событие протокола — не наш ответ

                if (root.TryGetProperty("error", out JsonElement error))
                {
                    string message = error.TryGetProperty("message", out JsonElement m)
                        ? m.GetString() ?? "ошибка CDP"
                        : "ошибка CDP";
                    throw new InvalidOperationException($"{method}: {message}");
                }

                return root.GetProperty("result").Clone();
            }
        }

        /// <summary>Значение выражения (returnByValue) или null при ошибке выполнения JS.</summary>
        public async Task<JsonElement?> EvaluateValueAsync(string expression, CancellationToken ct)
        {
            JsonElement result = await SendAsync("Runtime.evaluate",
                new { expression, returnByValue = true }, ct);
            if (result.TryGetProperty("exceptionDetails", out _)) return null;
            return result.TryGetProperty("result", out JsonElement remote) &&
                   remote.TryGetProperty("value", out JsonElement value)
                ? value.Clone()
                : null;
        }

        public async Task<bool> EvaluateBoolAsync(string expression, CancellationToken ct)
        {
            JsonElement? value = await EvaluateValueAsync(expression, ct);
            return value is { ValueKind: JsonValueKind.True or JsonValueKind.False } v && v.GetBoolean();
        }

        private async Task<string> ReceiveTextAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult chunk = await _ws.ReceiveAsync(
                    new ArraySegment<byte>(buffer), ct);
                message.Write(buffer, 0, chunk.Count);
                if (chunk.EndOfMessage) break;
            }
            return System.Text.Encoding.UTF8.GetString(message.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch { /* вкладку могли закрыть — соединение и так мертво */ }
            _ws.Dispose();
        }
    }
}
