using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Win32;
using HanumanInstitute.LibMpv;
using VideoPlayer;
using VideoPlayer.Core;

internal static partial class Program
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MediaSynchronizationTimeout = TimeSpan.FromMinutes(10);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("-i")) return RunSlowSynchronizer(args);
        if (args.Length is not (2 or 4) || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tools/SubtitleAutomationCheck -- tests/VideoPlayer.Tests/TestData artifacts/subtitle-ui-check [video subtitle]");
            return 2;
        }
        var fixtures = Path.GetFullPath(args[0]);
        var output = Directory.CreateDirectory(Path.GetFullPath(args[1])).FullName;
        var profile = Path.Combine(output, $"profile-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("PANTHEON_DATA_DIRECTORY", profile);
        var preferences = new PlayerPreferences
        {
            PreferredAudioTrackIndex = 2,
            AutoHideBottom = false,
            AutoHideHeader = false,
            SubtitleSearchLanguage = "tr",
            OpenSubtitlesApiKey = "fixture-key",
            Muted = true
        };
        new PlayerPreferencesStore(Path.Combine(profile, "settings.json")).Save(preferences);
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software] })
            .AfterSetup(builder =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)builder.Instance!.ApplicationLifetime!;
                var repository = new FixtureRepository(File.ReadAllText(Path.Combine(fixtures, "speech-reference.srt")));
                lifetime.Startup += async (_, _) =>
                {
                    try
                    {
                        var initialWindow = lifetime.MainWindow;
                        var window = new MainWindow(Array.Empty<string>(), new HttpClient(repository));
                        lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        lifetime.MainWindow = window;
                        window.Show();
                        initialWindow?.Close();
                        await Task.Yield();
                        VerifyPopupPointerRouting(window);
                        await VerifyAsync(window, fixtures, output, profile, preferences, repository);
                        if (args.Length == 4)
                            await VerifyExternalSubtitleAsync(window, args[2], args[3], profile, preferences);
                        Console.WriteLine("PASS: online repository search/download, local and embedded subtitle precedence, real speech sync, playback, cache, manual adjustment, media/audio switching and cancellation (controlled HTTP repository).");
                        lifetime.Shutdown();
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine(exception);
                        lifetime.Shutdown(1);
                    }
                };
            }).StartWithClassicDesktopLifetime(args);
    }

    private static void VerifyPopupPointerRouting(MainWindow window)
    {
        var popup = window.FindControl<Avalonia.Controls.Primitives.Popup>("SubtitlePickerPopup")!;
        var button = window.FindControl<Button>("SynchronizeSubtitleButton")!;
        popup.IsOpen = true;
        window.UpdateLayout();
        var popupRoot = TopLevel.GetTopLevel(button)
            ?? throw new InvalidOperationException("The subtitle menu must be attached to a visual root.");
        Require(!ReferenceEquals(popupRoot, window), "The subtitle menu must use a separate visual root.");
        var buttonPosition = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), popupRoot)
            ?? throw new InvalidOperationException("The subtitle button must have coordinates in the popup.");
        var pointer = new Avalonia.Input.Pointer(1, PointerType.Mouse, true);
        var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var pressed = new PointerPressedEventArgs(button, pointer, popupRoot, buttonPosition, 0, properties, KeyModifiers.None);
        var reachedMenu = false;
        EventHandler<PointerPressedEventArgs> verifyPress = (_, e) =>
        {
            Require(!e.Handled, "The window's resize and queue handlers must not consume subtitle menu presses.");
            reachedMenu = true;
        };
        button.AddHandler(InputElement.PointerPressedEvent, verifyPress, RoutingStrategies.Tunnel);
        button.RaiseEvent(pressed);
        button.RemoveHandler(InputElement.PointerPressedEvent, verifyPress);
        Require(reachedMenu, "The press must reach the subtitle menu control.");
        Require(popup.IsOpen, "Pressing a subtitle menu control must not close or detach the popup.");
        button.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, button, pointer, popupRoot,
            buttonPosition, 1, properties, KeyModifiers.None));
        button.RaiseEvent(new PointerWheelEventArgs(button, pointer, popupRoot, buttonPosition, 2,
            properties, KeyModifiers.None, new Vector(0, -1)));
        Require(popup.IsOpen, "Moving or scrolling inside the subtitle menu must keep it attached.");
        button.RaiseEvent(new PointerReleasedEventArgs(button, pointer, popupRoot, buttonPosition, 3,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        var root = window.FindControl<Grid>("RootPanel")!;
        root.RaiseEvent(new PointerPressedEventArgs(root, pointer, window,
            new Point(window.Bounds.Width / 2, window.Bounds.Height / 2), 4, properties, KeyModifiers.None));
        Require(!popup.IsOpen, "Pressing the main window must still dismiss the subtitle menu.");
        Console.WriteLine("PASS: subtitle popup presses, movement and scrolling preserve the popup visual tree.");
    }

    private static async Task VerifyExternalSubtitleAsync(MainWindow window, string video, string subtitle,
        string profile, PlayerPreferences preferences)
    {
        Invoke(window, "SavePreferences", preferences with { AutoFindSubtitles = false, AutoSynchronizeSubtitles = false });
        var player = window.FindControl<ToneMappedVideoView>("VideoSurface")!.MpvContext;
        var originalHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(subtitle));
        Open(window, Path.GetFullPath(video));
        await WaitUntilAsync(() => string.Equals(player.GetProperty<string>("path"), Path.GetFullPath(video), StringComparison.OrdinalIgnoreCase)
            && player.GetProperty<double>("duration") > 0
            && window.FindControl<Button>("SynchronizeSubtitleButton")!.IsEnabled
            && (bool)typeof(MainWindow).GetField("_hasAppliedTrackPreferences", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
        Invoke(window, "AddSubtitle", Path.GetFullPath(subtitle), true, false);
        player.SetProperty("pause", false);
        VerifyPopupPointerRouting(window);
        window.FindControl<Button>("SynchronizeSubtitleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => typeof(MainWindow).GetField("_subtitleAutomationCancellation",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null, MediaSynchronizationTimeout);
        var result = SelectedExternalSubtitle(player);
        Require(result.EndsWith(".synced.srt", StringComparison.Ordinal) && SubtitleFileCache.IsValidSrt(result),
            $"The supplied subtitle must synchronize successfully: {window.FindControl<TextBlock>("SubtitleAutomationStatus")!.Text}");
        Require(new SubtitleFileCache(Path.Combine(profile, "Subtitles")).FindSynchronizationSource(result) == Path.GetFullPath(subtitle),
            "The synchronized output must refer to the supplied original subtitle.");
        var currentHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(subtitle));
        Require(originalHash.SequenceEqual(currentHash),
            "Synchronization must preserve the supplied subtitle file.");
        Require(!player.GetProperty<bool>("pause"), "The supplied video must keep playing during synchronization.");
        Console.WriteLine($"PASS: supplied video/subtitle synchronized with uninterrupted playback; output: {result}");
    }

    private static async Task VerifyAsync(MainWindow window, string fixtures, string output, string profile, PlayerPreferences preferences, FixtureRepository repository)
    {
        var mediaDirectory = Directory.CreateDirectory(Path.Combine(output, $"media-{Guid.NewGuid():N}")).FullName;
        var first = Path.Combine(mediaDirectory, "Example.Show.S01E01.mkv");
        var second = Path.Combine(mediaDirectory, "Example.Show.S01E02.mkv");
        File.Copy(Path.Combine(fixtures, "speech.mkv"), first);
        File.Copy(Path.Combine(fixtures, "speech.mkv"), second);
        var localSubtitle = Path.ChangeExtension(first, ".srt");
        var localContent = "1\n00:00:00,000 --> 00:00:55,000\nLOCAL_ONLY\n";
        await File.WriteAllTextAsync(localSubtitle, localContent);
        await File.WriteAllTextAsync(Path.ChangeExtension(second, ".srt"), localContent);
        var player = window.FindControl<ToneMappedVideoView>("VideoSurface")!.MpvContext;
        var status = window.FindControl<TextBlock>("SubtitleAutomationStatus")!;
        Open(window, first);
        await WaitUntilAsync(() => SelectedExternalSubtitle(player).EndsWith(".synced.srt", StringComparison.Ordinal));
        var synchronizedPath = SelectedExternalSubtitle(player);
        var cache = new SubtitleFileCache(Path.Combine(profile, "Subtitles"));
        var source = cache.DownloadPath(first, "tr");
        Require(synchronizedPath.EndsWith(".synced.srt", StringComparison.Ordinal), "Automatic sync should select its cached output.");
        Require(repository.DownloadCount == 1, "An existing local subtitle must not suppress the repository download.");
        Require(await File.ReadAllTextAsync(localSubtitle) == localContent, "The local subtitle must be preserved.");
        Require(await File.ReadAllTextAsync(source) == repository.FirstEpisodeSubtitle, "The downloaded original must be preserved.");
        Require(!player.GetProperty<bool>("pause"), "Automatic synchronization must not pause playback.");
        player.SetProperty("pause", true);
        player.Seek(3.0, SeekOption.Absolute | SeekOption.Exact).Invoke();
        await WaitUntilAsync(() => player.GetProperty<string>("sub-text")?.Contains("train", StringComparison.Ordinal) == true);
        Console.WriteLine("PASS: repository subtitle is downloaded and aligned even when a local subtitle is already loaded.");

        Invoke(window, "AdjustSubtitleDelay", 1.0);
        var manualDelay = player.GetProperty<double>("sub-delay");
        Require(manualDelay > 0, "Manual subtitle adjustment should work.");
        Open(window, first);
        await WaitUntilAsync(() => status.Text == "Preserving saved manual subtitle timing.");
        Require(Math.Abs(player.GetProperty<double>("sub-delay") - manualDelay) < 0.001, "Reopening must preserve manual timing.");
        Require(repository.DownloadCount == 1, "Reopening must reuse the downloaded subtitle without spending another download.");
        Console.WriteLine("PASS: reopening preserves manually adjusted synchronization.");

        Invoke(window, "ResetSubtitleDelay");
        await (Task)Invoke(window, "RunSubtitleAutomationAsync", false, true, true)!;
        Require(SelectedExternalSubtitle(player) == synchronizedPath, "Cached synchronization must be reused.");

        var slowPreferences = preferences with { SubtitleSynchronizerPath = Environment.ProcessPath! };
        Invoke(window, "SavePreferences", slowPreferences);
        // Invalidate the content-addressed cache, so a deliberately slow worker starts.
        await File.AppendAllTextAsync(source, "\n13\n00:00:56,000 --> 00:00:57,000\nAdditional cue\n");
        Invoke(window, "AddSubtitle", source, true, false);
        await WaitUntilAsync(() => WorkerStarted(profile));
        Invoke(window, "AdjustSubtitleDelay", 1.0);
        var selectedBeforeCancel = SelectedExternalSubtitle(player);
        await WaitUntilAsync(() => !WorkerDirectoryExists(profile));
        Require(SelectedExternalSubtitle(player) == selectedBeforeCancel, "A canceled result must not replace the manual subtitle.");
        Require(player.GetProperty<double>("sub-delay") > 0, "The canceled worker must not reset a manual delay.");
        Console.WriteLine("PASS: manual adjustment kills and discards pending synchronization.");

        Invoke(window, "ResetSubtitleDelay");
        window.FindControl<Button>("SynchronizeSubtitleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => WorkerStarted(profile));
        Open(window, second);
        await WaitUntilAsync(() => string.Equals(player.GetProperty<string>("path"), second, StringComparison.OrdinalIgnoreCase)
            && window.FindControl<Button>("SynchronizeSubtitleButton")!.IsEnabled
            && SelectedExternalSubtitle(player) == cache.DownloadPath(second, "tr"));
        Invoke(window, "AdjustSubtitleDelay", 1.0); // Cancel the second video's worker too.
        await WaitUntilAsync(() => !WorkerDirectoryExists(profile));
        Require(SelectedExternalSubtitle(player) == cache.DownloadPath(second, "tr"), "An old worker must never attach to the next episode.");
        Require(File.ReadAllText(SelectedExternalSubtitle(player)).Contains("SECOND_EPISODE", StringComparison.Ordinal), "The repository download must match the new episode.");
        Console.WriteLine("PASS: switching episodes discards the previous video's result.");

        var audioSelector = window.FindControl<ListBox>("AudioTrackList")!;
        window.FindControl<Button>("SynchronizeSubtitleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => WorkerStarted(profile));
        audioSelector.SelectedIndex = 1; // index 0 is off, index 1 is the silent audio stream.
        await WaitUntilAsync(() => !WorkerDirectoryExists(profile));
        Require(player.GetProperty<int>("aid") == 1, "Audio selection must remain under user control.");

        Invoke(window, "SavePreferences", preferences with { AutoSynchronizeSubtitles = false });
        var embedded = Path.Combine(mediaDirectory, "Embedded.Show.S02E03.mkv");
        File.Copy(Path.Combine(fixtures, "multitrack.mkv"), embedded);
        Open(window, embedded);
        await WaitUntilAsync(() => SelectedExternalSubtitle(player) == cache.DownloadPath(embedded, "tr"));
        Require(repository.DownloadCount == 3, "A matching Turkish embedded track must not suppress online discovery.");
        Require(File.ReadAllText(SelectedExternalSubtitle(player)).Contains("REMOTE_EMBEDDED", StringComparison.Ordinal), "The downloaded subtitle must replace the embedded choice.");
        Console.WriteLine("PASS: embedded Turkish subtitles do not suppress repository search and download.");

        var movie = Path.Combine(mediaDirectory, "Example.Film.2020.mkv");
        File.Copy(Path.Combine(fixtures, "speech.mkv"), movie);
        Open(window, movie);
        await WaitUntilAsync(() => SelectedExternalSubtitle(player) == cache.DownloadPath(movie, "tr"));
        Require(repository.DownloadCount == 4, "Movie playback must request the movie subtitle.");
        Require(File.ReadAllText(SelectedExternalSubtitle(player)).Contains("REMOTE_MOVIE", StringComparison.Ordinal), "The repository result must belong to the film.");
        Console.WriteLine("PASS: automatic repository download also works for films.");
        repository.RefreshMovieSubtitle();
        await (Task)Invoke(window, "RunSubtitleAutomationAsync", true, false, true)!;
        Require(repository.DownloadCount == 5 && status.Text == UiText.SubtitleDownloadedFromOpenSubtitlesAndLoaded,
            "An explicit repository lookup must refresh the downloaded subtitle.");
        player.SetProperty("pause", true);
        player.Seek(6.0, SeekOption.Absolute | SeekOption.Exact).Invoke();
        await WaitUntilAsync(() => player.GetProperty<string>("sub-text")?.Contains("REMOTE_MOVIE_REFRESH", StringComparison.Ordinal) == true);
        Console.WriteLine("PASS: explicit online search refreshes and reloads the repository subtitle.");
    }

    private static bool WorkerDirectoryExists(string profile) => Directory.Exists(Path.Combine(profile, "Subtitles"))
        && Directory.EnumerateDirectories(Path.Combine(profile, "Subtitles"), "work-*").Any();
    private static bool WorkerStarted(string profile) => Directory.Exists(Path.Combine(profile, "Subtitles"))
        && Directory.EnumerateFiles(Path.Combine(profile, "Subtitles"), "started.txt", SearchOption.AllDirectories).Any();

    private static string SelectedExternalSubtitle(MpvContext player)
    {
        var count = player.GetProperty<int>("track-list/count");
        for (var index = 0; index < count; index++)
        {
            if (player.GetProperty<string>($"track-list/{index}/type") != "sub"
                || !player.GetProperty<bool>($"track-list/{index}/selected")) continue;
            return player.GetProperty<string>($"track-list/{index}/external-filename") ?? "";
        }
        return "";
    }

    private static int RunSlowSynchronizer(string[] args)
    {
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "started.txt"), "ready");
        Thread.Sleep(TimeSpan.FromSeconds(5));
        var input = args[Array.IndexOf(args, "-i") + 1];
        var output = args[Array.IndexOf(args, "-o") + 1];
        File.Copy(input, output, overwrite: true);
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "ffsubsync.log"),
            "score: 1000\noffset seconds: -3\nframerate scale factor: 1.0\ntotal of speech segments: 1000");
        return 0;
    }

    // Exercise the application's real handlers without sending global keyboard/mouse input to the user's desktop.
    private static void Open(MainWindow window, string path) => Invoke(window, "OpenVideosFromAnotherInstance", (object)new[] { path });
    private static object? Invoke(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? operationTimeout = null)
    {
        using var timeout = new CancellationTokenSource(operationTimeout ?? OperationTimeout);
        while (true)
        {
            try { if (predicate()) return; }
            catch (MpvException) { }
            await Task.Delay(25, timeout.Token);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureRepository : HttpMessageHandler
    {
        private readonly Dictionary<int, string> _subtitles;
        public string FirstEpisodeSubtitle { get; }
        public int DownloadCount { get; private set; }
        public void RefreshMovieSubtitle() => _subtitles[5000] = _subtitles[5000].Replace("REMOTE_MOVIE", "REMOTE_MOVIE_REFRESH");

        public FixtureRepository(string reference)
        {
            FirstEpisodeSubtitle = TimestampPattern().Replace(reference, match => TimeSpan.FromSeconds(
                TimeSpan.ParseExact(match.Value.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture).TotalSeconds + 3)
                .ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture));
            _subtitles = new()
            {
                [1001] = FirstEpisodeSubtitle,
                [1002] = FirstEpisodeSubtitle.Replace("train", "SECOND_EPISODE"),
                [1203] = "1\n00:00:00,000 --> 00:00:03,000\nREMOTE_EMBEDDED\n",
                [5000] = FirstEpisodeSubtitle.Replace("train", "REMOTE_MOVIE")
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/api/v1/subtitles")
            {
                Require(request.Headers.GetValues("Api-Key").Single() == "fixture-key", "The fixture API key should reach only the repository.");
                var query = uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
                    .ToDictionary(pair => pair[0], pair => WebUtility.UrlDecode(pair[1]));
                var episode = query["type"] == "episode";
                var season = episode ? int.Parse(query["season_number"], CultureInfo.InvariantCulture) : 0;
                var number = episode ? int.Parse(query["episode_number"], CultureInfo.InvariantCulture) : 0;
                var fileId = episode ? 1000 + (season == 2 ? 200 : 0) + number : 5000;
                object feature = episode
                    ? new { feature_type = "Episode", parent_title = season == 2 ? "Embedded Show" : "Example Show", season_number = season, episode_number = number }
                    : new { feature_type = "Movie", title = "Example Film", year = 2020 };
                return Json(new
                {
                    data = new[] { new { attributes = new { language = "tr", moviehash_match = query.ContainsKey("moviehash"),
                    feature_details = feature, files = new[] { new { file_id = fileId } }, release = "fixture", download_count = 10 } } }
                });
            }
            if (uri.AbsolutePath == "/api/v1/download")
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                var fileId = body.GetProperty("file_id").GetInt32();
                DownloadCount++;
                return Json(new { link = $"https://repository-fixture.example/{fileId}.srt" });
            }
            Require(uri.Host == "repository-fixture.example", "Unexpected download host.");
            Require(!request.Headers.Contains("Api-Key"), "Download hosts must not receive API keys.");
            var requestedId = int.Parse(Path.GetFileNameWithoutExtension(uri.AbsolutePath), CultureInfo.InvariantCulture);
            return new(HttpStatusCode.OK) { Content = new StringContent(_subtitles[requestedId], Encoding.UTF8) };
        }

        private static HttpResponseMessage Json(object content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json")
        };
    }

    [GeneratedRegex(@"\d{2}:\d{2}:\d{2},\d{3}")]
    private static partial Regex TimestampPattern();
}
