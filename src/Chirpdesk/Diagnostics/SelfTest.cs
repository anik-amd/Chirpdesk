using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using Chirpdesk.Core.CommandBar;
using Chirpdesk.Core.Blocking;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Lifecycle;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Plugins;
using Chirpdesk.Core.Settings;
using Chirpdesk.Core.Storage;
using Chirpdesk.Core.Theming;
using Chirpdesk.Core.Time;
using Chirpdesk.Core.Updates;
using Chirpdesk.Definitions;
using Chirpdesk.Hosting;
using Chirpdesk.Interop;
using Chirpdesk.Notifications;
using Chirpdesk.Storage;
using Windows.UI.Notifications;

namespace Chirpdesk.Diagnostics;

/// <summary>
/// The automated half of the phase-0 gate: everything a machine can judge, headless, no login.
/// The human half (QR scan, a real message, a visible flash) stays in the checklist.
/// </summary>
internal sealed class SelfTest
{
    private readonly StartupOptions _options;
    private readonly HostWindow _host;
    private readonly ILog _log;
    private readonly List<string> _lines = [];
    private int _failures;

    private SelfTest(StartupOptions options, HostWindow host, ILog log) =>
        (_options, _host, _log) = (options, host, log);

    public static async Task<int> RunAsync(StartupOptions options, HostWindow host, ILog log)
    {
        var test = new SelfTest(options, host, log);
        return await test.ExecuteAsync().ConfigureAwait(true);
    }

    private async Task<int> ExecuteAsync()
    {
        // The window must be created before anything inspects it, and the report always
        // reflects every check that ran, even when one of them throws.
        CheckWebViewRuntime();

        Guarded("settings", CheckSettings);
        Guarded("app-identity", CheckAppIdentity);
        Guarded("app-icon", CheckAppIcon);
        Guarded("contrast", CheckContrast);
        Guarded("tray", CheckTray);
        Guarded("window-hidden", CheckWindowIsHidden);
        Guarded("single-instance", CheckSingleInstance);
        Guarded("definitions", CheckDefinitions);
        Guarded("catalog", CheckCatalog);
        Guarded("blocking", CheckBlocking);
        Guarded("update-policy", CheckUpdatePolicy);
        Guarded("migration", CheckMigration);
        Guarded("bar-state", CheckBarState);
        Guarded("storage", CheckStorage);
        Guarded("orphans", CheckOrphans);
        Guarded("dnd", CheckDnd);
        Guarded("autostart", CheckAutostart);

        try
        {
            var environmentOk = await CheckEnvironmentAsync().ConfigureAwait(true);
            Check("environment", environmentOk, environmentOk ? "created under the profiles root, cached per profile" : "could not create or reuse a profile environment");
        }
        catch (Exception ex)
        {
            Check("environment", false, ex.Message);
            _log.Error("environment check crashed", ex);
        }

        await CheckRegistryAsync().ConfigureAwait(true);

        var probeDirectory = Path.Combine(AppPaths.ProfilesRoot, "__probe");
        try
        {
            var probe = await HiddenWebViewProbe.RunAsync(_host, _log, probeDirectory, _options.Soak).ConfigureAwait(true);

            Check(
                "hidden-warm",
                probe.TimerHealthy,
                probe.Error is null
                    ? $"timers={probe.Ticks}/{probe.ExpectedTicks} ready={probe.ReadyState}"
                    : probe.Error);

            Check(
                "notification-capability",
                probe.NotificationEventFired,
                $"event={probe.NotificationEventFired} script={probe.NotificationScriptRan} permission={probe.NotificationPermission}");
        }
        catch (Exception ex)
        {
            Check("hidden-warm", false, ex.Message);
            _log.Error("hidden-warm check crashed", ex);
        }

        CheckToastShown();
        await Task.Delay(1200).ConfigureAwait(true);

        var released = await TryDeleteAsync(probeDirectory).ConfigureAwait(true);
        Check("profile-released", released, released ? "no process is holding the profile" : "profile still locked after dispose");

        WriteReport();

        return _failures == 0 ? 0 : 1;
    }

    private void Check(string name, bool ok, string detail, bool soft = false)
    {
        var status = ok ? "PASS" : soft ? "WARN" : "FAIL";
        if (!ok && !soft)
        {
            _failures++;
        }

        _lines.Add($"[{status}] {name} - {detail}");
        ConsoleBridge.Write($"[{status}] {name} - {detail}");
    }

    /// <summary>A throwing check fails one line, never the whole gate.</summary>
    private void Guarded(string name, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Check(name, false, ex.Message);
            _log.Error($"self-test '{name}' crashed", ex);
        }
    }

    private void CheckWebViewRuntime()
    {
        var version = WebViewHost.BrowserVersion;
        Check("webview2-runtime", version.Length > 0, version.Length > 0 ? version : "Evergreen runtime not found");
    }

    private void CheckSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chirpdesk-selftest", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new SettingsStore(path, _log);
            var settings = new AppSettings();
            settings.Services.Add(new ServiceInstance
            {
                InstanceId = "probe",
                Name = "Probe",
                Url = "https://probe.example/",
            });

            store.Save(settings);
            var loaded = store.Load();

            File.WriteAllText(path, "{ not json");
            var recovered = store.Load();

            Check(
                "settings",
                loaded.Services.Count == 1
                && loaded.Services[0].InstanceId == "probe"
                && recovered.Services.Count == 0
                && File.Exists(path + ".corrupt"),
                "round-trip, atomic replace and corrupt-file quarantine");
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            Check("settings", false, ex.Message);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private void CheckDefinitions()
    {
        var temp = Path.Combine(Path.GetTempPath(), "chirpdesk-selftest-plugins", Guid.NewGuid().ToString("N"));

        try
        {
            var bundledRoot = new PluginScanRoot(AppPaths.BundledPluginsDirectory, Bundled: true);
            var bundled = PluginLoader.LoadServiceDefinitions([bundledRoot], _log);
            string[] expected = ["whatsapp", "messenger", "telegram", "facebook", "instagram"];
            var present = expected.All(id => bundled.Definitions.Any(definition => definition.Id == id));
            var clean = bundled.Errors.Count == 0;

            var bad = Path.Combine(temp, "bad-root", "bad");
            Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "plugin.json"), """
                { "id": "bad id", "kind": "service", "name": "Bad", "version": "1.0.0", "apiVersion": 1 }
                """);
            File.WriteAllText(Path.Combine(bad, "service.json"), """
                { "url": "https://bad.example/" }
                """);
            var skipped = PluginLoader.LoadServiceDefinitions(
                [new PluginScanRoot(Path.Combine(temp, "bad-root"), Bundled: false)],
                _log);

            var shadow = Path.Combine(temp, "shadow-root", "whatsapp");
            Directory.CreateDirectory(shadow);
            File.WriteAllText(Path.Combine(shadow, "plugin.json"), """
                { "id": "whatsapp", "kind": "service", "name": "Shadow", "version": "1.0.0", "apiVersion": 1 }
                """);
            File.WriteAllText(Path.Combine(shadow, "service.json"), """
                { "url": "https://shadow.example/" }
                """);
            var shadowed = PluginLoader.LoadServiceDefinitions(
                [bundledRoot, new PluginScanRoot(Path.Combine(temp, "shadow-root"), Bundled: false)],
                _log);
            var shadowDefinition = shadowed.Definitions.FirstOrDefault(definition => definition.Id == "whatsapp");
            var shadowWorks = shadowDefinition?.Url == "https://shadow.example/"
                && shadowed.Shadowed.Contains("whatsapp");

            Check(
                "definitions",
                present && clean && skipped.Errors.Count == 1 && skipped.Definitions.Count == 0 && shadowWorks,
                $"bundled={bundled.Definitions.Count} skipped=1 shadowed=whatsapp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Check("definitions", false, ex.Message);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private void CheckBlocking()
    {
        var packs = PluginCatalog.LoadFilters(_log);
        var bundled = packs.Where(pack => pack.Id == "filters-core").ToList();
        var matcher = new RequestMatcher("https://web.whatsapp.com/", bundled);

        var allowlist = matcher.Evaluate("https://web.whatsapp.com/ws") == RequestVerdict.Allow
            && matcher.Evaluate("https://example.org/oauth/callback") == RequestVerdict.Allow;
        var blocks = matcher.Evaluate("https://ads.doubleclick.net/pixel") == RequestVerdict.Block;
        var cosmetic = matcher.CosmeticSelectors.Count > 0;

        var broken = new RequestMatcher(
            "https://web.whatsapp.com/",
            [new FilterPack { Id = "broken", BlockHosts = ["bad host", "https://nope"] }]);
        var failOpen = broken.SkippedRules >= 2
            && broken.Evaluate("https://safe.example/x") == RequestVerdict.Allow;

        Check(
            "blocking",
            allowlist && blocks && cosmetic && failOpen && bundled.Count == 1,
            $"allowlist={allowlist} blocks={blocks} cosmetic={cosmetic} fail-open={failOpen} packs={bundled.Count}");
    }

    private void CheckCatalog()
    {
        var definition = new ServiceDefinition
        {
            Id = "probe",
            Name = "Probe",
            Url = "https://probe.example/",
            UnreadRegex = @"\((\d+)\)",
            ApiVersion = DefinitionValidator.HostApiVersion,
        };

        var catalog = new ServiceCatalog([definition]);
        var fallback = catalog.Resolve(new ServiceInstance
        {
            InstanceId = "fallback",
            DefinitionId = "probe",
        });
        var overridden = catalog.Resolve(new ServiceInstance
        {
            InstanceId = "override",
            DefinitionId = "probe",
            Name = "Probe Work",
            Url = "https://probe.example/work",
        });
        var unknown = catalog.Resolve(new ServiceInstance
        {
            InstanceId = "unknown",
            DefinitionId = "missing",
        });

        Check(
            "catalog",
            fallback?.Url == definition.Url
            && overridden?.Name == "Probe Work"
            && overridden.Url == "https://probe.example/work"
            && unknown is null,
            "instance overrides, definition fallback, unknown id fails closed");
    }

    private void CheckMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chirpdesk-selftest-migration", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "services": [
                    {
                      "instanceId": "legacy-chat",
                      "definitionId": null,
                      "name": "Legacy Chat",
                      "url": "https://legacy.example/",
                      "warmAtBoot": true
                    }
                  ]
                }
                """);

            var loaded = new SettingsStore(path, _log).Load();
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var instance = loaded.Services.FirstOrDefault();

            Check(
                "migration",
                instance?.InstanceId == "legacy-chat"
                && instance.Url == "https://legacy.example/"
                && document.RootElement.GetProperty("schemaVersion").GetInt32() == AppSettings.CurrentSchemaVersion,
                $"v1 fixture migrated to v{AppSettings.CurrentSchemaVersion}, instance preserved");
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            Check("migration", false, ex.Message);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private void CheckBarState()
    {
        var cycle = BarState.ForCycle(["a", "b", "c"], activeId: "a", direction: 1);
        var startsNext = cycle.Selected == "b";
        cycle.Move(1);
        cycle.Move(1);
        var wraps = cycle.Selected == "a";
        var escCancels = BarKeys.Decide(cycle, BarKey.Escape, BarModifiers.None).Kind == BarKeyActionKind.Cancel;
        var ctrlWCloses = BarKeys.Decide(cycle, BarKey.W, BarModifiers.Control).Kind == BarKeyActionKind.CloseTab;

        var search = BarState.ForSearch();
        search.ShowResults(["telegram", "messenger"]);
        search.Append("tel");
        var digitsSelect = BarKeys.Decide(search, BarKey.Digit2, BarModifiers.Control).Value == 1;
        var ranked = SearchIndex.Rank(
            "tel",
            [
                new SearchCandidate("telegram", "Telegram", SearchKind.Warm),
                new SearchCandidate("telethon", "Telethon", SearchKind.Cold),
            ]);

        Check(
            "bar-state",
            startsNext && wraps && escCancels && ctrlWCloses && digitsSelect && ranked[0].Id == "telegram",
            "cycle wraps, Esc cancels the palette, Ctrl+W closes, Ctrl+2 selects, prefix ranking");
    }

    private async Task CheckRegistryAsync()
    {
        var firstDirectory = Path.Combine(AppPaths.ProfilesRoot, "__registry-a");
        var secondDirectory = Path.Combine(AppPaths.ProfilesRoot, "__registry-b");
        ServiceRegistry? registry = null;
        var ok = false;
        var detail = string.Empty;

        try
        {
            registry = new ServiceRegistry(_log);
            await registry.AddAsync(await CreateProbeRuntimeAsync("registry-a", "__registry-a")).ConfigureAwait(true);
            await registry.AddAsync(await CreateProbeRuntimeAsync("registry-b", "__registry-b")).ConfigureAwait(true);

            registry.Activate("registry-a");
            registry.Activate("registry-b");
            registry.Activate("registry-a");

            var orderOk = registry.Order.SequenceEqual(["registry-a", "registry-b"]);
            var aliveOk = registry.Find("registry-a") is not null && registry.Find("registry-b") is not null;

            registry.Cycle(1);
            var cycleOk = registry.Active?.InstanceId == "registry-b";

            var sleptA = await registry.SleepAsync("registry-a").ConfigureAwait(true)
                && registry.StateOf("registry-a") == RuntimeState.Asleep
                && registry.Find("registry-a") is null
                && registry.Order.SequenceEqual(["registry-b"]);

            var sleptB = await registry.SleepAsync("registry-b").ConfigureAwait(true)
                && registry.StateOf("registry-b") == RuntimeState.Asleep
                && registry.Active is null;

            await registry.AddAsync(await CreateProbeRuntimeAsync("registry-a", "__registry-a")).ConfigureAwait(true);
            var woke = registry.StateOf("registry-a") == RuntimeState.Warm
                && registry.Activate("registry-a")
                && registry.Active?.InstanceId == "registry-a";

            ok = orderOk && aliveOk && cycleOk && sleptA && sleptB && woke;
            detail = $"mru={string.Join(',', registry.Order)} cycleOk={cycleOk} sleepOk={sleptA && sleptB} wakeOk={woke}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            detail = ex.Message;
            _log.Error("registry check crashed", ex);
        }
        finally
        {
            if (registry is not null)
            {
                await registry.DisposeAsync().ConfigureAwait(true);
            }

            WebViewHost.Forget(firstDirectory);
            WebViewHost.Forget(secondDirectory);
            _ = await TryDeleteAsync(firstDirectory).ConfigureAwait(true);
            _ = await TryDeleteAsync(secondDirectory).ConfigureAwait(true);
        }

        Check("registry", ok, detail);
    }

    private Task<ServiceRuntime> CreateProbeRuntimeAsync(string instanceId, string profileId) =>
        ServiceRuntime.CreateAsync(
            _host,
            new ServiceInstance
            {
                InstanceId = instanceId,
                Name = instanceId,
                Url = "https://example.invalid/",
                ProfileId = profileId,
            },
            _log,
            SystemClock.Instance,
            new NoToast(),
            DateTimeOffset.UtcNow,
            darkTheme: false);

    private sealed class NoToast : IToastSink
    {
        public void Show(ToastMessage message)
        {
        }
    }

    private void CheckStorage()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "chirpdesk-selftest-storage", Guid.NewGuid().ToString("N"));

        try
        {
            var profiles = Path.Combine(temporary, "profiles");
            var profile = Path.Combine(profiles, "probe");
            Directory.CreateDirectory(Path.Combine(profile, "Cache"));
            Directory.CreateDirectory(Path.Combine(profile, "Network"));
            File.WriteAllBytes(Path.Combine(profile, "Cache", "cache.bin"), new byte[40]);
            File.WriteAllBytes(Path.Combine(profile, "Network", "Cookies"), new byte[10]);

            var usage = ProfileScanner.ScanRoot(profiles).Single();
            var plan = TrimPlanner.Plan(
                [new ProfileCache(usage.ProfileId, profile, usage.CacheBytes, Disposed: true, DateTimeOffset.UtcNow)],
                perProfileBudget: 0,
                globalBudget: 0);

            Check(
                "storage",
                usage.DataBytes == 10
                && usage.CacheBytes == 40
                && plan.ReclaimedBytes == 40
                && PathGuard.IsUnder(profiles, profile)
                && !PathGuard.IsUnder(profiles, Path.Combine(temporary, "outside")),
                "Data and Cache separated, cache planned, outside path refused");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void CheckOrphans()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "chirpdesk-selftest-orphans", Guid.NewGuid().ToString("N"));

        try
        {
            var profiles = Path.Combine(temporary, "profiles");
            Directory.CreateDirectory(Path.Combine(profiles, "leftover", "Cache"));
            Directory.CreateDirectory(Path.Combine(profiles, "referenced", "Cache"));
            File.WriteAllBytes(Path.Combine(profiles, "leftover", "Cache", "x"), new byte[8]);

            var known = Path.Combine(temporary, "KnownApp");
            Directory.CreateDirectory(known);
            File.WriteAllBytes(Path.Combine(known, "data.bin"), new byte[4]);

            var found = OrphanScanner.Scan(
                profiles,
                new HashSet<string> { "referenced" },
                [new ScanRoot(known, "KnownApp")],
                _ => false);

            Check(
                "orphans",
                found.Count == 2
                && found.Any(item => item.ProfileId == "leftover")
                && found.Any(item => item.App == "KnownApp")
                && found.All(item => item.ProfileId != "referenced"),
                "unreferenced profile and known root found, referenced profile skipped");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void CheckDnd()
    {
        var now = DateTimeOffset.UtcNow;

        Check(
            "dnd",
            NotificationPolicy.ShouldSuppress(muted: true, dndUntil: null, now)
            && NotificationPolicy.ShouldSuppress(muted: false, dndUntil: now.AddMinutes(5), now)
            && !NotificationPolicy.ShouldSuppress(muted: false, dndUntil: now.AddMinutes(-5), now)
            && !NotificationPolicy.ShouldSuppress(muted: false, dndUntil: null, now),
            "mute and live DND suppress; expired DND does not");
    }

    private void CheckAutostart()
    {
        var executable = Environment.ProcessPath ?? "Chirpdesk.exe";
        var command = AutostartEntry.Command(executable);

        Check(
            "autostart",
            AutostartEntry.IsCurrent(command, executable)
            && !AutostartEntry.IsCurrent(null, executable)
            && !AutostartEntry.IsCurrent("Chirpdesk.exe", executable),
            "quoted command and idempotence");
    }

    private void CheckAppIdentity()
    {
        var shortcut = Aumid.ShortcutPath;
        var toasts = new ToastService(_log);
        var available = toasts.IsAvailable;
        var processId = Aumid.CurrentProcessAppUserModelId();

        Check(
            "app-identity",
            File.Exists(shortcut) && available && string.Equals(processId, Aumid.AppId, StringComparison.Ordinal),
            $"shortcut={File.Exists(shortcut)} notifier={available} aumid={processId}");
    }

    /// <summary>
    /// Exercises the same decisions as the release verifier without a network or installer:
    /// a signed newer manifest is accepted; tampering, replay and a wrong key are refused.
    /// </summary>
    private void CheckUpdatePolicy()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var manifest = new UpdateManifest
        {
            Version = "9.9.9",
            Channel = "stable",
            Url = "https://example.org/Chirpdesk-Setup.exe",
            Size = 3,
            Sha256 = new string('a', 64),
        };

        manifest.Signature = Sign(key, manifest);
        var pem = key.ExportSubjectPublicKeyInfoPem();

        var acceptsSigned = UpdateVerifier.IsAcceptable(manifest, pem, new Version(1, 0, 0, 0), out _);

        var tampered = With(manifest, url: "https://example.org/Other.exe");
        var refusesTamper = !UpdateVerifier.IsAcceptable(tampered, pem, new Version(1, 0, 0, 0), out _);

        var replay = With(manifest, version: "0.0.1");
        replay.Signature = Sign(key, replay);
        var refusesReplay = !UpdateVerifier.IsAcceptable(replay, pem, new Version(2, 0, 0, 0), out _);

        var refusesWrongKey = !UpdateVerifier.IsAcceptable(
            manifest, other.ExportSubjectPublicKeyInfoPem(), new Version(1, 0, 0, 0), out _);

        var policy = acceptsSigned && refusesTamper && refusesReplay && refusesWrongKey;

        Check(
            "update-policy",
            policy,
            $"signed={acceptsSigned} tamper={refusesTamper} replay={refusesReplay} wrong-key={refusesWrongKey}");
    }

    private static UpdateManifest With(UpdateManifest source, string? version = null, string? url = null) => new()
    {
        Version = version ?? source.Version,
        Channel = source.Channel,
        Url = url ?? source.Url,
        Size = source.Size,
        Sha256 = source.Sha256,
        Signature = source.Signature,
    };

    private static string Sign(ECDsa key, UpdateManifest manifest) =>
        Convert.ToBase64String(key.SignData(
            UpdateManifestCodec.CanonicalBytes(manifest),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>
    /// The exe, window and tray must all resolve the shipped mark. A missing resource is a
    /// blank taskbar icon, which is easy to miss until the app is running from an installer.
    /// </summary>
    private void CheckAppIcon()
    {
        var app = LoadIcon("chirpdesk.ico");
        var tray = TrayIconFactory.CreateIcon(unread: false) as BitmapSource;
        var unread = TrayIconFactory.CreateIcon(unread: true) as BitmapSource;

        var sizes = app.Frames.Select(frame => frame.PixelWidth).ToArray();
        var ok = _host.Icon is not null
            && sizes.Contains(16)
            && sizes.Contains(256)
            && tray is { PixelWidth: >= 16 }
            && unread is { PixelWidth: >= 16 };

        Check(
            "app-icon",
            ok,
            $"window={_host.Icon is not null} frames={string.Join(',', sizes)} tray={tray?.PixelWidth} unread={unread?.PixelWidth}");
    }

    private static BitmapDecoder LoadIcon(string name) =>
        BitmapDecoder.Create(
            new Uri($"pack://application:,,,/Chirpdesk;component/Assets/{name}", UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

    /// <summary>
    /// Both token dictionaries are read from disk and the pairs that carry text are checked
    /// against WCAG AA. A palette edit that lowers contrast fails the gate instead of shipping.
    /// </summary>
    private void CheckContrast()
    {
        var failures = new List<string>();

        foreach (var name in new[] { "TokensLight", "TokensDark" })
        {
            var tokens = LoadTokens(name);
            if (tokens is null)
            {
                failures.Add($"{name} missing");
                continue;
            }

            foreach (var (foreground, background, minimum) in ContrastPairs)
            {
                if (ColorOf(tokens, foreground) is not { } fg || ColorOf(tokens, background) is not { } bg)
                {
                    failures.Add($"{name}:{foreground}/{background} missing");
                    continue;
                }

                var ratio = Contrast.Ratio(fg, bg);
                if (ratio < minimum)
                {
                    failures.Add($"{name} {foreground}/{background}={ratio:0.00}<{minimum:0.0}");
                }
            }
        }

        Check(
            "contrast",
            failures.Count == 0,
            failures.Count == 0
                ? "light and dark pass AA for text, muted text, accent, danger and palette pairs"
                : string.Join("; ", failures));
    }

    private static readonly (string Foreground, string Background, double Minimum)[] ContrastPairs =
    [
        ("CdText", "CdSurface", 4.5),
        ("CdText", "CdCanvas", 4.5),
        ("CdText", "CdSurfaceAlt", 4.5),
        ("CdTextMuted", "CdSurface", 4.5),
        ("CdTextMuted", "CdCanvas", 4.5),
        ("CdAccent", "CdSurface", 4.5),
        ("CdOnAccent", "CdAccent", 4.5),
        ("CdDanger", "CdSurface", 4.5),
        ("CdPaletteText", "CdPaletteRaised", 4.5),
        ("CdPaletteMuted", "CdPaletteRaised", 4.5),
        ("CdPaletteAccent", "CdPaletteRaised", 4.5),
    ];

    private ResourceDictionary? LoadTokens(string name)
    {
        try
        {
            return new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Chirpdesk;component/Ui/{name}.xaml", UriKind.Absolute),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"could not load {name} for contrast validation: {ex.Message}");
            return null;
        }
    }

    private static Rgb? ColorOf(ResourceDictionary tokens, string key) =>
        tokens[key] is System.Windows.Media.SolidColorBrush brush
            ? new Rgb(brush.Color.R, brush.Color.G, brush.Color.B)
            : null;

    /// <summary>The tray icon is a real Win32 icon; if the library cannot create one nothing else shows.</summary>
    private void CheckTray()
    {
        var tray = TrayIconFactory.Create(open: static () => { }, hide: static () => { }, settings: static () => { }, quit: static () => { });
        try
        {
            Check("tray", tray.IconSource is not null, $"icon={tray.IconSource is not null} tooltip={tray.ToolTipText}");
        }
        finally
        {
            tray.Dispose();
        }
    }

    private void CheckWindowIsHidden()
    {
        var hwnd = _host.Handle;
        var toolWindow = WindowChrome.IsToolWindow(hwnd);
        var appWindow = WindowChrome.IsAppWindow(hwnd);
        var visible = NativeMethods.IsWindowVisible(hwnd);
        var hasRect = NativeMethods.GetWindowRect(hwnd, out var rect);
        var offScreen = hasRect && rect.Left <= -30_000 && rect.Top <= -30_000;

        Check(
            "window-hidden",
            toolWindow && !appWindow && visible && offScreen,
            $"toolwindow={toolWindow} appwindow={appWindow} visible={visible} at={rect.Left},{rect.Top}");
    }

    /// <summary>Re-launches the app: it must see the mutex and exit without a window.</summary>
    private void CheckSingleInstance()
    {
        var start = StartInfo();
        if (start is null)
        {
            Check("single-instance", false, "could not resolve the executable path", soft: true);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        using var second = Process.Start(start);
        if (second is null)
        {
            Check("single-instance", false, "second process did not start", soft: true);
            return;
        }

        var exited = second.WaitForExit(5000);
        if (!exited)
        {
            try
            {
                second.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        Check(
            "single-instance",
            exited && second.ExitCode == 0 && second.MainWindowHandle == nint.Zero,
            $"exited={exited} code={(exited ? second.ExitCode : -1)} windows={second.MainWindowHandle != nint.Zero} in {stopwatch.ElapsedMilliseconds} ms");
    }

    private static async Task<bool> CheckEnvironmentAsync()
    {
        var directory = Path.Combine(AppPaths.ProfilesRoot, "__envcheck");

        try
        {
            var first = await WebViewHost.CreateEnvironmentAsync("__envcheck", directory).ConfigureAwait(true);
            var second = await WebViewHost.CreateEnvironmentAsync("__envcheck", directory).ConfigureAwait(true);
            return Directory.Exists(directory) && ReferenceEquals(first, second);
        }
        finally
        {
            WebViewHost.Forget(directory);
            TryDelete(directory);
        }
    }

    private void CheckToastShown()
    {
        var toasts = new ToastService(_log);
        var toast = toasts.ShowAndTrack(new ToastMessage("Chirpdesk self-test", "If you can read this, toasts work.", null, "selftest"));

        var found = false;
        try
        {
            found = ToastNotificationManager.History.GetHistory(Aumid.AppId).Count > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            _log.Warn($"could not read toast history: {ex.Message}");
        }

        // Soft: Windows can have notifications switched off for this app, which is a user setting,
        // not a defect in Chirpdesk.
        Check("toast-shown", found, found ? "appeared in Action Center history" : "not in history (notifications may be off)", soft: true);
        toasts.Hide(toast);
    }

    private static ProcessStartInfo? StartInfo()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return null;
        }

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        return info;
    }

    private static async Task<bool> TryDeleteAsync(string directory)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!Directory.Exists(directory))
            {
                return true;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(250);
            }
        }

        return false;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is harmless.
        }
    }

    private void WriteReport()
    {
        var report = string.Join(Environment.NewLine, _lines) + Environment.NewLine;

        try
        {
            Directory.CreateDirectory(AppPaths.LogsRoot);
            File.WriteAllText(AppPaths.SelfTestReport, report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("could not write the self-test report", ex);
        }

        ConsoleBridge.Write(report);
    }
}
