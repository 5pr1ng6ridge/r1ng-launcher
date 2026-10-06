namespace R1ng.Launcher.Platform;

/// <summary>
/// Runs one short shell/COM operation on a dedicated STA thread with a bounded wait.
/// <para>
/// The completion signal is a <see cref="TaskCompletionSource{TResult}"/> that is deliberately never
/// disposed. When the caller stops waiting after the timeout the worker keeps ownership of the
/// signal, so a late completion is a harmless no-op instead of an
/// <see cref="ObjectDisposedException"/> on the worker thread (which used to crash the process).
/// The late result is discarded, so a timed-out lookup can never publish a stale value, and the
/// worker thread is a background thread so a stuck COM call cannot keep the process alive.
/// </para>
/// </summary>
internal static class BoundedStaCall
{
    /// <summary>The result of a bounded call: whether it finished in time, its value and its task.</summary>
    internal readonly struct Outcome<T>
    {
        public Outcome(bool completed, T? value, Task<T> completion)
        {
            Completed = completed;
            Value = value;
            Completion = completion;
        }

        /// <summary>True when the worker finished before the timeout elapsed.</summary>
        public bool Completed { get; }

        /// <summary>The worker value; only meaningful when <see cref="Completed"/> is true.</summary>
        public T? Value { get; }

        /// <summary>
        /// Completes when the worker finishes, even after the timeout. Never faults (a throwing
        /// worker yields the default value) and can always be awaited safely by a late observer.
        /// </summary>
        public Task<T> Completion { get; }
    }

    /// <summary>
    /// Starts <paramref name="work"/> on a background STA thread and waits up to
    /// <paramref name="timeout"/> for it. A timeout returns an incomplete outcome; the worker keeps
    /// running and its eventual completion is ignored.
    /// </summary>
    public static Outcome<T> Run<T>(Func<T> work, TimeSpan timeout, string threadName)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            T value;
            try
            {
                value = work();
            }
            catch (Exception)
            {
                // Best effort: a shell/COM failure is reported as "nothing" by the callers.
                value = default!;
            }

            completion.TrySetResult(value);
        })
        {
            IsBackground = true,
            Name = threadName,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (completion.Task.Wait(timeout))
            return new Outcome<T>(completed: true, completion.Task.Result, completion.Task);

        // Do not dispose or reuse anything the worker still owns; just stop waiting.
        return new Outcome<T>(completed: false, value: default, completion.Task);
    }
}
