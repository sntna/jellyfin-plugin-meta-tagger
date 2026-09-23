using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("MetaTagger")]
public sealed class MetaTaggerDashboardController : ControllerBase
{
    private readonly MetaTaggerStateStore _stateStore;
    private readonly Func<PluginConfiguration?> _getConfiguration;
    private readonly MetaTaggerRunner? _runner;

    public MetaTaggerDashboardController(MetaTaggerStateStore stateStore, MetaTaggerRunner runner)
        : this(stateStore, () => Plugin.Instance?.Configuration, runner)
    {
    }

    internal MetaTaggerDashboardController(
        MetaTaggerStateStore stateStore,
        Func<PluginConfiguration?> getConfiguration,
        MetaTaggerRunner? runner = null)
    {
        _stateStore = stateStore;
        _getConfiguration = getConfiguration;
        _runner = runner;
    }

    [HttpGet("ConfigurationRevision")]
    public ActionResult<string> GetConfigurationRevision()
        => new JsonResult(_getConfiguration()?.ConfigurationRevision);

    [HttpGet("Runs")]
    public Task<IReadOnlyList<MetaTaggerRunRecord>> GetRunsAsync(CancellationToken cancellationToken)
        => _stateStore.LoadRunsAsync(cancellationToken);

    [HttpGet("Runs/{runId:guid}")]
    public async Task<ActionResult<MetaTaggerRunRecord>> GetRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var record = await _stateStore.LoadRunAsync(runId, cancellationToken).ConfigureAwait(false);
        return record is null ? NotFound() : record;
    }

    [HttpGet("Libraries")]
    public IReadOnlyList<MetaTaggerLibrary> GetLibraries() => Runner.GetLibraries();

    [HttpGet("Items")]
    public ActionResult<MetaTaggerItemPage> BrowseItems(
        [FromQuery] string? searchTerm, [FromQuery] Guid? libraryId,
        [FromQuery] int startIndex = 0, [FromQuery] int limit = 25,
        [FromQuery] Guid? parentId = null, [FromQuery] bool hierarchical = false)
    {
        try { return Runner.BrowseItems(searchTerm, libraryId, startIndex, limit, parentId, hierarchical); }
        catch (ArgumentException exception) { return BadRequest(new ProblemDetails { Title = exception.Message }); }
    }

    [HttpPost("Example")]
    public Task<MetaTaggerItemInspection> GetExampleAsync(
        [FromBody] MetaTaggerExampleRequest request,
        CancellationToken cancellationToken)
    {
        return Runner.InspectItemAsync(request.ItemId, request.Configuration, cancellationToken);
    }

    [HttpGet("Items/{itemId:guid}")]
    public Task<MetaTaggerItemInspection> InspectItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        return Runner.InspectItemAsync(itemId, null, cancellationToken);
    }

    [HttpPost("Items/{itemId:guid}/Preview")]
    public Task<MetaTaggerItemInspection> PreviewItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        return Runner.PreviewItemAsync(itemId, cancellationToken);
    }

    [HttpPost("Items/{itemId:guid}/Apply")]
    public async Task<ActionResult<MetaTaggerRunSummary>> ApplyItemAsync(
        Guid itemId, [FromBody] MetaTaggerItemApplyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await Runner.ApplyItemAsync(itemId, request.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message, Status = StatusCodes.Status409Conflict });
        }
    }

    [HttpGet("Cleanup/Items")]
    [ProducesResponseType<IReadOnlyCollection<MetaTaggerCleanupItem>>(StatusCodes.Status200OK)]
    public IReadOnlyCollection<MetaTaggerCleanupItem> SearchCleanupItems([FromQuery] string searchTerm)
    {
        return Runner.SearchCleanupItems(searchTerm);
    }

    [HttpPost("Cleanup/Preview")]
    [ProducesResponseType<MetaTaggerCleanupPreview>(StatusCodes.Status200OK)]
    public Task<MetaTaggerCleanupPreview> PreviewCleanupAsync(
        [FromBody] MetaTaggerCleanupRequest request,
        CancellationToken cancellationToken)
    {
        return Runner.PreviewCleanupAsync(request.ItemId, new Progress<double>(), cancellationToken);
    }

    [HttpPost("Cleanup/Apply")]
    [ProducesResponseType<MetaTaggerRunSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MetaTaggerRunSummary>> ApplyCleanupAsync(
        [FromBody] MetaTaggerCleanupApplyRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Runner.ApplyCleanupAsync(request.Token, new Progress<double>(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message, Status = StatusCodes.Status409Conflict });
        }
    }

    private MetaTaggerRunner Runner => _runner
        ?? throw new InvalidOperationException("Meta Tagger runner is unavailable.");

    [HttpGet("Preview")]
    [ProducesResponseType<MetaTaggerDashboardPreview>(StatusCodes.Status200OK)]
    public async Task<MetaTaggerDashboardPreview> GetLatestPreviewAsync(CancellationToken cancellationToken)
    {
        var summary = await _stateStore.LoadSummaryAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyCollection<MetaTaggerPreviewChange> changes = [];
        var status = "NoRuns";
        if (summary is not null)
        {
            if (!summary.PreviewOnly)
            {
                status = "LatestRunApplied";
            }
            else if (string.IsNullOrWhiteSpace(summary.ConfigurationRevision)
                || !string.Equals(
                    summary.ConfigurationRevision,
                    _getConfiguration()?.ConfigurationRevision,
                    StringComparison.Ordinal))
            {
                status = "Unavailable";
            }
            else if (summary.PreviewChangesExported <= 0
                || string.IsNullOrWhiteSpace(summary.PreviewChangesExportPath))
            {
                status = "NoChanges";
            }
            else
            {
                changes = await _stateStore.LoadPreviewChangesAsync(cancellationToken).ConfigureAwait(false);
                status = changes.Count == summary.PreviewChangesExported
                    && string.Equals(
                        summary.PreviewChangesChecksum,
                        MetaTaggerPreviewChangeChecksum.Compute(changes),
                        StringComparison.Ordinal)
                    ? "Ready"
                    : "Unavailable";
                if (status == "Unavailable")
                {
                    changes = [];
                }
            }
        }

        return new MetaTaggerDashboardPreview
        {
            Status = status,
            Summary = summary,
            Changes = changes
        };
    }
}
