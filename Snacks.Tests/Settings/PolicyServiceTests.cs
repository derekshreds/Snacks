using System.Text.Json;
using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Settings;

/// <summary>
///     Pins the behavior of <see cref="PolicyService"/> — the business logic for the named
///     encoder-policy bundles. The controller is a thin transport layer; these tests cover
///     the seeding, immutability of built-ins, name-collision-on-import, schema-version
///     rejection, and the import-side <see cref="Policy.BuiltIn"/> sanitization.
///
///     Persistence is exercised against a real temp directory via <see cref="SNACKS_WORK_DIR"/>
///     so the atomic write-then-rename path of <see cref="ConfigFileService"/> is part of the
///     test surface. Each test allocates an isolated working directory and disposes it via
///     the <see cref="PolicyTestFixture"/> helper.
/// </summary>
public sealed class PolicyServiceTests
{
    [Fact]
    public void List_seeds_built_ins_on_first_call()
    {
        using var fx = new PolicyTestFixture();

        var policies = fx.Service.List();

        // PM strategy doc scopes the v1 built-in list to 5 use-case-named setups.
        // Names are deliberately outcome-led (Plex-Safe HEVC, Make-It-Small, Archive
        // Master, Old iPod, Clean Up Tracks) - long-tail device presets ship later
        // through the community tap registry.
        policies.Should().HaveCount(5, "the built-in list is scoped to exactly five use-case presets");
        policies.Should().OnlyContain(p => p.BuiltIn, "the first load contains only built-ins");
        policies.Select(p => p.Id).Should().BeEquivalentTo(new[]
        {
            "builtin-plex-safe-hevc",
            "builtin-make-it-small",
            "builtin-archive-master",
            "builtin-old-ipod",
            "builtin-clean-up-tracks",
        });
    }

    [Fact]
    public void PlexSafeHevc_is_the_single_recommended_built_in()
    {
        using var fx = new PolicyTestFixture();
        var policies = fx.Service.List();

        // Per the YouTube / Plex pattern, exactly one policy carries the
        // "Recommended" affordance so the non-expert always has a no-brain pick.
        // Plex-Safe HEVC is the chosen one - it's the Snacks default and the
        // strategy doc's named example.
        var recommended = policies.Where(p => p.Recommended).ToList();
        recommended.Should().HaveCount(1, "exactly one built-in carries the recommendation flag");
        recommended[0].Id.Should().Be("builtin-plex-safe-hevc");
    }

    [Fact]
    public void Built_ins_all_carry_outcome_bullets_and_a_tagline()
    {
        using var fx = new PolicyTestFixture();
        var policies = fx.Service.List();

        // Outcome bullets are the primary explanation surface for non-experts.
        // A built-in that ships without them defeats the entire UX premise of the
        // pane (the consumer designer's outcome-bullets-not-spec-table design).
        // Description acts as the one-line tagline shown beside the picker.
        policies.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Description),
            "every built-in needs a tagline so the picker shows what it does at a glance");
        policies.Should().OnlyContain(p => p.OutcomeBullets.Count >= 3,
            "every built-in needs at least three outcome bullets - the bullet list is the primary explanation for non-experts");
        policies.Should().OnlyContain(p => p.OutcomeBullets.All(b => !string.IsNullOrWhiteSpace(b)),
            "outcome bullets are user-facing strings, no blanks allowed");
    }

    /// <summary>
    ///     iPod 5G/Classic strictly requires H.264 Baseline profile level 3.0 with no
    ///     B-frames and no CABAC. libx264's default High profile output will not play
    ///     on the device, so the preset MUST carry an explicit H264Profile=baseline +
    ///     H264Level=3.0. TranscodingService emits the matching -profile:v / -level /
    ///     -bf 0 / -coder 0 / -pix_fmt yuv420p flags when those fields are set.
    /// </summary>
    [Fact]
    public void Builtin_OldIPod_matches_apple_video_spec()
    {
        using var fx = new PolicyTestFixture();
        var ipod = fx.Service.Get("builtin-old-ipod");

        ipod.Should().NotBeNull();
        ipod!.Options.Format.Should().Be("mp4",          "iPod 5G/Classic only plays MP4 containers");
        ipod.Options.Codec.Should().Be("h264",           "iPod video is H.264 only");
        ipod.Options.Encoder.Should().Be("libx264");
        ipod.Options.H264Profile.Should().Be("baseline", "iPod hardware decoder rejects Main and High profile streams");
        ipod.Options.H264Level.Should().Be("3.0",        "iPod video tops out at level 3.0");
        ipod.Options.DownscalePolicy.Should().Be("CapAtTarget", "downscale only when source is larger than 240p");
        ipod.Options.DownscaleTarget.Should().Be("240p", "iPod Classic display tops out at 320x240");
        ipod.Options.TargetBitrate.Should().Be(1200,     "1200 kbps gives headroom under the 1.5 Mbps spec ceiling");
        ipod.Options.FfmpegQualityPreset.Should().Be("medium", "240p encodes are trivially fast on any modern CPU");
        ipod.Options.PreserveOriginalAudio.Should().BeFalse("iPod requires a single re-encoded AAC stereo track");
        ipod.Options.AudioOutputs.Should().HaveCount(1);
        ipod.Options.AudioOutputs[0].Codec.Should().Be("aac");
        ipod.Options.AudioOutputs[0].Layout.Should().Be("Stereo");
        ipod.Options.AudioOutputs[0].BitrateKbps.Should().Be(128);
    }

    [Fact]
    public void List_persists_seed_so_subsequent_loads_dont_rewrite_unchanged_file()
    {
        using var fx = new PolicyTestFixture();

        fx.Service.List();
        var path = Path.Combine(fx.WorkDir, "config", "policies.json");
        File.Exists(path).Should().BeTrue("first list call seeds the file to disk");

        var firstSnapshot = File.ReadAllText(path);
        fx.Service.List();
        File.ReadAllText(path).Should().Be(firstSnapshot,
            "a second list call with no changes must not rewrite the file — that would force a backup churn");
    }

    [Fact]
    public void Update_rejects_built_in()
    {
        using var fx = new PolicyTestFixture();
        fx.Service.List();

        var act = () => fx.Service.Update("builtin-plex-safe-hevc", "Renamed", null, null, new EncoderOptions());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Built-in*read-only*");
    }

    [Fact]
    public void Delete_rejects_built_in()
    {
        using var fx = new PolicyTestFixture();
        fx.Service.List();

        var act = () => fx.Service.Delete("builtin-plex-safe-hevc");

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Built-in*cannot be deleted*");
    }

    [Fact]
    public void Create_assigns_fresh_id_and_forces_custom_flag()
    {
        using var fx = new PolicyTestFixture();

        var created = fx.Service.Create("My Preset", "for late-night encodes", null, new EncoderOptions { TargetBitrate = 4000 });

        created.Id.Should().StartWith("policy-", "custom policies use the policy- id prefix");
        created.BuiltIn.Should().BeFalse();
        created.Name.Should().Be("My Preset");
        created.Description.Should().Be("for late-night encodes");
        created.Options.TargetBitrate.Should().Be(4000);

        // Round-trips through disk.
        var fromDisk = fx.Service.Get(created.Id);
        fromDisk.Should().NotBeNull();
        fromDisk!.Name.Should().Be("My Preset");
        fromDisk.Options.TargetBitrate.Should().Be(4000);
    }

    [Fact]
    public void Create_unique_names_when_a_collision_exists()
    {
        using var fx = new PolicyTestFixture();

        var a = fx.Service.Create("Preset", null, null, new EncoderOptions());
        var b = fx.Service.Create("Preset", null, null, new EncoderOptions());
        var c = fx.Service.Create("Preset", null, null, new EncoderOptions());

        a.Name.Should().Be("Preset");
        b.Name.Should().Be("Preset (copy)");
        c.Name.Should().Be("Preset (copy) 2");
    }

    /// <summary>
    ///     A user authoring a custom setup for the community (or for export to another
    ///     install) needs to attach a description and outcome bullets the same way
    ///     built-ins do, or the shared file looks half-baked next to the built-ins
    ///     when an importer opens it. This pins the full author surface end-to-end.
    /// </summary>
    [Fact]
    public void Create_roundtrips_description_and_outcome_bullets()
    {
        using var fx = new PolicyTestFixture();

        var bullets = new[]
        {
            "Files end up around a quarter the size",
            "Plays on my LG OLED",
            "Drops Spanish dubs we never watch",
        };

        var created = fx.Service.Create(
            "Living-room setup",
            "Tuned for my LG OLED in the den.",
            bullets,
            new EncoderOptions { TargetBitrate = 2400 });

        created.Description.Should().Be("Tuned for my LG OLED in the den.");
        created.OutcomeBullets.Should().BeEquivalentTo(bullets);

        // After a load from disk the bullets and description must still be present -
        // the PolicyService.LoadAndSeed path round-trips them through JSON.
        var reloaded = fx.Service.Get(created.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Description.Should().Be("Tuned for my LG OLED in the den.");
        reloaded.OutcomeBullets.Should().BeEquivalentTo(bullets);
    }

    [Fact]
    public void Create_sanitizes_bullet_input()
    {
        using var fx = new PolicyTestFixture();

        // Mixed bag: leading/trailing whitespace, blank lines, null entry.
        // Sanitization trims, drops blanks, and the cap clamps runaway pastes.
        var messy = new[]
        {
            "  Files end up about half the size  ",
            "",
            "   ",
            "Drops surround audio",
            null!,
            "Trims black bars",
        };

        var created = fx.Service.Create("Tidy", null, messy, new EncoderOptions());

        created.OutcomeBullets.Should().BeEquivalentTo(new[]
        {
            "Files end up about half the size",
            "Drops surround audio",
            "Trims black bars",
        }, "blank entries and surrounding whitespace must never reach the picker card");
    }

    [Fact]
    public void Create_caps_bullets_at_twelve()
    {
        using var fx = new PolicyTestFixture();

        // 20 entries -> picker should still cap at 12 so the card doesn't become a wall of text.
        var lots = Enumerable.Range(1, 20).Select(i => $"Bullet {i}");

        var created = fx.Service.Create("Verbose", null, lots, new EncoderOptions());

        created.OutcomeBullets.Should().HaveCount(12,
            "the cap prevents a community-shared policy from blowing up the card layout");
        created.OutcomeBullets[0].Should().Be("Bullet 1",  "the cap keeps the FIRST 12 entries, not a random subset");
        created.OutcomeBullets[11].Should().Be("Bullet 12");
    }

    [Fact]
    public void Update_can_edit_description_and_bullets_on_a_custom_policy()
    {
        using var fx = new PolicyTestFixture();

        var created = fx.Service.Create("Original", "first take", new[] { "First bullet" }, new EncoderOptions());

        var updated = fx.Service.Update(
            created.Id,
            "Renamed",
            "second take",
            new[] { "New first", "New second" },
            new EncoderOptions { TargetBitrate = 5000 });

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Renamed");
        updated.Description.Should().Be("second take");
        updated.OutcomeBullets.Should().BeEquivalentTo(new[] { "New first", "New second" });
        updated.Options.TargetBitrate.Should().Be(5000);
    }

    // =====================================================================
    //  Machine-local fields (OutputDirectory, EncodeDirectory)
    //
    //  A policy is a portable bundle - shareable with another install via
    //  .snackspolicy.json export. Filesystem paths from the author's machine
    //  ("/mnt/myraid/output") are meaningless on the importing machine and
    //  must NEVER ride along inside the policy. The rule is enforced on
    //  Create, Update, Import, and defensively on load. Apply preserves the
    //  user's existing paths so switching policies never silently nukes them.
    // =====================================================================

    [Fact]
    public void Builtins_carry_no_filesystem_paths()
    {
        using var fx = new PolicyTestFixture();
        var policies = fx.Service.List();

        policies.Should().OnlyContain(p => p.Options.OutputDirectory == null,
            "shipping a built-in with an author-machine path would break imports on every other install");
        policies.Should().OnlyContain(p => p.Options.EncodeDirectory == null);
    }

    [Fact]
    public void Create_strips_machine_local_filesystem_paths()
    {
        using var fx = new PolicyTestFixture();

        var withPaths = new EncoderOptions
        {
            TargetBitrate   = 4000,
            OutputDirectory = "/mnt/myraid/encoded",
            EncodeDirectory = "/tmp/snacks-scratch",
        };

        var created = fx.Service.Create("Should-strip", null, null, withPaths);

        created.Options.OutputDirectory.Should().BeNull("OutputDirectory is machine-local; it never belongs in a portable policy");
        created.Options.EncodeDirectory.Should().BeNull("EncodeDirectory is machine-local; it never belongs in a portable policy");
        created.Options.TargetBitrate.Should().Be(4000, "non-machine-local fields must survive the strip");
    }

    [Fact]
    public void Update_strips_machine_local_filesystem_paths()
    {
        using var fx = new PolicyTestFixture();
        var created = fx.Service.Create("To update", null, null, new EncoderOptions());

        var withPaths = new EncoderOptions
        {
            TargetBitrate   = 5000,
            OutputDirectory = "/mnt/some/path",
            EncodeDirectory = "/tmp/scratch",
        };

        var updated = fx.Service.Update(created.Id, "Renamed", null, null, withPaths);

        updated!.Options.OutputDirectory.Should().BeNull();
        updated.Options.EncodeDirectory.Should().BeNull();
        updated.Options.TargetBitrate.Should().Be(5000);
    }

    [Fact]
    public void Import_strips_machine_local_filesystem_paths()
    {
        using var fx = new PolicyTestFixture();
        fx.Service.List();

        var doc = new PolicyDocument
        {
            SchemaVersion = PolicyDocument.CurrentSchemaVersion,
            Policies = new()
            {
                new Policy
                {
                    Name    = "From a friend",
                    Options = new EncoderOptions
                    {
                        Codec           = "h265",
                        OutputDirectory = "/home/friend/library/encoded",
                        EncodeDirectory = "/home/friend/scratch",
                    },
                },
            },
        };

        var added = fx.Service.Import(doc);

        added[0].Options.OutputDirectory.Should().BeNull("the importer's local paths must not be silently overwritten by the exporter's");
        added[0].Options.EncodeDirectory.Should().BeNull();
        added[0].Options.Codec.Should().Be("h265", "encoding fields still come across");
    }

    /// <summary>
    ///     Even if a policy slipped through the strip on Create/Update/Import (older
    ///     Snacks version that didn't yet enforce the rule), the load path should still
    ///     scrub paths so they never resurface in the picker. Test by writing a raw
    ///     PolicyDocument directly to disk with paths set, then loading via the service.
    /// </summary>
    [Fact]
    public void LoadAndSeed_scrubs_paths_from_legacy_on_disk_policies()
    {
        using var fx = new PolicyTestFixture();

        var legacy = new PolicyDocument
        {
            SchemaVersion = PolicyDocument.CurrentSchemaVersion,
            Policies = new()
            {
                new Policy
                {
                    Id      = "policy-legacy-001",
                    Name    = "Pre-rule custom",
                    BuiltIn = false,
                    Options = new EncoderOptions
                    {
                        OutputDirectory = "/legacy/output",
                        EncodeDirectory = "/legacy/scratch",
                    },
                },
            },
        };

        var path = Path.Combine(fx.WorkDir, "config", "policies.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(legacy));

        var loaded = fx.Service.Get("policy-legacy-001");
        loaded.Should().NotBeNull();
        loaded!.Options.OutputDirectory.Should().BeNull();
        loaded.Options.EncodeDirectory.Should().BeNull();
    }

    [Fact]
    public void GetActive_ignores_filesystem_path_drift()
    {
        // The user applied Plex-Safe HEVC, then set their OutputDirectory on the General
        // tab. That's NOT a drift of the policy - it's a machine-local choice. The
        // Active card should still read "Now using Plex-Safe HEVC" with no modified marker.
        using var fx = new PolicyTestFixture();
        var plexSafe = fx.Service.Get("builtin-plex-safe-hevc")!;
        fx.Service.SetLastAppliedPolicyId(plexSafe.Id);

        var currentSettings = plexSafe.Options.Clone();
        currentSettings.OutputDirectory = "/mnt/user/library/encoded";
        currentSettings.EncodeDirectory = "/var/tmp/snacks";

        var active = fx.Service.GetActive(currentSettings);

        active.Id.Should().Be("builtin-plex-safe-hevc");
        active.Modified.Should().BeFalse(
            "OutputDirectory/EncodeDirectory are machine-local; setting them is not a drift of the encoding policy");
    }

    // -- EncoderOptions helpers -----------------------------------------------

    [Fact]
    public void ClearMachineLocalFields_only_clears_paths()
    {
        var opts = new EncoderOptions
        {
            Codec           = "h265",
            TargetBitrate   = 4200,
            OutputDirectory = "/x",
            EncodeDirectory = "/y",
        };

        opts.ClearMachineLocalFields();

        opts.OutputDirectory.Should().BeNull();
        opts.EncodeDirectory.Should().BeNull();
        opts.Codec.Should().Be("h265",         "non-path fields must not be touched");
        opts.TargetBitrate.Should().Be(4200);
    }

    [Fact]
    public void MergeMachineLocalFrom_overwrites_paths_and_leaves_everything_else()
    {
        var target = new EncoderOptions
        {
            Codec           = "h265",      // policy's choice
            TargetBitrate   = 6000,        // policy's choice
            OutputDirectory = null,        // policy doesn't carry paths
            EncodeDirectory = null,
        };
        var source = new EncoderOptions
        {
            Codec           = "h264",                  // wrong codec - must NOT leak
            TargetBitrate   = 1500,                    // wrong bitrate - must NOT leak
            OutputDirectory = "/mnt/user/encoded",     // SHOULD copy
            EncodeDirectory = "/tmp/snacks-scratch",   // SHOULD copy
        };

        target.MergeMachineLocalFrom(source);

        target.OutputDirectory.Should().Be("/mnt/user/encoded");
        target.EncodeDirectory.Should().Be("/tmp/snacks-scratch");
        target.Codec.Should().Be("h265",          "encoding decisions still come from the policy, not the source");
        target.TargetBitrate.Should().Be(6000);
    }

    // =====================================================================
    //  Duplicate (keeps the description + bullets from the source)
    // =====================================================================

    /// <summary>
    ///     When the user duplicates a built-in to customize it, the new policy must
    ///     start with the source's description and outcome bullets, not just the
    ///     options. Otherwise "Duplicate, then tweak" produces a half-empty card.
    /// </summary>
    [Fact]
    public void Duplicate_carries_description_and_outcome_bullets()
    {
        using var fx = new PolicyTestFixture();
        fx.Service.List();

        var dup = fx.Service.Duplicate("builtin-plex-safe-hevc");

        dup.Should().NotBeNull();
        dup!.Description.Should().NotBeNullOrWhiteSpace("description is part of the visible card; a dup that loses it looks broken");
        dup.OutcomeBullets.Should().NotBeEmpty("outcome bullets are the primary explanation; a dup must inherit them");
        dup.OutcomeBullets.Should().BeEquivalentTo(new[]
        {
            "Files end up roughly half the size",
            "Plays on phones, TVs, browsers",
            "Keeps the original soundtrack",
            "Keeps subtitles as-is",
            "Uses your graphics card when available",
        });
    }

    [Fact]
    public void Duplicate_clones_a_built_in_into_a_custom_policy()
    {
        using var fx = new PolicyTestFixture();
        fx.Service.List();

        var dup = fx.Service.Duplicate("builtin-plex-safe-hevc");

        dup.Should().NotBeNull();
        dup!.BuiltIn.Should().BeFalse();
        dup.Id.Should().NotBe("builtin-plex-safe-hevc", "the duplicate is a new policy, not a reference to the source");
        dup.Name.Should().Be("Plex-Safe HEVC (custom)");
        dup.Options.TargetBitrate.Should().Be(3500, "duplicates inherit the source's options");
    }

    [Fact]
    public void Import_strips_built_in_flag_from_payload()
    {
        using var fx = new PolicyTestFixture();
        fx.Service.List();

        var doc = new PolicyDocument
        {
            SchemaVersion = PolicyDocument.CurrentSchemaVersion,
            Policies = new()
            {
                new Policy
                {
                    Id      = "builtin-malicious",
                    Name    = "Smuggled built-in",
                    BuiltIn = true,
                    Options = new EncoderOptions(),
                },
            },
        };

        var added = fx.Service.Import(doc);

        added.Should().HaveCount(1);
        added[0].BuiltIn.Should().BeFalse("imports always land as custom policies");
        added[0].Id.Should().NotBe("builtin-malicious", "imports get a freshly-assigned id");
        added[0].Id.Should().StartWith("policy-");
    }

    [Fact]
    public void Import_renames_on_name_collision_with_existing_built_in()
    {
        using var fx = new PolicyTestFixture();
        fx.Service.List();

        var doc = new PolicyDocument
        {
            SchemaVersion = PolicyDocument.CurrentSchemaVersion,
            Policies = new()
            {
                new Policy { Name = "Plex-Safe HEVC", Options = new EncoderOptions() },
            },
        };

        var added = fx.Service.Import(doc);

        added[0].Name.Should().Be("Plex-Safe HEVC (imported)",
            "the (imported) suffix marks the user's copy and disambiguates from the built-in");
    }

    [Fact]
    public void Import_rejects_unknown_schema_version()
    {
        using var fx = new PolicyTestFixture();

        var doc = new PolicyDocument
        {
            SchemaVersion = PolicyDocument.CurrentSchemaVersion + 1,
            Policies      = new() { new Policy { Name = "Future", Options = new EncoderOptions() } },
        };

        var act = () => fx.Service.Import(doc);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*targets policy schema*", "users need to know to upgrade Snacks rather than land on a half-imported file");
    }

    [Fact]
    public void Import_rejects_missing_schema_version()
    {
        using var fx = new PolicyTestFixture();

        var doc = new PolicyDocument
        {
            SchemaVersion = 0,
            Policies      = new() { new Policy { Name = "Vintage", Options = new EncoderOptions() } },
        };

        var act = () => fx.Service.Import(doc);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*missing a schema version*");
    }

    [Fact]
    public void Export_one_returns_a_single_entry_document()
    {
        using var fx = new PolicyTestFixture();
        var created = fx.Service.Create("Exportable", "desc", null, new EncoderOptions { TargetBitrate = 2222 });

        var doc = fx.Service.ExportOne(created.Id);

        doc.Should().NotBeNull();
        doc!.SchemaVersion.Should().Be(PolicyDocument.CurrentSchemaVersion);
        doc.Policies.Should().HaveCount(1);
        doc.Policies[0].Name.Should().Be("Exportable");
        doc.Policies[0].Options.TargetBitrate.Should().Be(2222);
    }

    [Fact]
    public void GetActive_returns_pinned_policy_when_settings_match_exactly()
    {
        using var fx = new PolicyTestFixture();
        var plexSafe = fx.Service.Get("builtin-plex-safe-hevc")!;
        fx.Service.SetLastAppliedPolicyId(plexSafe.Id);

        var active = fx.Service.GetActive(plexSafe.Options.Clone());

        active.Id.Should().Be("builtin-plex-safe-hevc");
        active.Name.Should().Be("Plex-Safe HEVC");
        active.Modified.Should().BeFalse("settings exactly equal the policy's options");
    }

    [Fact]
    public void GetActive_marks_modified_when_settings_drift_from_pinned_policy()
    {
        using var fx = new PolicyTestFixture();
        var plexSafe = fx.Service.Get("builtin-plex-safe-hevc")!;
        fx.Service.SetLastAppliedPolicyId(plexSafe.Id);

        var drifted = plexSafe.Options.Clone();
        drifted.TargetBitrate = plexSafe.Options.TargetBitrate + 500;

        var active = fx.Service.GetActive(drifted);

        active.Id.Should().Be("builtin-plex-safe-hevc", "the pinned policy is still the user's stated intent");
        active.Modified.Should().BeTrue("the bitrate change must surface as a drift marker");
    }

    [Fact]
    public void GetActive_falls_back_to_exact_match_when_nothing_pinned()
    {
        using var fx = new PolicyTestFixture();
        // No SetLastAppliedPolicyId call — simulates a fresh install where the user
        // never explicitly applied a policy but their settings happen to match one.
        var archive = fx.Service.Get("builtin-archive-master")!;

        var active = fx.Service.GetActive(archive.Options.Clone());

        active.Id.Should().Be("builtin-archive-master");
        active.Modified.Should().BeFalse();
    }

    [Fact]
    public void GetActive_returns_Custom_when_settings_match_nothing()
    {
        using var fx = new PolicyTestFixture();
        fx.Service.List();

        // A weird mix nothing in the seed list matches.
        var wild = new EncoderOptions
        {
            Codec               = "h265",
            TargetBitrate       = 99999,
            DownscalePolicy     = "Never",
        };

        var active = fx.Service.GetActive(wild);

        active.Id.Should().BeNull();
        active.Name.Should().Be("Custom");
        active.Modified.Should().BeFalse("Custom is the default state, not a drift state");
    }

    [Fact]
    public void Deleting_active_custom_clears_LastAppliedPolicyId()
    {
        using var fx = new PolicyTestFixture();
        var custom = fx.Service.Create("Mine", null, null, new EncoderOptions());
        fx.Service.SetLastAppliedPolicyId(custom.Id);

        fx.Service.Delete(custom.Id);

        fx.Service.GetLastAppliedPolicyId().Should().BeNull(
            "the active pin must clear when its target is removed, otherwise GetActive points at a missing policy");
    }

    [Fact]
    public void ExportAllCustom_excludes_built_ins()
    {
        using var fx = new PolicyTestFixture();
        fx.Service.List();
        fx.Service.Create("Mine", null, null, new EncoderOptions());

        var doc = fx.Service.ExportAllCustom();

        doc.Policies.Should().HaveCount(1, "built-ins are excluded from the all-custom export");
        doc.Policies[0].Name.Should().Be("Mine");
        doc.Policies[0].BuiltIn.Should().BeFalse();
    }

    /// <summary>
    ///     Isolates each test in its own working directory via the <c>SNACKS_WORK_DIR</c>
    ///     environment variable that <see cref="FileService.GetWorkingDirectory"/> honors.
    ///     Cleans up the temp tree on dispose.
    /// </summary>
    private sealed class PolicyTestFixture : IDisposable
    {
        public string         WorkDir { get; }
        public PolicyService  Service { get; }

        private readonly string? _previousWorkDir;

        public PolicyTestFixture()
        {
            WorkDir = Path.Combine(Path.GetTempPath(), "snacks-policy-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkDir);

            _previousWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
            Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", WorkDir);

            var fileService   = new FileService();
            var configFiles   = new ConfigFileService(fileService);
            Service           = new PolicyService(configFiles);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _previousWorkDir);
            try { Directory.Delete(WorkDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
