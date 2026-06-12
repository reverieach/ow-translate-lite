namespace OwTranslateLite.Core;

public sealed class TimelineAlignmentDetector
{
    private readonly int _tailWindowSize;
    private readonly double _matchThreshold;
    private bool _previousFrameHadVisibleChat;

    public TimelineAlignmentDetector(int tailWindowSize = 15, double matchThreshold = 0.76)
    {
        _tailWindowSize = Math.Max(1, tailWindowSize);
        _matchThreshold = Math.Clamp(matchThreshold, 0, 1);
    }

    public TimelineAlignmentResult Detect(
        ChatTimeline timeline,
        IReadOnlyList<ParsedChatLine> visibleLines,
        long frameId)
    {
        if (visibleLines.Count == 0)
        {
            _previousFrameHadVisibleChat = false;
            return TimelineAlignmentResult.Empty(frameId, "empty-frame");
        }

        if (timeline.Messages.Count == 0)
        {
            _previousFrameHadVisibleChat = true;
            return AddAllAsNew(timeline, visibleLines, frameId, "cold-start");
        }

        if (!_previousFrameHadVisibleChat && visibleLines.Count <= 2)
        {
            _previousFrameHadVisibleChat = true;
            return AddAllAsNew(timeline, visibleLines, frameId, "after-empty-force-new");
        }

        IReadOnlyList<ChatMessage> tail = timeline.TailWindow(_tailWindowSize);
        AlignmentCandidate best = FindBestSuffixCandidate(tail, visibleLines);
        if (best.MatchedCount == 0)
        {
            _previousFrameHadVisibleChat = true;
            return AddAllAsNew(timeline, visibleLines, frameId, "no-suffix-match");
        }

        List<TimelineAlignmentMatch> matches = [];
        for (int i = 0; i < best.MatchedCount; i++)
        {
            ChatMessage message = tail[best.TailStartIndex + i];
            ParsedChatLine line = visibleLines[i];
            timeline.Observe(message, line, frameId);
            matches.Add(new TimelineAlignmentMatch(message, line, best.Scores[i]));
        }

        List<ChatMessage> newMessages = [];
        for (int i = best.MatchedCount; i < visibleLines.Count; i++)
        {
            newMessages.Add(timeline.AddDetected(visibleLines[i], frameId));
        }

        _previousFrameHadVisibleChat = true;
        return new TimelineAlignmentResult(
            frameId,
            false,
            best.Reason,
            matches,
            newMessages,
            best.AverageScore);
    }

    public void Reset()
    {
        _previousFrameHadVisibleChat = false;
    }

    private TimelineAlignmentResult AddAllAsNew(
        ChatTimeline timeline,
        IReadOnlyList<ParsedChatLine> visibleLines,
        long frameId,
        string reason)
    {
        List<ChatMessage> newMessages = [];
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

    private AlignmentCandidate FindBestSuffixCandidate(
        IReadOnlyList<ChatMessage> tail,
        IReadOnlyList<ParsedChatLine> visibleLines)
    {
        AlignmentCandidate best = AlignmentCandidate.Empty;
        for (int start = 0; start < tail.Count; start++)
        {
            List<double> scores = [];
            int maxPairs = Math.Min(tail.Count - start, visibleLines.Count);
            for (int visibleIndex = 0; visibleIndex < maxPairs; visibleIndex++)
            {
                double score = GetMatchScore(tail[start + visibleIndex], visibleLines[visibleIndex]);
                if (score < _matchThreshold)
                {
                    break;
                }

                scores.Add(score);
            }

            if (scores.Count == 0)
            {
                continue;
            }

            double average = scores.Average();
            if (scores.Count > best.MatchedCount ||
                scores.Count == best.MatchedCount && average > best.AverageScore)
            {
                best = new AlignmentCandidate(start, scores.Count, scores, average, "suffix-match");
            }
        }

        return best;
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

    private sealed record AlignmentCandidate(
        int TailStartIndex,
        int MatchedCount,
        IReadOnlyList<double> Scores,
        double AverageScore,
        string Reason)
    {
        public static AlignmentCandidate Empty { get; } = new(-1, 0, Array.Empty<double>(), 0, "none");
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
}

public sealed record TimelineAlignmentMatch(
    ChatMessage Message,
    ParsedChatLine Line,
    double Score);
