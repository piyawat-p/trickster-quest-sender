namespace TricksterQuestSender.Core;

public sealed record RunResult(string Message, int Completed);

/// <summary>
/// Runs a preset: same behaviour as the macOS app. Talks to the OS only through
/// <see cref="IInputDriver"/> and <see cref="IScreenReader"/>, so it can be tested with fakes.
/// </summary>
public sealed class QuestEngine
{
    private readonly IInputDriver _input;
    private readonly IScreenReader _screen;
    private readonly IClock _clock;
    private readonly Random _random;
    /// <summary>Where the dialog was last seen; reading just this area is much faster.</summary>
    private ScreenRect? _dialogRegion;

    public QuestEngine(IInputDriver input, IScreenReader screen, IClock? clock = null, Random? random = null)
    {
        _input = input;
        _screen = screen;
        _clock = clock ?? new SystemClock();
        _random = random ?? new Random();
    }

    public event Action<string>? StatusChanged;
    public event Action<int>? CompletedChanged;

    public int CompletedCount { get; private set; }
    public int CurrentLoop { get; private set; }

    private void Status(string s) => StatusChanged?.Invoke(s);

    private RunResult Stop(string message) => new(message, CompletedCount);

    public async Task<RunResult> RunAsync(QuestConfig config, ScreenPoint startMouse, CancellationToken ct)
    {
        var steps = config.Steps.Where(s => s.Enabled).ToList();
        _dialogRegion = null;
        CompletedCount = 0;
        CurrentLoop = 0;
        double scale = _screen.PointScale;

        try
        {
            Status("กำลังจะเริ่ม...");
            await _clock.Delay(config.StartDelayMs, ct);

            ScreenPoint? lastClick = null, lastNpc = null;
            var anchor = startMouse; // which display to read
            int loop = 0, noDialogStreak = 0;

            while (config.Unlimited || loop < Math.Max(config.Loops, 1))
            {
                loop++;
                CurrentLoop = loop;
                string label = $"รอบ {loop}/{(config.Unlimited ? "∞" : config.Loops.ToString())}";
                bool restartRound = false;

                foreach (var step in steps)
                {
                    ct.ThrowIfCancellationRequested();

                    // If the mouse is no longer where we left it, the user took over: stop.
                    if (config.StopIfMouseMoved && lastClick is { } lc && _input.CursorPosition.DistanceTo(lc) > 30 * scale)
                        return Stop($"หยุดเพราะมีการขยับเมาส์ (รอบที่ {loop})");

                    Status($"{label}: {step.Name}");

                    switch (step.Kind)
                    {
                        case StepKind.ClickNPC:
                        {
                            var names = step.NpcNames;
                            // Once found, don't wait long: NPCs stand still.
                            int timeout = lastNpc is null ? step.TimeoutMs : Math.Min(step.TimeoutMs, 800);
                            var hit = await WaitForNpcAsync(names, timeout, lastNpc, ct);
                            // If the label is hidden (e.g. behind the dialog), reuse the last spot.
                            ScreenPoint? labelCenter = hit?.Center ?? lastNpc;
                            if (labelCenter is not { } c)
                                return Stop($"หยุด: หา {string.Join(" / ", names)} ไม่เจอในรอบที่ {loop} (ส่งสำเร็จ {CompletedCount} รอบ)");
                            var p = new ScreenPoint(c.X, c.Y + step.NpcOffsetY * scale);
                            await _input.ClickAsync(p, ct);
                            lastClick = p;
                            lastNpc = c;
                            anchor = p;
                            break;
                        }

                        case StepKind.DialogUntilEnd:
                        {
                            var r = await RunDialogAsync(step, label, anchor, ct);
                            string done = $"ส่งสำเร็จ {CompletedCount} รอบ";
                            switch (r.Kind)
                            {
                                case DialogOutcomeKind.Finished:
                                    CompletedCount++;
                                    CompletedChanged?.Invoke(CompletedCount);
                                    noDialogStreak = 0;
                                    break;
                                case DialogOutcomeKind.NeverOpened:
                                    noDialogStreak++;
                                    if (noDialogStreak >= 3)
                                        return Stop($"หยุด: คลิก NPC แล้วบทสนทนาไม่ขึ้น 3 ครั้งติด ({done})");
                                    Status($"บทสนทนาไม่ขึ้น ลองคลิก NPC ใหม่ ({noDialogStreak}/3)");
                                    restartRound = true;
                                    break;
                                case DialogOutcomeKind.MissingRequired:
                                    return Stop(step.Floor > 0
                                        ? $"หยุด: บทสนทนาจบโดยไม่เจอ Chaos Integer.{step.Floor}Byte — ชั้นหอคอยตรงไหม? ({done})"
                                        : $"หยุด: บทสนทนาจบโดยไม่เจอ “{step.Text}” ({done})");
                                case DialogOutcomeKind.StopTextSeen:
                                    return Stop($"หยุด: เจอข้อความ “{step.StopText}” ({done})");
                                case DialogOutcomeKind.TooManyPresses:
                                    return Stop($"หยุด: กดเกิน {step.MaxPresses} ครั้งแล้วบทสนทนายังไม่จบ ({done})");
                                case DialogOutcomeKind.UnknownPage:
                                    return Stop($"หยุด: ข้อความเปลี่ยนเป็น “{r.Text}” ซึ่งไม่เคยเจอตอนบันทึก ({done})");
                                case DialogOutcomeKind.ClosedBeforeEnd:
                                    return Stop($"หยุด: บทสนทนาปิดก่อนถึงหน้าสุดท้าย ({done})");
                            }
                            break;
                        }

                        case StepKind.ClickStartMouse:
                            await _input.ClickAsync(startMouse, ct);
                            lastClick = startMouse;
                            break;

                        case StepKind.ClickPoint:
                            if (step.Point is { } pt)
                            {
                                await _input.ClickAsync(pt, ct);
                                lastClick = pt;
                            }
                            break;

                        case StepKind.Key:
                        {
                            string expect = step.Text.Trim();
                            if (expect.Length == 0)
                            {
                                await _input.PressKeyAsync(step.Key, ct);
                                break;
                            }
                            var result = await PressWhenVisibleAsync(expect, step, label, anchor, ct);
                            if (result == PressResult.NotFound)
                                return Stop($"หยุด: ไม่เจอหน้า “{expect}” ในรอบที่ {loop}");
                            if (result == PressResult.Stuck)
                                return Stop($"หยุด: กดแล้วหน้า “{expect}” ไม่เปลี่ยน (รอบที่ {loop})");
                            break;
                        }

                        case StepKind.WaitText:
                        case StepKind.ClickText:
                        {
                            var hit = await WaitForTextAsync(step.Text, step.TimeoutMs, anchor, ct);
                            if (hit is null)
                                return Stop($"หยุด: ไม่เจอข้อความ “{step.Text}” ในรอบที่ {loop} (ส่งสำเร็จ {CompletedCount} รอบ)");
                            if (step.Kind == StepKind.ClickText)
                            {
                                await _input.ClickAsync(hit.Center, ct);
                                lastClick = hit.Center;
                            }
                            break;
                        }
                    }

                    if (restartRound) break;
                    int jitter = config.JitterMs > 0 ? _random.Next(0, config.JitterMs + 1) : 0;
                    await _clock.Delay(step.DelayAfterMs + jitter, ct);
                }
            }

            return Stop(CompletedCount > 0 ? $"ครบ {loop} รอบแล้ว (ส่งสำเร็จ {CompletedCount})" : $"ครบ {loop} รอบแล้ว");
        }
        catch (OperationCanceledException)
        {
            return Stop("หยุดแล้ว");
        }
    }

    // ---------------------------------------------------------------- dialog

    public enum DialogOutcomeKind
    {
        Finished, NeverOpened, MissingRequired, StopTextSeen, TooManyPresses, UnknownPage, ClosedBeforeEnd,
    }

    private readonly record struct DialogOutcome(DialogOutcomeKind Kind, string Text = "");

    /// <summary>
    /// Press the option key once per new dialog page until the round ends.
    /// <list type="bullet">
    /// <item>Which page: recorded page images first, OCR text as fallback; with recorded pages, a page
    /// matching none of them = wording changed → stop.</item>
    /// <item>Page changed: by page index, else by how the page looks (works even when OCR can't read
    /// Thai), else by text. A new page must be seen on 2 reads in a row before it's pressed.</item>
    /// <item>Same page for RetryMs → the press was lost → press again.</item>
    /// <item>Last page (any of): the last recorded page · an end text ("a | b") · "[Chaos Integer.{Floor}Byte]".
    /// It is pressed once and ends the round.</item>
    /// <item>Must appear during the dialog: the required text (any alternative) and, if Floor is set,
    /// "Chaos Integer.{Floor}Byte" — otherwise the round counts as failed and the run stops.</item>
    /// </list>
    /// </summary>
    private async Task<DialogOutcome> RunDialogAsync(QuestStep step, string label, ScreenPoint near, CancellationToken ct)
    {
        var requiredAlts = TextMatching.Alternatives(step.Text);
        var stopAlts = TextMatching.Alternatives(step.StopText);
        var endAlts = TextMatching.Alternatives(step.EndText);
        int floor = step.Floor;
        var known = step.KnownPages;
        // Images for this run: start as recorded; a page recognised only by text gets its current
        // look here (this run only; the tab's recording is never changed).
        var images = step.KnownPageHashes.ToArray();
        bool useRecordedImages = images.Any(h => h is not null);
        bool hasEndRule = endAlts.Count > 0 || known.Count > 0 || floor > 0;
        double scale = _screen.PointScale;

        bool sawRequired = requiredAlts.Count == 0;
        bool floorSeen = floor <= 0;
        int presses = 0, missing = 0, candidateReads = 0;
        string? lastKey = null, candidateKey = null;
        byte[]? lastLook = null, candidateLook = null;
        DateTime lastPressAt = DateTime.MinValue;
        DateTime openDeadline = _clock.Now.AddMilliseconds(step.TimeoutMs);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ScreenFrame? frame = _dialogRegion is { } region ? await _screen.ReadRegionFrameAsync(region, ct) : null;
            frame ??= await _screen.ReadScreenFrameAsync(near, ct);
            var option = TextMatching.FindDialogOption(step.Key, frame.Hits);

            // Not in the usual spot: check the whole screen before deciding the dialog is gone.
            if (option is null && _dialogRegion is not null && (missing >= 1 || presses == 0))
            {
                frame = await _screen.ReadScreenFrameAsync(near, ct);
                option = TextMatching.FindDialogOption(step.Key, frame.Hits);
            }
            var hits = frame.Hits;

            if (!sawRequired && requiredAlts.Any(r => TextMatching.FindFuzzy(r, hits) is not null)) sawRequired = true;
            if (!floorSeen && TextMatching.HasFloorItem(hits, floor, bracketed: false)) floorSeen = true;
            if (stopAlts.Any(w => TextMatching.FindFuzzy(w, hits) is not null))
                return new(DialogOutcomeKind.StopTextSeen);
            var success = sawRequired && floorSeen ? DialogOutcomeKind.Finished : DialogOutcomeKind.MissingRequired;

            if (option is null)
            {
                if (presses == 0)
                {
                    if (_clock.Now > openDeadline) return new(DialogOutcomeKind.NeverOpened);
                    Status($"{label}: รอบทสนทนาขึ้น...");
                }
                else if (++missing >= 2)
                {
                    return new(hasEndRule ? DialogOutcomeKind.ClosedBeforeEnd : success);
                }
                candidateKey = null;
                candidateLook = null;
                await _clock.Delay(120, ct);
                continue;
            }

            missing = 0;
            _dialogRegion = RegionAroundOption(option.Rect);
            byte[]? look = frame.Image?.Fingerprint(ImageFingerprint.PageRect(option.Rect, scale));

            // ---- Which recorded page is this?
            int? pageIndex = null;
            string how = "";
            if (useRecordedImages && look is not null)
            {
                var scored = images
                    .Select((h, i) => (Index: i, Distance: h is null ? double.MaxValue : ImageFingerprint.Distance(look, h)))
                    .Where(x => x.Distance <= 1)
                    .OrderBy(x => x.Distance)
                    .ToList();
                if (scored.Count > 0 && scored[0].Distance <= ImageFingerprint.MatchThreshold
                    && (scored.Count < 2 || scored[1].Distance - scored[0].Distance >= ImageFingerprint.RequiredMargin))
                {
                    pageIndex = scored[0].Index;
                    how = $"ภาพตรง {Math.Round((1 - scored[0].Distance) * 100)}%";
                }
            }
            if (pageIndex is null && known.Count > 0)
            {
                int byText = known.ToList().FindIndex(k => TextMatching.SameLine(k, option.Text));
                if (byText >= 0)
                {
                    pageIndex = byText;
                    how = "ข้อความ";
                    if (useRecordedImages && look is not null) images[byText] = look;
                }
            }
            // Not one of the recorded pages. Without a floor, that means the wording changed → stop.
            // With a floor set, the system check takes over ("recorded OR system check"): keep
            // pressing, judge page changes by look, and end on "[Chaos Integer.{Floor}Byte]".
            if (pageIndex is null && known.Count > 0 && floor <= 0)
                return new(DialogOutcomeKind.UnknownPage, option.Text);
            if (pageIndex is null && known.Count > 0) how = "ไม่ตรงกับที่บันทึก · ใช้เช็กชั้น";

            // ---- Is it a new page? (by index, else by look, else by text)
            string? key = null;
            bool isNewPage, sameAsCandidate;
            if (pageIndex is { } pi)
            {
                key = $"#{pi}";
                isNewPage = key != lastKey;
                sameAsCandidate = candidateKey == key;
            }
            else if (look is not null)
            {
                isNewPage = lastLook is null || ImageFingerprint.Distance(look, lastLook) > ImageFingerprint.PageChangeThreshold;
                sameAsCandidate = candidateLook is not null
                                  && ImageFingerprint.Distance(look, candidateLook) <= ImageFingerprint.PageChangeThreshold;
            }
            else
            {
                key = option.Text;
                isNewPage = lastKey is null || !TextMatching.SameLine(lastKey, option.Text);
                sameAsCandidate = candidateKey is not null && TextMatching.SameLine(candidateKey, option.Text);
            }

            // ---- New page: confirm on two reads in a row first (no acting on a half-drawn page).
            if (isNewPage)
            {
                if (sameAsCandidate) candidateReads++;
                else { candidateKey = key; candidateLook = look; candidateReads = 1; }
                if (candidateReads < 2)
                {
                    await _clock.Delay(120, ct);
                    continue;
                }
            }

            bool stuck = (_clock.Now - lastPressAt).TotalMilliseconds > step.RetryMs;
            if (isNewPage || stuck)
            {
                if (presses >= step.MaxPresses) return new(DialogOutcomeKind.TooManyPresses);
                bool isEnd = (pageIndex is not null && known.Count > 0 && pageIndex == known.Count - 1)
                             || endAlts.Any(e => TextMatching.SameLine(e, option.Text))
                             || TextMatching.HasFloorItem(hits, floor, bracketed: true);
                string pageLabel = pageIndex is { } p
                    ? $"หน้า {p + 1}/{known.Count} ({how})"
                    : $"หน้าที่ {presses + 1}{(how.Length > 0 ? $" ({how})" : "")}";
                Status($"{label}: กด {step.Key} — {pageLabel}{(isNewPage ? "" : " (กดซ้ำ)")}{(isEnd ? " · หน้าสุดท้าย" : "")}");
                await _input.PressKeyAsync(step.Key, ct);
                presses++;
                lastKey = key;
                lastLook = look;
                lastPressAt = _clock.Now;
                candidateKey = null;
                candidateLook = null;

                if (isEnd)
                {
                    // Give the game a moment to take the press, but never press the last page twice.
                    await _clock.Delay(Math.Min(step.RetryMs, 1000), ct);
                    return new(success);
                }
            }
            await _clock.Delay(120, ct);
        }
    }

    private ScreenRect RegionAroundOption(ScreenRect r)
    {
        double s = _screen.PointScale;
        return new ScreenRect(r.X - 80 * s, r.Y - 170 * s, 520 * s, 340 * s);
    }

    // ---------------------------------------------------------------- key with expected text

    private enum PressResult { Pressed, NotFound, Stuck }

    /// <summary>Wait for the line, press, wait for the line to go away; press again if it doesn't (max 3).</summary>
    private async Task<PressResult> PressWhenVisibleAsync(string text, QuestStep step, string label,
                                                          ScreenPoint near, CancellationToken ct)
    {
        var deadline = _clock.Now.AddMilliseconds(step.TimeoutMs);
        Status($"{label}: รอหน้า “{text}”");
        while (await LocateAsync(text, near, regionOnly: false, ct) is null)
        {
            if (_clock.Now > deadline) return PressResult.NotFound;
            await _clock.Delay(120, ct);
        }
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            Status($"{label}: กด {step.Key} — {text}{(attempt > 1 ? " (กดซ้ำ)" : "")}");
            await _input.PressKeyAsync(step.Key, ct);
            var changeDeadline = _clock.Now.AddMilliseconds(step.RetryMs);
            while (_clock.Now < changeDeadline)
            {
                await _clock.Delay(120, ct);
                if (await LocateAsync(text, near, regionOnly: true, ct) is null) return PressResult.Pressed;
            }
        }
        return PressResult.Stuck;
    }

    private async Task<TextHit?> LocateAsync(string text, ScreenPoint near, bool regionOnly, CancellationToken ct)
    {
        if (_dialogRegion is { } region)
        {
            var inRegion = TextMatching.FindFuzzy(text, await _screen.ReadRegionAsync(region, ct));
            if (inRegion is not null || regionOnly) return inRegion;
        }
        var hit = TextMatching.FindFuzzy(text, await _screen.ReadScreenAsync(near, ct));
        if (hit is not null) _dialogRegion = RegionAroundOption(hit.Rect);
        return hit;
    }

    // ---------------------------------------------------------------- waiting helpers

    private async Task<TextHit?> WaitForTextAsync(string text, int timeoutMs, ScreenPoint near, CancellationToken ct)
    {
        var deadline = _clock.Now.AddMilliseconds(timeoutMs);
        do
        {
            var hit = TextMatching.FindFuzzy(text, await _screen.ReadScreenAsync(near, ct));
            if (hit is not null) return hit;
            await _clock.Delay(250, ct);
        } while (_clock.Now < deadline);
        return null;
    }

    private async Task<TextHit?> WaitForNpcAsync(IReadOnlyList<string> names, int timeoutMs, ScreenPoint? prefer,
                                                 CancellationToken ct)
    {
        var deadline = _clock.Now.AddMilliseconds(timeoutMs);
        do
        {
            var screens = await _screen.ReadAllDisplaysAsync(ct);
            var hit = TextMatching.FindNpc(names, screens, prefer, 150 * _screen.PointScale);
            if (hit is not null) return hit;
            await _clock.Delay(250, ct);
        } while (_clock.Now < deadline);
        return null;
    }
}
