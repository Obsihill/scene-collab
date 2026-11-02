using System;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Says whether this editor's scene is safe to write to disk after it has been promoted to host.
    ///
    /// Promotion hands the save right to the longest-connected guest. That guest has been receiving
    /// ops right up to the moment the old host vanished, and the queue does not stop at the promotion
    /// frame: a snapshot can be half applied, the outbox can still hold ops written against the old
    /// revision, and the last catch-up burst can still be draining. Saving then writes a scene that is
    /// missing the tail of the session, and Git keeps the lie forever.
    ///
    /// So promotion does not grant the save right immediately: it puts the editor in a short "verifying"
    /// state that clears only after the inbound stream has been quiet for <see cref="QuietSeconds"/> with
    /// nothing pending. The window reports it and <see cref="SceneSaveGuard"/> holds the save.
    /// </summary>
    public sealed class SyncReadiness
    {
        /// <summary>How long the room must be quiet before a promoted host may save.</summary>
        public const double QuietSeconds = 0.75;

        double _lastActivity = double.NegativeInfinity;
        double _promotedAt = double.NegativeInfinity;
        bool _promoted;
        bool _reported;

        /// <summary>False while a promoted host is still catching up: saves are held.</summary>
        public bool IsReady { get; private set; } = true;

        public string Reason { get; private set; } = "ready";

        /// <summary>How many promotions have completed verification in this session.</summary>
        public int Verified { get; private set; }

        /// <summary>The editor just became host. Everything is unverified until the room goes quiet.</summary>
        public void MarkPromoted(double now)
        {
            _promoted = true;
            _reported = false;
            IsReady = false;
            Reason = "promoted: catching up";
            _promotedAt = now;
            _lastActivity = now;
        }

        /// <summary>Some op or snapshot frame arrived: the room is not quiet.</summary>
        public void NoteInbound(double now)
        {
            _lastActivity = now;
            if (_promoted && IsReady)
            {
                // Late traffic after a verified promotion means the room moved on again. Nothing to do
                // about it — the ops were applied — but the faith period restarts.
                _lastActivity = now;
            }
        }

        /// <summary>Called every tick with the queue state.</summary>
        public void Tick(double now, bool busy)
        {
            if (!_promoted)
            {
                IsReady = true;
                Reason = "host from the start";
                return;
            }

            if (busy)
            {
                _lastActivity = now;
                IsReady = false;
                Reason = "promoted: pending work";
                return;
            }

            if (now - _lastActivity < QuietSeconds)
            {
                IsReady = false;
                Reason = "promoted: settling";
                return;
            }

            IsReady = true;
            Reason = "promoted: caught up";
            if (!_reported)
            {
                _reported = true;
                Verified++;
            }
        }

        /// <summary>Stopping sync, leaving the room, or opening another scene clears the gate.</summary>
        public void Reset()
        {
            _promoted = false;
            _reported = false;
            IsReady = true;
            Reason = "ready";
            _lastActivity = double.NegativeInfinity;
            _promotedAt = double.NegativeInfinity;
        }
    }
}
