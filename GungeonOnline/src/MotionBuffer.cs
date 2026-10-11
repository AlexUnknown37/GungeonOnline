using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline
{
    // Smooth movement for the partner ("snapshot interpolation").
    // Position updates arrive ~30 times a second, but not evenly: some come late, some in bunches.
    // Instead of jumping to each one, we show the partner slightly in the past (InterpolationDelay) and
    // glide between the two updates around that moment. If updates stop briefly, we keep moving the way
    // they were going for a short while (prediction) instead of freezing.
    public class MotionBuffer
    {
        private const float InterpolationDelay = 0.1f;  // seconds behind the newest update
        private const float MaxPrediction = 0.15f;      // how far past the newest update we guess
        private const float TeleportDistance = 4f;      // bigger jumps are real teleports: don't glide

        private struct Snapshot
        {
            public float Time;      // on the partner's clock
            public Vector2 Position;
        }

        private readonly List<Snapshot> snapshots = new List<Snapshot>();
        private float clockOffset;  // our clock minus theirs (includes one-way delay)
        private bool hasOffset;

        public void Clear()
        {
            snapshots.Clear();
            hasOffset = false;
        }

        public void Add(float senderTime, Vector2 position)
        {
            float offset = Time.realtimeSinceStartup - senderTime;
            // Smooth the clock offset so network jitter doesn't make the partner speed up and slow down.
            if (!hasOffset || Mathf.Abs(offset - clockOffset) > 1f)
                clockOffset = offset;
            else
                clockOffset = Mathf.Lerp(clockOffset, offset, 0.05f);
            hasOffset = true;

            // Unreliable packets can arrive out of order; keep the list sorted and skip duplicates.
            int i = snapshots.Count;
            while (i > 0 && snapshots[i - 1].Time > senderTime)
                i--;
            if (i > 0 && Mathf.Approximately(snapshots[i - 1].Time, senderTime))
                return;
            Snapshot s = new Snapshot();
            s.Time = senderTime;
            s.Position = position;
            snapshots.Insert(i, s);

            // One second of history is plenty.
            while (snapshots.Count > 2 && snapshots[0].Time < senderTime - 1f)
                snapshots.RemoveAt(0);
        }

        public bool HasData { get { return snapshots.Count > 0; } }

        public Vector2 Sample()
        {
            if (snapshots.Count == 0)
                return Vector2.zero;
            if (snapshots.Count == 1)
                return snapshots[0].Position;

            float renderTime = Time.realtimeSinceStartup - clockOffset - InterpolationDelay;

            if (renderTime <= snapshots[0].Time)
                return snapshots[0].Position;

            for (int i = 0; i < snapshots.Count - 1; i++)
            {
                Snapshot a = snapshots[i];
                Snapshot b = snapshots[i + 1];
                if (renderTime > b.Time)
                    continue;
                if ((b.Position - a.Position).sqrMagnitude > TeleportDistance * TeleportDistance)
                    return renderTime < b.Time ? a.Position : b.Position;
                float t = (renderTime - a.Time) / Mathf.Max(b.Time - a.Time, 0.0001f);
                return Vector2.Lerp(a.Position, b.Position, t);
            }

            // Newer than anything we have: predict from the last two updates, for a short time only.
            Snapshot last = snapshots[snapshots.Count - 1];
            Snapshot prev = snapshots[snapshots.Count - 2];
            float dt = last.Time - prev.Time;
            if (dt <= 0.0001f || (last.Position - prev.Position).sqrMagnitude > TeleportDistance * TeleportDistance)
                return last.Position;
            Vector2 velocity = (last.Position - prev.Position) / dt;
            return last.Position + velocity * Mathf.Min(renderTime - last.Time, MaxPrediction);
        }
    }
}
