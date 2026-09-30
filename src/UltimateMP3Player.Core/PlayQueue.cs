namespace UltimateMP3Player.Core;

public readonly record struct QueueEntry(string Id, bool Queued);

// Manual queue first, then the rest of the list; fair shuffle.
// Full queue = MaxQueue songs of the list (or all the others, for a shorter list), each once.
public sealed class PlayQueue
{
    public const int MaxQueue = 50;

    private readonly Random _rng = new();
    private readonly List<(string Id, bool Queued)> _history = new();
    private List<string> _plan = new();
    // Shuffle: this pass through the list in random order; the queue is dealt from it, then a new pass.
    private List<string> _round = new();

    public List<string> Context { get; private set; } = new();
    public string? ContextId { get; private set; }
    public string? ContextName { get; private set; }
    public List<string> UpNext { get; } = new();
    public RepeatMode Repeat { get; set; }
    public string? Current { get; private set; }
    private bool _currentQueued;
    // Emptied by hand (or a song started alone): the list stops refilling what comes next, until Generate.
    public bool Cleared { get; private set; }
    // Automatic queue: at every new song the queue is topped up again, so it stays full.
    public bool AutoFill { get; set; }
    // The list for a song that's alone in its own (the library), with its id and name.
    public Func<(IList<string> Ids, string? Id, string? Name)>? Fallback { get; set; }

    public event Action? Changed;

    private bool _shuffle;
    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            // An emptied queue stays empty.
            if (!Cleared) Deal(Current, false);
            Changed?.Invoke();
        }
    }

    // fill = false: only this song plays, the rest of the list waits for Generate.
    public void Play(IList<string> tracks, int start, string? contextId, string? contextName, bool fill = true)
    {
        Context = tracks.Distinct().ToList();
        ContextId = contextId;
        ContextName = contextName;
        if (Context.Count == 0) return;
        string first = start >= 0 && start < tracks.Count ? tracks[start] : Context[_rng.Next(Context.Count)];
        SetCurrent(first, false);
        Cleared = !fill;
        _plan = new();
        if (fill) Deal(first, AutoFill);
        else if (Shuffle) _round = NewRound(first);
        Changed?.Invoke();
    }

    // A fresh "next up" after this song: shuffled, around the list (automatic queue) or to its end; at most MaxQueue.
    private void Deal(string? from, bool fallback)
    {
        _plan = new();
        if (Shuffle || AutoFill)
        {
            if (Shuffle) _round = NewRound(from);
            Fill(fallback, fromCurrent: true);
        }
        else
        {
            var manual = UpNext.ToHashSet();
            _plan = OrderedAfter(from).Where(id => !manual.Contains(id)).Take(MaxQueue).ToList();
        }
    }

    // Shuffled, starting from a random song.
    public void PlayShuffled(IList<string> tracks, string? contextId, string? contextName)
    {
        _shuffle = true;
        Play(tracks, -1, contextId, contextName);
    }

    public string? Next(bool auto)
    {
        if (auto && Repeat == RepeatMode.One && Current != null) return Current;
        // Automatic queue: it never runs dry (emptied by hand: the songs that were coming come back).
        if (AutoFill && UpNext.Count == 0 && _plan.Count == 0) Fill(true);
        string? id = null;
        bool queued = false;
        if (UpNext.Count > 0)
        {
            id = UpNext[0];
            UpNext.RemoveAt(0);
            queued = true;
        }
        else
        {
            // End of the queue: "repeat all" (or a press on next) goes on with the list.
            if (_plan.Count == 0 && !Cleared && Context.Count > 0 && !(auto && Repeat == RepeatMode.Off)) Fill(false);
            if (_plan.Count > 0)
            {
                id = _plan[0];
                _plan.RemoveAt(0);
            }
            // Emptied queue and "repeat all": all that is left is this song.
            else if (Cleared && auto && Repeat == RepeatMode.All) id = Current;
        }
        if (id != null)
        {
            SetCurrent(id, queued);
            if (AutoFill) Fill(true);
        }
        Changed?.Invoke();
        return id;
    }

    // Goes back; the current song returns to the front.
    public string? Previous()
    {
        if (Current == null) return null;
        string? prev = null;
        if (_history.Count >= 2)
        {
            _history.RemoveAt(_history.Count - 1);
            prev = _history[^1].Id;
            PutBack(Current, _currentQueued);
            _currentQueued = _history[^1].Queued;
        }
        else if (!Shuffle && Context.IndexOf(Current) is int i and > 0)
        {
            prev = Context[i - 1];
            PutBack(Current, _currentQueued);
            _history.Clear();
            _history.Add((prev, false));
            _currentQueued = false;
        }
        if (prev == null) return Current;
        Current = prev;
        if (AutoFill)
        {
            // It may have been waiting: now it's playing. And the queue keeps its maximum.
            _plan.Remove(prev);
            var inList = Context.ToHashSet();
            while (_plan.Count > 0 && UpNext.Concat(_plan).Count(inList.Contains) > Target) _plan.RemoveAt(_plan.Count - 1);
        }
        Changed?.Invoke();
        return prev;
    }

    private void PutBack(string id, bool queued)
    {
        if (queued) UpNext.Insert(0, id);
        else _plan.Insert(0, id);
    }

    private void SetCurrent(string id, bool queued)
    {
        Current = id;
        _currentQueued = queued;
        _history.Add((id, queued));
        if (_history.Count > 1000) _history.RemoveRange(0, _history.Count - 1000);
    }

    private List<string> OrderedAfter(string? id)
    {
        int i = id != null ? Context.IndexOf(id) : -1;
        return Context.Skip(i + 1).ToList();
    }

    // Random round that doesn't start with songs just heard.
    private List<string> NewRound(string? exclude)
    {
        var round = Context.Where(id => id != exclude).ToList();
        for (int i = round.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (round[i], round[j]) = (round[j], round[i]);
        }
        int n = Context.Count;
        int k = Math.Clamp((int)Math.Round(n * 0.3), 1, Math.Max(1, n / 2));
        var recent = RecentlyPlayed(k);
        if (exclude != null) recent.Remove(exclude);
        for (int i = 0; i < Math.Min(k, round.Count); i++)
        {
            if (!recent.Contains(round[i])) continue;
            var candidates = Enumerable.Range(k, Math.Max(0, round.Count - k)).Where(j => !recent.Contains(round[j])).ToList();
            if (candidates.Count == 0) break;
            int swap = candidates[_rng.Next(candidates.Count)];
            (round[i], round[swap]) = (round[swap], round[i]);
        }
        return round;
    }

    private HashSet<string> RecentlyPlayed(int k)
    {
        var set = new HashSet<string>();
        for (int i = _history.Count - 1; i >= 0 && set.Count < k; i--)
            if (Context.Contains(_history[i].Id)) set.Add(_history[i].Id);
        return set;
    }

    // ------------------------------------------------------------------ editing

    public void Enqueue(string id)
    {
        UpNext.Add(id);
        Changed?.Invoke();
    }

    public void PlayNextInQueue(string id)
    {
        UpNext.Insert(0, id);
        Changed?.Invoke();
    }

    // Everything after this song goes, the list's songs too: then playback stops (or repeats this one).
    public void ClearAll()
    {
        UpNext.Clear();
        _plan.Clear();
        Cleared = true;
        Changed?.Invoke();
    }

    // Songs the list could give: the others in it.
    public bool CanGenerate => Context.Any(id => id != Current);

    // How many songs of the list a full queue holds.
    private int Target => Math.Min(MaxQueue, Context.Count(id => id != Current));

    // The generate button: a full queue is generated again from scratch; otherwise the songs that come next in the
    // list go at the END until it's full (49 waiting of 50 → 1 added, none → 50).
    public (int Added, bool Regenerated) Generate()
    {
        Cleared = false;
        int added = Fill(true);
        bool regenerated = false;
        if (added == 0 && Target > 0)
        {
            Deal(Current, true);
            added = _plan.Count;
            regenerated = true;
        }
        Changed?.Invoke();
        return (added, regenerated && added > 0);
    }

    // Automatic queue switched on: the queue fills up right away.
    public void TopUp()
    {
        if (Current == null) return;
        Cleared = false;
        Fill(true);
        Changed?.Invoke();
    }

    // Tops "next up" up to Target songs of the list, at the end: the ones that come after the last one waiting
    // (after this song when nothing waits, or fromCurrent), in list order or in the shuffled pass (then a new pass),
    // wrapping around the list. A song alone in its list takes the fallback list.
    private int Fill(bool fallback, bool fromCurrent = false)
    {
        if (fallback && !CanGenerate && Fallback != null)
        {
            var (ids, fid, fname) = Fallback();
            Context = ids.Distinct().ToList();
            ContextId = fid;
            ContextName = fname;
            if (Shuffle) _round = NewRound(Current);
        }
        var inList = Context.ToHashSet();
        var waiting = UpNext.Concat(_plan).ToHashSet();
        int need = Target - waiting.Count(inList.Contains);
        if (need <= 0) return 0;
        var order = Shuffle ? _round : Context;
        var inOrder = order.ToHashSet();
        string? anchor = fromCurrent ? Current : UpNext.Concat(_plan).LastOrDefault(inOrder.Contains) ?? Current;
        int i = anchor != null ? order.IndexOf(anchor) : -1;
        int added = 0;
        for (int guard = 0; added < need && guard < 3 * (Context.Count + 1); guard++)
        {
            if (++i >= order.Count)
            {
                // End of the list (or of the shuffled pass): around again.
                if (Shuffle) order = _round = NewRound(null);
                i = 0;
                if (order.Count == 0) break;
            }
            var id = order[i];
            if (id == Current || waiting.Contains(id) || !inList.Contains(id)) continue;
            _plan.Add(id);
            waiting.Add(id);
            added++;
        }
        return added;
    }

    public List<QueueEntry> Upcoming(int max)
    {
        var list = new List<QueueEntry>(Math.Min(max, UpNext.Count + _plan.Count));
        foreach (var id in UpNext) { if (list.Count >= max) break; list.Add(new QueueEntry(id, true)); }
        foreach (var id in _plan) { if (list.Count >= max) break; list.Add(new QueueEntry(id, false)); }
        return list;
    }

    public int UpcomingCount => UpNext.Count + _plan.Count;
    public int PlanCount => _plan.Count;

    public void RemoveAt(int index)
    {
        if (index < 0) return;
        if (index < UpNext.Count) UpNext.RemoveAt(index);
        else if (index - UpNext.Count < _plan.Count) _plan.RemoveAt(index - UpNext.Count);
        else return;
        Changed?.Invoke();
    }

    // Moving into the manual queue makes it queued.
    public void Move(int from, int to)
    {
        int total = UpNext.Count + _plan.Count;
        if (from < 0 || from >= total || to < 0 || to >= total || from == to) return;
        var all = UpNext.Select(x => (Id: x, Queued: true)).Concat(_plan.Select(x => (Id: x, Queued: false))).ToList();
        var item = all[from];
        all.RemoveAt(from);
        int queuedLeft = all.Count(x => x.Queued);
        bool queued = to < queuedLeft || (to == queuedLeft && item.Queued);
        all.Insert(to, (item.Id, queued));
        int lastQueued = all.FindLastIndex(x => x.Queued);
        UpNext.Clear();
        UpNext.AddRange(all.Take(lastQueued + 1).Select(x => x.Id));
        _plan = all.Skip(lastQueued + 1).Select(x => x.Id).ToList();
        Changed?.Invoke();
    }

    // Plays an upcoming song, skipping those before it.
    public string? JumpTo(int index)
    {
        string? id;
        bool queued;
        if (index < 0) return null;
        if (index < UpNext.Count)
        {
            id = UpNext[index];
            UpNext.RemoveRange(0, index + 1);
            queued = true;
        }
        else
        {
            int p = index - UpNext.Count;
            if (p >= _plan.Count) return null;
            id = _plan[p];
            UpNext.Clear();
            _plan.RemoveRange(0, p + 1);
            queued = false;
        }
        SetCurrent(id, queued);
        if (AutoFill) Fill(true);
        Changed?.Invoke();
        return id;
    }

    // A deleted song leaves every list.
    public void Forget(string id)
    {
        Context.Remove(id);
        UpNext.RemoveAll(x => x == id);
        _plan.Remove(id);
        _round.Remove(id);
        _history.RemoveAll(h => h.Id == id);
        if (Current == id && _history.Count > 0) _currentQueued = _history[^1].Queued;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ saving

    public QueueState Save(double position) => new()
    {
        Context = Context.ToList(),
        ContextId = ContextId,
        ContextName = ContextName,
        UpNext = UpNext.ToList(),
        Plan = _plan.ToList(),
        Round = _round.ToList(),
        History = _history.TakeLast(100).Select(h => h.Id).ToList(),
        Current = Current,
        CurrentQueued = _currentQueued,
        Position = position,
        Cleared = Cleared,
    };

    // Restores a saved queue, skipping songs that no longer exist.
    public void Restore(QueueState s, Func<string, bool> exists)
    {
        Context = s.Context.Where(exists).Distinct().ToList();
        ContextId = s.ContextId;
        ContextName = s.ContextName;
        UpNext.Clear();
        UpNext.AddRange(s.UpNext.Where(exists));
        // Saves from before the maximum may hold the whole list.
        _plan = s.Plan.Where(exists).Take(MaxQueue).ToList();
        _round = s.Round.Where(exists).ToList();
        if (Shuffle && _round.Count == 0) _round = _plan.ToList();
        _history.Clear();
        _history.AddRange(s.History.Where(exists).Select(id => (id, false)));
        Current = s.Current != null && exists(s.Current) ? s.Current : null;
        _currentQueued = s.CurrentQueued;
        Cleared = s.Cleared;
        if (Current != null && (_history.Count == 0 || _history[^1].Id != Current)) _history.Add((Current, _currentQueued));
        // An old save without a plan: rebuild what follows from the list.
        if (!Cleared && Current != null && _plan.Count == 0 && UpNext.Count == 0 && Context.Count > 1) Deal(Current, false);
        Changed?.Invoke();
    }
}
