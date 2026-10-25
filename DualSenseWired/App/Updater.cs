// Update check against the GitHub releases of this repository; installing downloads the setup and
// runs it silently. The setup stops this app (--stop), replaces it and starts the new version.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace DualSenseWired.App;

public sealed class Updater
{
    const string Repo = "mrFunkill/DualSenseWired";
    static readonly HttpClient Http = CreateClient();

    readonly Action<string> _log;

    public Updater(Action<string> log) => _log = log;

    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    public sealed record Release(Version Version, string Tag, string PageUrl, string AssetUrl, long AssetSize);

    /// <summary>The newest release, if it is newer than this build.</summary>
    public Release? Available { get; private set; }
    public string State { get; private set; } = "idle";     // idle, downloading, installing, failed
    public string? Error { get; private set; }

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DualSenseWired", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    public async Task<Release?> CheckAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=10"));
            Release? best = null;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.GetProperty("draft").GetBoolean())
                    continue;
                string tag = r.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                    continue;
                var asset = r.GetProperty("assets").EnumerateArray()
                    .FirstOrDefault(a => a.GetProperty("name").GetString()?.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) == true);
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;
                if (best == null || version > best.Version)
                    best = new Release(version, tag, r.GetProperty("html_url").GetString() ?? "",
                                       asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64());
            }
            Available = best != null && best.Version > Current ? best : null;
            if (Available != null)
                _log($"update available: {Available.Tag} (this is {Current})");
            return Available;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _log($"update check failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Download the setup and start it; the setup closes this app.</summary>
    public async Task InstallAsync()
    {
        var release = Available ?? throw new InvalidOperationException("no update");
        try
        {
            State = "downloading";
            Error = null;
            string dir = Path.Combine(Path.GetTempPath(), "DualSenseWired-update");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"DualSenseWired-{release.Version}-setup.exe");
            using (var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(file);
                await response.Content.CopyToAsync(output);
            }
            if (new FileInfo(file).Length != release.AssetSize)
                throw new IOException("the download is incomplete");
            State = "installing";
            _log($"starting the setup of {release.Tag}");
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            State = "failed";
            Error = e.Message;
            _log($"update failed: {e.Message}");
        }
    }
}
