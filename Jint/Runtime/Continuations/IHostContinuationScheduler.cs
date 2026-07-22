namespace Jint.Runtime.Continuations;

/// <summary>
/// Schedules implicit host-continuation resumes onto the single thread that owns a
/// <see cref="Engine"/> instance.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Post"/> may be called from any thread. It must enqueue the callback for a later
/// event-loop turn on the owner thread and must never execute the callback inline.
/// </para>
/// <para>
/// Jint verifies both <see cref="CheckAccess"/> and the managed thread identifier captured when
/// the continuation run starts. Returning <see langword="true"/> from <see cref="CheckAccess"/>
/// on a different thread does not permit the engine to migrate between threads.
/// </para>
/// </remarks>
public interface IHostContinuationScheduler
{
    /// <summary>
    /// Returns <see langword="true"/> when the caller is currently running on the owner event-loop
    /// thread.
    /// </summary>
    bool CheckAccess();

    /// <summary>
    /// Enqueues <paramref name="callback"/> for a later event-loop turn on the owner thread.
    /// The callback must not be invoked inline.
    /// </summary>
    void Post(Action callback);
}
