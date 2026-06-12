namespace OwTranslateLite.Core;

/// <summary>
/// Aligns each OCR frame to the chat timeline using OW's append-only invariant: the visible
/// region is always a contiguous suffix of the log — old lines fade off the top, new lines are
/// appended at the bottom, nothing is inserted in the middle. So a frame is fully explained by a
/// single scroll offset: "visible[0..overlap) maps onto a suffix of the timeline tail, and the
/// bottom `newCount` lines are genuinely new."
///
/// Matching is positional, not greedy: we score every overlap pair without breaking on the first
/// sub-threshold line, then pick the shift that explains the most lines. This tolerates scattered
/// single-line OCR drift (a line whose text drifted below threshold this frame is still treated as
/// the same message by position, and its drifted text is folded in as a variant). When no shift
/// explains the frame, we do NOT dump everything as new — we treat it as a bad frame and change
/// nothing, unless the frame genuinely resembles nothing in the timeline (a real new burst).
/// </summary>
public sealed class TimelineAlignmentDetector
{
    private readonly int _tailWindowSize;
    private readonly double _matchThreshold;
    private readonly double _overlapAcceptRatio;

    public TimelineAlignmentDetector(
        int tailWindowSize = 15,
        double matchThreshold = 0.76,
        double overlapAcceptRatio = 0.5)
    {
        _tailWindowSize = Math.Max(1, tailWindowSize);
        _matchThreshold = Math.Clamp(matchThreshold, 0, 1);
        _overlapAcceptRatio = Math.Clamp(overlapAcceptRatio, 0, 1);
    }

    public TimelineAlignmentResult Detect(
        ChatTimeline timeline,
        IReadOnlyList<ParsedChatLine> visibleLines,
        long frameId)
    {
        if (visibleLines.Count == 0)
        {
            return TimelineAlignmentResult.Empty(frameId, "empty-frame");
        }

        if (timeline.Messages.Count == 0)
        {
            return AddAllAsNew(timeline, visibleLines, frameId, "cold-start");
        }

        IReadOnlyList<ChatMessage> tail = timeline.TailWindow(_tailWindowSize);
        ShiftAlignment best = FindBestShift(tail, visibleLines);

        bool overlapExplained =
            best.OverlapLength > 0 &&
            best.MatchedCount >= Math.Max(1, (int)Math.Ceiling(best.OverlapLength * _overlapAcceptRatio));

        if (!overlapExplained)
        {
            // No scroll offset explains the frame. Distinguish a degraded re-display of known
            // messages (heavy drift / partial OCR) from a genuinely new conversation.
            if (AnyLineMatchesTimeline(tail, visibleLines))
            {
                // Known content we simply failed to align this frame: do nothing, wait for a
                // cleaner frame. Never rebuild already-known messages as new (that was the dup bug).
                return TimelineAlignmentResult.BadFrame(frameId, "bad-frame-drift");
            }

            return AddAllAsNew(timeline, visibleLines, frameId, "no-match-all-new");
        }

        // Overlap lines map onto a suffix of the tail by position — observe them in place even when
        // a particular line drifted below threshold this frame (its text becomes a consensus variant).
        List<TimelineAlignmentMatch> matches = new(best.OverlapLength);
        for (int i = 0; i < best.OverlapLength; i++)
        {
            ChatMessage message = tail[best.TailStart + i];
            ParsedChatLine line = visibleLines[i];
            timeline.Observe(message, line, frameId);
            matches.Add(new TimelineAlignmentMatch(message, line, best.Scores[i]));
        }

        // The bottom `newCount` visible lines stick out past the known timeline → genuinely new.
        List<ChatMessage> newMessages = new(visibleLines.Count - best.OverlapLength);
        for (int i = best.OverlapLength; i < visibleLines.Count; i++)
        {
            newMessages.Add(timeline.AddDetected(visibleLines[i], frameId));
        }

        string reason = newMessages.Count == 0 ? "suffix-stable" : "suffix-append";
        return new TimelineAlignmentResult(
            frameId,
            false,
            reason,
            matches,
            newMessages,
            best.AverageScore);
    }

    public void Reset()
    {
        // Stateless across frames: alignment is a pure function of (timeline, visible lines).
    }

    private ShiftAlignment FindBestShift(
        IReadOnlyList<ChatMessage> tail,
        IReadOnlyList<ParsedChatLine> visible)
    {
        ShiftAlignment best = ShiftAlignment.None;

        // newCount = how many bottom visible lines stick out beyond the known timeline.
        // overlapLen = visible lines that align onto a suffix of the tail at this scroll offset.
        for (int newCount = 0; newCount < visible.Count; newCount++)
        {
            int overlapLen = visible.Count - newCount;
            int tailStart = tail.Count - overlapLen;
            if (tailStart < 0)
            {
                continue; // overlap longer than the available tail at this offset
            }

            double[] scores = new double[overlapLen];
            int matched = 0;
            double sum = 0;
            for (int i = 0; i < overlapLen; i++)
            {
                double score = GetMatchScore(tail[tailStart + i], visible[i]);
                scores[i] = score;
                sum += score;
                if (score >= _matchThreshold)
                {
                    matched++;
                }
            }

            double average = overlapLen > 0 ? sum / overlapLen : 0;
            ShiftAlignment candidate = new(tailStart, overlapLen, newCount, matched, scores, average);

            // Maximize explained lines; this locks onto the true scroll offset without breaking on
            // a single drifted line. Ties prefer fewer new messages (don't invent appends), then a
            // higher average score.
            if (candidate.MatchedCount > best.MatchedCount ||
                (candidate.MatchedCount == best.MatchedCount && candidate.NewCount < best.NewCount) ||
                (candidate.MatchedCount == best.MatchedCount &&
                 candidate.NewCount == best.NewCount &&
                 candidate.AverageScore > best.AverageScore))
            {
                best = candidate;
            }
        }

        return best;
    }

    private bool AnyLineMatchesTimeline(
        IReadOnlyList<ChatMessage> tail,
        IReadOnlyList<ParsedChatLine> visible)
    {
        foreach (ParsedChatLine line in visible)
        {
            foreach (ChatMessage message in tail)
            {
                if (GetMatchScore(message, line) >= _matchThreshold)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static TimelineAlignmentResult AddAllAsNew(
        ChatTimeline timeline,
        IReadOnlyList<ParsedChatLine> visibleLines,
        long frameId,
        string reason)
    {
        List<ChatMessage> newMessages = new(visibleLines.Count);
        foreach (ParsedChatLine line in visibleLines)
        {
            newMessages.Add(timeline.AddDetected(line, frameId));
        }

        return new TimelineAlignmentResult(
            frameId,
            false,
            reason,
            Array.Empty<TimelineAlignmentMatch>(),
            newMessages,
            0);
    }

    private static double GetMatchScore(ChatMessage message, ParsedChatLine line)
    {
        string messageSpeaker = OcrDedupeNormalizer.NormalizeSpeaker(message.Speaker);
        string lineSpeaker = OcrDedupeNormalizer.NormalizeSpeaker(line.Speaker);
        if (!OcrDedupeNormalizer.IsSpeakerMatch(messageSpeaker, lineSpeaker))
        {
            return 0;
        }

        string messageText = OcrDedupeNormalizer.NormalizeText(message.ConsensusText);
        string lineText = OcrDedupeNormalizer.NormalizeText(line.SourceText);
        double textScore = OcrDedupeNormalizer.TextSimilarityScore(messageText, lineText);
        if (string.Equals(messageSpeaker, lineSpeaker, StringComparison.Ordinal))
        {
            textScore = Math.Min(1, textScore + 0.04);
        }

        return textScore;
    }

    private sealed record ShiftAlignment(
        int TailStart,
        int OverlapLength,
        int NewCount,
        int MatchedCount,
        IReadOnlyList<double> Scores,
        double AverageScore)
    {
        public static ShiftAlignment None { get; } =
            new(-1, 0, int.MaxValue, -1, Array.Empty<double>(), 0);
    }
}

public sealed record TimelineAlignmentResult(
    long FrameId,
    bool IsBadFrame,
    string Reason,
    IReadOnlyList<TimelineAlignmentMatch> Matches,
    IReadOnlyList<ChatMessage> NewMessages,
    double AverageMatchScore)
{
    public static TimelineAlignmentResult Empty(long frameId, string reason) =>
        new(
            frameId,
            false,
            reason,
            Array.Empty<TimelineAlignmentMatch>(),
            Array.Empty<ChatMessage>(),
            0);

    public static TimelineAlignmentResult BadFrame(long frameId, string reason) =>
        new(
            frameId,
            true,
            reason,
            Array.Empty<TimelineAlignmentMatch>(),
            Array.Empty<ChatMessage>(),
            0);
}

public sealed record TimelineAlignmentMatch(
    ChatMessage Message,
    ParsedChatLine Line,
    double Score);
