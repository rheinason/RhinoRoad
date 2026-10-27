using System.Diagnostics;
using Rhino;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// A preview computed off the display thread when it is too slow to compute inside a frame, with
/// the newest request winning.
/// </summary>
/// <remarks>
/// <para>
/// A pick's dynamic draw runs on every mouse move, and planning there blocks the cursor. Most legs
/// plan in a few milliseconds and are computed in the frame, so the preview is exact and never a
/// frame behind. A docking reverse tries several correction strengths and a long route's sweep grows
/// with it; those go to a background task, and the frame draws the last completed preview, faded,
/// until the newer one lands and redraws the view, as LiveJourneyEditConduit does for an edit.
/// </para>
/// <para>
/// One task runs at a time. A request arriving while it runs is not queued: when the task finishes
/// the view redraws, the next frame asks for whatever the cursor is on by then, and anything moved
/// past in between is never computed at all. A request is the same request when it is equal, so the
/// key should be the whole input the result depends on.
/// </para>
/// <para>
/// Whether a request is slow is predicted by the caller where it can be known, and otherwise learned:
/// a computation that overran the frame budget sends the next one to the background, and a fast
/// background result brings it back into the frame.
/// </para>
/// <para>
/// Everything here except the computation itself runs on the UI thread. The computation must be
/// safe to run beside the UI thread's own use of whatever it captures.
/// </para>
/// <para>
/// Disposing cancels the token every computation is given, so a plan still running for a pick that
/// has ended can stop early; a computation that does not take the token runs to completion, and its
/// result is ignored. Nothing is scheduled after disposal. A computation that throws in the frame is
/// reported through <see cref="Error"/>, as a failed background one is, rather than escaping the
/// pick's draw handler.
/// </para>
/// </remarks>
internal sealed class LatestPreview<TRequest, TResult> : IDisposable
    where TRequest : notnull
    where TResult : class
{
    private static readonly TimeSpan FrameBudget = TimeSpan.FromMilliseconds(40);

    private readonly Func<TRequest, CancellationToken, TResult> _compute;
    private readonly RhinoDoc _document;
    private readonly CancellationTokenSource _cancellation = new();
    private Task<(TResult Result, TimeSpan Elapsed)>? _work;
    private TRequest? _workRequest;
    private TResult? _result;
    private TRequest? _resultRequest;
    private TRequest? _failedRequest;
    private TimeSpan _lastElapsed;
    private bool _workSuperseded;
    private bool _disposed;

    public LatestPreview(RhinoDoc document, Func<TRequest, TResult> compute)
        : this(document, (request, _) => compute(request))
    {
    }

    /// <summary>
    /// For a computation that can stop early: <paramref name="compute"/> is given a token that is
    /// cancelled when this preview is disposed.
    /// </summary>
    public LatestPreview(RhinoDoc document, Func<TRequest, CancellationToken, TResult> compute)
    {
        _document = document;
        _compute = compute;
    }

    /// <summary>The message from a background computation that failed for the current request.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// The preview for <paramref name="request"/>, or the last one completed while it is being
    /// computed. <c>Current</c> says which: false means the result is for an earlier request, or
    /// null when nothing has completed yet.
    /// </summary>
    public (TResult? Result, bool Current) Get(TRequest request, bool slow)
    {
        if (_disposed) return (_result, false);
        Harvest();
        if (_result is not null && Same(_resultRequest, request)) return (_result, true);
        if (Same(_failedRequest, request)) return (_result, false);
        Error = null;
        _failedRequest = default;

        if (!slow && _lastElapsed <= FrameBudget)
        {
            var clock = Stopwatch.StartNew();
            TResult computed;
            try
            {
                computed = _compute(request, _cancellation.Token);
            }
            catch (Exception error)
            {
                // The same outcome as a background failure: the last good preview stays on screen,
                // faded, and the message says why this request has none.
                _failedRequest = request;
                Error = error.GetBaseException().Message;
                return (_result, false);
            }
            Store(request, computed, clock.Elapsed);
            // A task still running is for an older request, and must not replace this when it lands.
            _workSuperseded = _work is not null;
            return (computed, true);
        }

        if (_work is null) Start(request);
        return (_result, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cancelled, not disposed: a background computation may still be reading the token.
        _cancellation.Cancel();
    }

    private void Start(TRequest request)
    {
        _workRequest = request;
        _workSuperseded = false;
        var compute = _compute;
        var token = _cancellation.Token;
        var work = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            var result = compute(request, token);
            return (result, clock.Elapsed);
        }, token);
        _work = work;
        _ = work.ContinueWith(completed =>
        {
            // Observe a fault even when the pick has moved on and nobody harvests it.
            _ = completed.Exception;
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (!_disposed && ReferenceEquals(_work, work)) _document.Views.Redraw();
            }));
        }, TaskScheduler.Default);
    }

    private void Harvest()
    {
        if (_work is not { IsCompleted: true } work) return;
        _work = null;
        if (_workSuperseded) return;
        if (work.IsCompletedSuccessfully)
        {
            Store(_workRequest!, work.Result.Result, work.Result.Elapsed);
            return;
        }

        _failedRequest = _workRequest;
        Error = work.Exception?.GetBaseException().Message ?? "Preview cancelled";
    }

    private void Store(TRequest request, TResult result, TimeSpan elapsed)
    {
        _result = result;
        _resultRequest = request;
        _lastElapsed = elapsed;
    }

    private static bool Same(TRequest? stored, TRequest request) =>
        stored is not null && EqualityComparer<TRequest>.Default.Equals(stored, request);
}
