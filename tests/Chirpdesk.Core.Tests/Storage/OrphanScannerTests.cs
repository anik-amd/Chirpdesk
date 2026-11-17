using Chirpdesk.Core.Storage;

namespace Chirpdesk.Core.Tests.Storage;

public sealed class OrphanScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "chirpdesk-orphan-tests",
        Guid.NewGuid().ToString("N"));

    public OrphanScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Harmless leftover.
        }
    }

    [Fact]
    public void An_unreferenced_profile_is_reported()
    {
        var profiles = ProfilesRoot();
        WriteFile(Path.Combine(profiles, "leftover", "Cache", "data.bin"), 32);

        var found = OrphanScanner.Scan(profiles, new HashSet<string>(), [], _ => false);

        var item = Assert.Single(found);
        Assert.Equal("leftover", item.ProfileId);
        Assert.Equal(32, item.Bytes);
        Assert.True(item.IsChirpdeskProfile);
    }

    [Fact]
    public void A_referenced_profile_is_never_reported()
    {
        var profiles = ProfilesRoot();
        WriteFile(Path.Combine(profiles, "whatsapp", "Cache", "data.bin"), 10);

        var found = OrphanScanner.Scan(profiles, new HashSet<string> { "whatsapp" }, [], _ => false);

        Assert.Empty(found);
    }

    [Fact]
    public void A_known_app_root_is_reported_with_its_size()
    {
        var whatsapp = Path.Combine(_root, "WhatsApp");
        WriteFile(Path.Combine(whatsapp, "message.db"), 64);
        var profiles = ProfilesRoot();

        var found = OrphanScanner.Scan(
            profiles,
            new HashSet<string>(),
            [new ScanRoot(whatsapp, "WhatsApp")],
            _ => false);

        var item = Assert.Single(found);
        Assert.Equal("WhatsApp", item.App);
        Assert.Equal(64, item.Bytes);
        Assert.False(item.IsChirpdeskProfile);
    }

    [Fact]
    public void A_directory_owned_by_a_running_app_is_skipped()
    {
        var telegram = Path.Combine(_root, "Telegram Desktop");
        WriteFile(Path.Combine(telegram, "data.bin"), 8);
        var profiles = ProfilesRoot();

        var found = OrphanScanner.Scan(
            profiles,
            new HashSet<string>(),
            [new ScanRoot(telegram, "Telegram")],
            path => path == telegram);

        Assert.Empty(found);
    }

    [Fact]
    public void A_missing_root_is_ignored()
    {
        var profiles = ProfilesRoot();

        var found = OrphanScanner.Scan(
            profiles,
            new HashSet<string>(),
            [new ScanRoot(Path.Combine(_root, "not-there"), "Ghost")],
            _ => false);

        Assert.Empty(found);
    }

    [Fact]
    public void An_unknown_profile_with_its_own_folder_is_ordered_deterministically()
    {
        var profiles = ProfilesRoot();
        WriteFile(Path.Combine(profiles, "zeta", "Cache", "x"), 1);
        WriteFile(Path.Combine(profiles, "alpha", "Cache", "x"), 1);

        var found = OrphanScanner.Scan(profiles, new HashSet<string>(), [], _ => false);

        Assert.Equal(["alpha", "zeta"], found.Select(item => item.ProfileId));
    }

    private string ProfilesRoot()
    {
        var directory = Path.Combine(_root, "profiles");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }
}
