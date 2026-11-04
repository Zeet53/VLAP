using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace VLAP;

public sealed class AppDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>app | folder</summary>
    public string Type { get; set; } = "";
    /// <summary>What actually gets launched: exe path, shortcut path, url or folder path.</summary>
    public string Resource { get; set; } = "";
    /// <summary>Category keys the app is tagged with.</summary>
    public string[] Categories { get; set; } = Array.Empty<string>();
    /// <summary>Whether the app's process is running right now.</summary>
    public bool Running { get; set; }
}

public sealed class PresetDto
{
    public string Name { get; set; } = "";
    /// <summary>App ids in the order the set launches them.</summary>
    public string[] Apps { get; set; } = Array.Empty<string>();
    public string[] AppNames { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Loopback-only JSON API for external tools. Reading and using the launcher, not configuring it:
///   GET    /apps              -> every app and folder with id, resource, categories, running
///   GET    /presets           -> every set with the ids it launches
///   GET    /state             -> is the HUD open, how many apps/presets, category counts
///   POST   /launch            -> {"id": "..."} starts one app
///   POST   /presets/run       -> {"name": "..."} starts every app of the set
///   POST   /presets           -> {"name": "...", "apps": ["id", ...]} creates or replaces a set
///   DELETE /presets/{name}    -> removes a set, the apps stay
///   POST   /scenarios         -> the original name of POST /presets, kept for older scripts
/// Everything here runs in the background: no call shows, hides or focuses the HUD.
/// </summary>
public sealed class HttpApi : IDisposable
{
    public const int Port = 49521;
    private const string Prefix = "http://127.0.0.1:49521/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly MainWindow _window;
    private readonly HttpListener _listener = new();
    private bool _started;

    public HttpApi(MainWindow window)
    {
        _window = window;
    }

    public void Start()
    {
        try
        {
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _started = true;
            SettingsStore.Log("http api: listening on " + Prefix);
            _ = Task.Run(LoopAsync);
        }
        catch (Exception ex)
        {
            SettingsStore.Log("http api: cannot listen on " + Prefix + ": " + ex.Message);
        }
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";
            var method = request.HttpMethod;

            if (method == "GET" && path == "/apps")
            {
                Write(context, 200, _window.ApiApps());
                return;
            }

            if (method == "GET" && path == "/presets")
            {
                Write(context, 200, _window.ApiPresets());
                return;
            }

            if (method == "GET" && path == "/state")
            {
                Write(context, 200, _window.ApiState());
                return;
            }

            if (method == "POST" && path == "/launch")
            {
                var (status, body) = WithBody(context, root => _window.ApiLaunch(Text(root, "id")));
                Write(context, status, body);
                return;
            }

            if (method == "POST" && path == "/presets/run")
            {
                var (status, body) = WithBody(context, root => _window.ApiRunPreset(Text(root, "name")));
                Write(context, status, body);
                return;
            }

            // /scenarios is the original name of the same call; older scripts keep working
            if (method == "POST" && (path == "/presets" || path == "/scenarios"))
            {
                var (status, body) = WithBody(context, CreateScenario);
                Write(context, status, body);
                return;
            }

            if (method == "POST" && path == "/overlay")
            {
                // the HUD is the user's: an agent may use the launcher, not pull it onto the screen
                Write(context, 404, new { error = "not found" });
                return;
            }

            if (method == "DELETE" && path.StartsWith("/presets/", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path["/presets/".Length..]);
                var (status, body) = _window.ApiDeletePreset(name);
                Write(context, status, body);
                return;
            }

            Write(context, 404, new { error = "not found" });
        }
        catch (Exception ex)
        {
            SettingsStore.Log("http api: " + ex.Message);
            try { Write(context, 500, new { error = "internal error" }); } catch { /* client is gone */ }
        }
    }

    private (int Status, object Body) CreateScenario(JsonElement root)
    {
        string? name = null;
        string[]? ids = null;

        if (root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            name = nameElement.GetString();

        if (root.TryGetProperty("apps", out var appsElement) && appsElement.ValueKind == JsonValueKind.Array)
            ids = appsElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .ToArray();

        return _window.ApiCreateScenario(name, ids);
    }

    /// <summary>Reads a JSON object body, hands it over and turns broken or non-object JSON into a 400.</summary>
    private static (int Status, object Body) WithBody(
        HttpListenerContext context, Func<JsonElement, (int Status, object Body)> handle)
    {
        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            body = reader.ReadToEnd();

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (400, new { error = "invalid json" });
            return handle(doc.RootElement);
        }
        catch (JsonException)
        {
            return (400, new { error = "invalid json" });
        }
    }

    /// <summary>A string field of the body; empty when it is missing or not a string.</summary>
    private static string Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    private static void Write(HttpListenerContext context, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        try { if (_started) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _started = false;
    }
}
