using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAgentPipelines.Controllers;

public sealed class RunsController(RunManager runs) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await runs.ListAsync(100, cancellationToken));

    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(id, cancellationToken);
        return run is null ? NotFound() : View(run.Snapshot());
    }

    /// <summary>
    /// Server-sent events for a run, after <paramref name="after"/> (or the browser's Last-Event-ID on reconnect).
    /// Ends with an "end" event once the run has finished.
    /// </summary>
    [HttpGet]
    public async Task<IResult> Events(string id, long after, CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(id, cancellationToken);
        if (run is null) return Results.NotFound();

        if (long.TryParse(Request.Headers["Last-Event-ID"], out var lastEventId)) after = lastEventId;
        return TypedResults.ServerSentEvents(Stream(run, after, cancellationToken));
    }

    [HttpPost]
    public IActionResult Cancel(string id)
    {
        runs.Cancel(id);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public IActionResult Delete(string id)
    {
        if (runs.Delete(id))
        {
            TempData["Message"] = "Run deleted.";
        }
        else
        {
            TempData["Error"] = "A running pipeline cannot be deleted - cancel it first.";
        }
        return RedirectToAction(nameof(Index));
    }

    private static async IAsyncEnumerable<SseItem<RunEvent?>> Stream(PipelineRun run, long after,
                                                                    [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var (events, finished) = run.GetEventsAfter(after);
            foreach (var evt in events)
            {
                after = evt.Seq;
                yield return new SseItem<RunEvent?>(evt) { EventId = evt.Seq.ToString() };
            }
            if (finished)
            {
                // Tells the page to close the EventSource instead of reconnecting.
                yield return new SseItem<RunEvent?>(null, "end");
                yield break;
            }

            try
            {
                await run.WaitForChangeAsync(after, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }
}
