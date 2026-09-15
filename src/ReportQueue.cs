using System;
using System.Collections.Generic;

namespace SlmapsServerPlugin
{
    internal static class ReportEvents
    {
        public const string RoundStart = "round_start";
        public const string RoundEnd = "round_end";
        public const string Periodic = "periodic";
    }

    // Taken on the main thread so the background send never touches game state.
    internal sealed class ReportSnapshot
    {
        public string Event;
        public int Seed;
        public int Port;
        public string RoundId;
        public DateTime? RoundStartedAtUtc;
        public double? ElapsedSeconds;
        public double CreatedAt;
    }

    // Round events keep their order under a capacity cap; periodic has a single slot that a newer seed replaces.
    internal sealed class ReportQueue
    {
        public const int DefaultCapacity = 20;

        private readonly List<ReportSnapshot> _roundEvents = new List<ReportSnapshot>();
        private readonly int _capacity;
        private ReportSnapshot _periodic;

        public ReportQueue(int capacity)
        {
            _capacity = capacity < 2 ? 2 : capacity;
        }

        public int RoundEventCount
        {
            get { return _roundEvents.Count; }
        }

        public bool HasPeriodic
        {
            get { return _periodic != null; }
        }

        public int EnqueueRoundEvent(ReportSnapshot item)
        {
            _roundEvents.RemoveAll(x => SameKey(x, item));
            _roundEvents.Add(item);
            int dropped = 0;
            while (_roundEvents.Count > _capacity)
            {
                _roundEvents.RemoveAt(0);
                dropped++;
            }
            return dropped;
        }

        public bool HasRoundEventFor(int seed)
        {
            return _roundEvents.Exists(x => x.Seed == seed);
        }

        public void SetPeriodic(ReportSnapshot item)
        {
            _periodic = item;
        }

        public void DropPeriodic()
        {
            _periodic = null;
        }

        public void Clear()
        {
            _roundEvents.Clear();
            _periodic = null;
        }

        public ReportSnapshot TakeNext(int currentSeed, bool periodicEnabled)
        {
            if (_roundEvents.Count > 0)
            {
                ReportSnapshot first = _roundEvents[0];
                _roundEvents.RemoveAt(0);
                return first;
            }
            if (_periodic != null)
            {
                ReportSnapshot p = _periodic;
                _periodic = null;
                if (!periodicEnabled || p.Seed != currentSeed)
                {
                    return null;
                }
                return p;
            }
            return null;
        }

        public bool ReturnFailed(ReportSnapshot item, int currentSeed)
        {
            if (item.Event == ReportEvents.Periodic)
            {
                if (_periodic == null && item.Seed == currentSeed)
                {
                    _periodic = item;
                    return true;
                }
                return false;
            }
            if (_roundEvents.Exists(x => SameKey(x, item)))
            {
                return false;
            }
            _roundEvents.Insert(0, item);
            if (_roundEvents.Count > _capacity)
            {
                _roundEvents.RemoveAt(0);
                return false;
            }
            return true;
        }

        private static bool SameKey(ReportSnapshot a, ReportSnapshot b)
        {
            return a.Seed == b.Seed && string.Equals(a.Event, b.Event, StringComparison.Ordinal);
        }
    }
}
