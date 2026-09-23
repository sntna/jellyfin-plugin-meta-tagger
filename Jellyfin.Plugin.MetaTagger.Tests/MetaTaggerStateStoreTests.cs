using Jellyfin.Plugin.MetaTagger;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetaTaggerStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunHistory_LegacyCleanupIsNotInventedAsConfiguredGenerationPreview()
    {
        var store = new MetaTaggerStateStore(_directory);
        await store.SaveSummaryAsync(new MetaTaggerRunSummary { RunMode = "ClearGeneratedTags", PreviewOnly = true }, CancellationToken.None);
        var run = Assert.Single(await store.LoadRunsAsync(CancellationToken.None));
        Assert.Equal("Cleanup preview", run.Operation);
        Assert.Equal("Scope unavailable in legacy summary", run.Scope);
        Assert.False(run.DetailsAvailable);
    }

    [Fact]
    public async Task RunHistory_BoundsRetentionAndDoesNotConfuseMissingDetailsWithAnotherRun()
    {
        var store = new MetaTaggerStateStore(_directory);
        var first = new MetaTaggerRunRecord { Outcome = "Completed" };
        await store.SaveRunAsync(first, CancellationToken.None);
        for (var i = 0; i < 20; i++)
        {
            await store.SaveRunAsync(new MetaTaggerRunRecord { Outcome = "Completed" }, CancellationToken.None);
        }
        Assert.Equal(20, (await store.LoadRunsAsync(CancellationToken.None)).Count);
        Assert.Null(await store.LoadRunAsync(first.RunId, CancellationToken.None));
        var large = new MetaTaggerRunRecord { Outcome = "Completed" };
        for (var i = 0; i < 1001; i++) { large.AddItem(new MetaTaggerRunItem { ItemId = i.ToString() }); }
        await store.SaveRunAsync(large, CancellationToken.None);
        var detail = await store.LoadRunAsync(large.RunId, CancellationToken.None);
        Assert.Equal(1000, detail!.Items.Count);
        Assert.True(detail.DetailsTruncated);
        await File.WriteAllTextAsync(Path.Combine(_directory, "runs", large.RunId.ToString("N") + ".json"), "corrupt");
        detail = await store.LoadRunAsync(large.RunId, CancellationToken.None);
        Assert.False(detail!.DetailsAvailable);
        Assert.Empty(detail.Items);
    }

    [Fact]
    public async Task RunHistory_UnfinishedRunIsInterruptedAfterStoreRestartAndOversizedDetailsAreExplicit()
    {
        var store = new MetaTaggerStateStore(_directory);
        var run = new MetaTaggerRunRecord();
        run.AddItem(new MetaTaggerRunItem { Name = new string('x', 5 * 1024 * 1024 + 1) });
        await store.SaveRunAsync(run, CancellationToken.None);
        var reopened = new MetaTaggerStateStore(_directory);
        var loaded = await reopened.LoadRunAsync(run.RunId, CancellationToken.None);
        Assert.Equal("Interrupted", loaded!.Outcome);
        Assert.True(loaded.DetailsTruncated);
        Assert.Empty(loaded.Items);
    }

    [Fact]
    public async Task RunHistory_TwoRunsRemainSeparatelyReadableAfterReopeningStore()
    {
        var store = new MetaTaggerStateStore(_directory);
        var first = new MetaTaggerRunRecord { Operation = "Preview", Outcome = "Completed" };
        first.AddItem(new MetaTaggerRunItem { ItemId = "dune", Name = "Dune", Outcome = "Changes", AddedTags = ["meta:year:2024"] });
        var second = new MetaTaggerRunRecord { Operation = "Apply", Outcome = "Completed" };
        await store.SaveRunAsync(first, CancellationToken.None);
        await store.SaveRunAsync(second, CancellationToken.None);
        var reopened = new MetaTaggerStateStore(_directory);
        var history = await reopened.LoadRunsAsync(CancellationToken.None);
        Assert.Equal([second.RunId, first.RunId], history.Select(run => run.RunId));
        Assert.Equal(["meta:year:2024"], Assert.Single((await reopened.LoadRunAsync(first.RunId, CancellationToken.None))!.Items).AddedTags);
        Assert.Empty((await reopened.LoadRunAsync(second.RunId, CancellationToken.None))!.Items);
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyStateWhenLedgerDoesNotExist()
    {
        var store = new MetaTaggerStateStore(_directory);

        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Empty(state.Items);
    }

    [Fact]
    public async Task LoadAsync_WhenPrimaryIsMissing_RecoversExistingBackup()
    {
        var store = new MetaTaggerStateStore(_directory);
        var primaryPath = await CreateBackupAndCorruptPrimaryAsync(store);
        File.Delete(primaryPath);

        var recovered = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(["item-1"], recovered.Items.Keys);
        Assert.Equal(["meta:genre:first"], recovered.Items["item-1"].LastAppliedTags);
    }

    [Fact]
    public async Task LoadAsync_WhenPrimaryIsNull_RecoversAndPreservesExistingBackup()
    {
        var store = new MetaTaggerStateStore(_directory);
        var primaryPath = await CreateBackupAndCorruptPrimaryAsync(store);
        await File.WriteAllTextAsync(primaryPath, "null");

        var recovered = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(["item-1"], recovered.Items.Keys);
        await store.SaveAsync(recovered, CancellationToken.None);
        await File.WriteAllTextAsync(primaryPath, "null");
        var recoveredAgain = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["meta:genre:first"], recoveredAgain.Items["item-1"].LastAppliedTags);
    }

    [Fact]
    public async Task LoadAsync_WhenPrimaryIsNullWithoutBackup_RejectsTheLedger()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "meta-tagger-state.json"), "null");

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => new MetaTaggerStateStore(_directory).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_LegacyStateWithoutRunCursors_UsesEmptyCursorMap()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "meta-tagger-state.json"),
            """
            {
              "schemaVersion": 1,
              "pluginVersion": "0.1.0",
              "items": {}
            }
            """);

        var state = await new MetaTaggerStateStore(_directory).LoadAsync(CancellationToken.None);

        Assert.Empty(state.RunCursors);
    }

    [Fact]
    public async Task LoadAsync_NormalizesCursorKeysAndPendingIds()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "meta-tagger-state.json"),
            """
            {
              "schemaVersion": 1,
              "pluginVersion": "0.1.0",
              "items": {},
              "runCursors": {
                " profile ": {
                  "pendingItemIds": [" item-1 ", "ITEM-1", "", "item-2"],
                  "nextIndex": 3
                },
                "empty": {
                  "pendingItemIds": null,
                  "nextIndex": 99
                },
                "   ": {
                  "pendingItemIds": ["ignored"]
                }
              }
            }
            """);

        var state = await new MetaTaggerStateStore(_directory).LoadAsync(CancellationToken.None);

        Assert.Equal(2, state.RunCursors.Count);
        Assert.Equal(["item-1", "item-2"], state.RunCursors["profile"].PendingItemIds);
        Assert.Equal(1, state.RunCursors["profile"].NextIndex);
        Assert.Empty(state.RunCursors["empty"].PendingItemIds);
        Assert.Equal(0, state.RunCursors["empty"].NextIndex);
    }

    [Fact]
    public async Task SaveAsync_PersistsLedgerEntries()
    {
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState
        {
            Items =
            {
                ["item-1"] = new MetaTaggerStateItem
                {
                    ItemId = "item-1",
                    ItemPath = "/media/item.mkv",
                    LastMetadataFingerprint = "sha256:test",
                    LastGeneratedTags = ["meta:genre:animation"],
                    LastAppliedTags = ["meta:genre:animation"]
                }
            }
        };

        await store.SaveAsync(state, CancellationToken.None);

        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["item-1"], reloaded.Items.Keys);
        Assert.Equal(["meta:genre:animation"], reloaded.Items["item-1"].LastAppliedTags);
    }

    [Fact]
    public async Task SaveAsync_PersistsRunCursorPosition()
    {
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState
        {
            RunCursors =
            {
                ["profile"] = new MetaTaggerRunCursor
                {
                    PendingItemIds = ["item-1", "item-2", "item-3"],
                    NextIndex = 2
                }
            }
        };

        await store.SaveAsync(state, CancellationToken.None);

        var cursor = Assert.Single((await store.LoadAsync(CancellationToken.None)).RunCursors).Value;
        Assert.Equal(["item-1", "item-2", "item-3"], cursor.PendingItemIds);
        Assert.Equal(2, cursor.NextIndex);
    }

    [Fact]
    public async Task SaveAsync_WithValidPrimary_RotatesPreviousPrimaryIntoRecoverableBackup()
    {
        var store = new MetaTaggerStateStore(_directory);
        await CreateBackupAndCorruptPrimaryAsync(store);

        var recovered = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(["item-1"], recovered.Items.Keys);
        Assert.Equal(["meta:genre:first"], recovered.Items["item-1"].LastAppliedTags);
    }

    [Fact]
    public async Task SaveAsync_AfterBackupRecovery_PreservesRecoverableBackup()
    {
        var store = new MetaTaggerStateStore(_directory);
        var primaryPath = await CreateBackupAndCorruptPrimaryAsync(store);

        var recovered = await store.LoadAsync(CancellationToken.None);
        await store.SaveAsync(recovered, CancellationToken.None);
        await File.WriteAllTextAsync(primaryPath, "{bad json again");

        var recoveredAgain = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["item-1"], recoveredAgain.Items.Keys);
        Assert.Equal(["meta:genre:first"], recoveredAgain.Items["item-1"].LastAppliedTags);
    }

    [Fact]
    public async Task SaveAsync_WhenRepairIsCanceled_PreservesBackupForLaterRepair()
    {
        var store = new MetaTaggerStateStore(_directory);
        var primaryPath = await CreateBackupAndCorruptPrimaryAsync(store);
        var recovered = await store.LoadAsync(CancellationToken.None);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(recovered, cancellationSource.Token));

        var recoveredAfterCancellation = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["item-1"], recoveredAfterCancellation.Items.Keys);

        await store.SaveAsync(recoveredAfterCancellation, CancellationToken.None);
        await File.WriteAllTextAsync(primaryPath, "{bad json again");

        var recoveredAfterRepair = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["item-1"], recoveredAfterRepair.Items.Keys);
        Assert.Equal(["meta:genre:first"], recoveredAfterRepair.Items["item-1"].LastAppliedTags);
    }

    [Fact]
    public async Task SaveAsync_AfterSuccessfulRepair_ResumesNormalBackupRotation()
    {
        var store = new MetaTaggerStateStore(_directory);
        var primaryPath = await CreateBackupAndCorruptPrimaryAsync(store);

        var recovered = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["item-1"], recovered.Items.Keys);
        await store.SaveAsync(CreateState("item-repaired", "repaired"), CancellationToken.None);
        await store.SaveAsync(CreateState("item-later", "later"), CancellationToken.None);
        await File.WriteAllTextAsync(primaryPath, "{bad json again");

        var rotatedBackup = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["item-repaired"], rotatedBackup.Items.Keys);
        Assert.Equal(["meta:genre:repaired"], rotatedBackup.Items["item-repaired"].LastAppliedTags);
    }

    [Fact]
    public async Task SaveAsync_WhenRecoveredRepairPromotionFails_PreservesBackupAndRecoveryProvenance()
    {
        var promoter = new FailOnceMetaTaggerStateFilePromoter();
        var store = new MetaTaggerStateStore(_directory, promoter);
        var primaryPath = await CreateBackupAndCorruptPrimaryAsync(store);
        var recovered = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["item-1"], recovered.Items.Keys);
        promoter.FailNextPromotion();

        await Assert.ThrowsAsync<IOException>(
            () => store.SaveAsync(CreateState("item-repaired", "repaired"), CancellationToken.None));

        var afterFailedRepair = await new MetaTaggerStateStore(_directory)
            .LoadAsync(CancellationToken.None);
        Assert.Equal(["item-1"], afterFailedRepair.Items.Keys);
        Assert.Equal(["meta:genre:first"], afterFailedRepair.Items["item-1"].LastAppliedTags);

        await store.SaveAsync(CreateState("item-repaired", "repaired"), CancellationToken.None);
        var repaired = await new MetaTaggerStateStore(_directory)
            .LoadAsync(CancellationToken.None);
        Assert.Equal(["item-repaired"], repaired.Items.Keys);
        await File.WriteAllTextAsync(primaryPath, "{bad json again");

        var recoveredAfterRepair = await new MetaTaggerStateStore(_directory)
            .LoadAsync(CancellationToken.None);
        Assert.Equal(["item-1"], recoveredAfterRepair.Items.Keys);
        Assert.Equal(["meta:genre:first"], recoveredAfterRepair.Items["item-1"].LastAppliedTags);
    }

    [Fact]
    public async Task SaveSummaryAsync_PersistsLastRunSummary()
    {
        var store = new MetaTaggerStateStore(_directory);
        var summary = new MetaTaggerRunSummary
        {
            ItemsScanned = 2,
            ItemsChanged = 1,
            TagsAdded = 3,
            PreviewOnly = true
        };

        await store.SaveSummaryAsync(summary, CancellationToken.None);

        var json = await File.ReadAllTextAsync(Path.Combine(_directory, "last-run-summary.json"));
        Assert.Contains("\"itemsScanned\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"previewOnly\": true", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadSummaryAsync_ReturnsSavedLastRunSummary()
    {
        var store = new MetaTaggerStateStore(_directory);
        var summary = new MetaTaggerRunSummary
        {
            ItemsScanned = 7,
            WritesApplied = 2,
            BudgetLimitReached = true,
            BudgetLimitReason = "max-writes"
        };

        await store.SaveSummaryAsync(summary, CancellationToken.None);

        var reloaded = await store.LoadSummaryAsync(CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(7, reloaded.ItemsScanned);
        Assert.Equal(2, reloaded.WritesApplied);
        Assert.True(reloaded.BudgetLimitReached);
        Assert.Equal("max-writes", reloaded.BudgetLimitReason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static MetaTaggerState CreateState(string itemId, string value)
    {
        return new MetaTaggerState
        {
            Items =
            {
                [itemId] = new MetaTaggerStateItem
                {
                    ItemId = itemId,
                    LastMetadataFingerprint = $"sha256:{value}",
                    LastAppliedTags = [$"meta:genre:{value}"]
                }
            }
        };
    }

    private async Task<string> CreateBackupAndCorruptPrimaryAsync(MetaTaggerStateStore store)
    {
        await store.SaveAsync(CreateState("item-1", "first"), CancellationToken.None);
        await store.SaveAsync(CreateState("item-2", "second"), CancellationToken.None);
        var primaryPath = Path.Combine(_directory, "meta-tagger-state.json");
        await File.WriteAllTextAsync(primaryPath, "{bad json");
        return primaryPath;
    }

    private sealed class FailOnceMetaTaggerStateFilePromoter : IMetaTaggerStateFilePromoter
    {
        private bool _failNextPromotion;

        public void FailNextPromotion()
        {
            _failNextPromotion = true;
        }

        public void Promote(string tempPath, string destinationPath)
        {
            if (_failNextPromotion)
            {
                _failNextPromotion = false;
                throw new IOException("Injected state-file promotion failure.");
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
    }
}
