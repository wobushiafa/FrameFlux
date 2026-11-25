using System.Diagnostics;
using SIPSorcery.Net;

namespace FrameFlux.WebRtc;

/// <summary>
/// Detects RTP sequence number gaps, dispatches RFC 4585 Generic NACK retransmission requests,
/// and manages rate-limited Picture Loss Indication (PLI) to eliminate video macroblocks and tearing.
/// </summary>
public sealed class WebRtcRtpLossDetector
{
    private readonly Action<RTCPFeedback> _sendRtcpFeedback;
    private readonly Action _requestKeyFrame;
    private readonly object _sync = new();

    private bool _initialized;
    private ushort _lastSeqNum;
    private uint _senderSsrc;
    private uint _mediaSsrc;
    private long _lastPliTimestampMs;
    private long _packetCount;
    private long _lostPacketCount;

    /// <summary>
    /// Minimum interval between consecutive RTCP PLI requests in milliseconds.
    /// </summary>
    public int MinPliIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Gets the total number of processed RTP packets.
    /// </summary>
    public long PacketCount => Interlocked.Read(ref _packetCount);

    /// <summary>
    /// Gets the total number of detected lost RTP packets.
    /// </summary>
    public long LostPacketCount => Interlocked.Read(ref _lostPacketCount);

    public WebRtcRtpLossDetector(Action<RTCPFeedback> sendRtcpFeedback, Action requestKeyFrame)
    {
        _sendRtcpFeedback = sendRtcpFeedback ?? throw new ArgumentNullException(nameof(sendRtcpFeedback));
        _requestKeyFrame = requestKeyFrame ?? throw new ArgumentNullException(nameof(requestKeyFrame));
    }

    /// <summary>
    /// Processes an incoming RTP packet sequence number.
    /// </summary>
    public bool ProcessRtpPacket(uint senderSsrc, uint mediaSsrc, ushort seqNum)
    {
        Interlocked.Increment(ref _packetCount);

        var sendNack = false;
        ushort firstMissing = 0;
        var missingCount = 0;
        uint nackSenderSsrc = 0;
        uint nackMediaSsrc = 0;
        var shouldRequestKeyFrame = false;
        var lossDetected = false;

        lock (_sync)
        {
            var mediaSourceChanged = _initialized &&
                mediaSsrc != 0 &&
                _mediaSsrc != 0 &&
                mediaSsrc != _mediaSsrc;

            _senderSsrc = senderSsrc != 0 ? senderSsrc : _senderSsrc;
            _mediaSsrc = mediaSsrc != 0 ? mediaSsrc : _mediaSsrc;

            if (mediaSourceChanged)
            {
                _lastSeqNum = seqNum;
                return false;
            }

            if (!_initialized)
            {
                _lastSeqNum = seqNum;
                _initialized = true;
                return false;
            }

            var diff = (ushort)(seqNum - _lastSeqNum);

            if (diff == 1)
            {
                // Normal sequential progression
                _lastSeqNum = seqNum;
                return false;
            }

            if (diff == 0)
            {
                // Duplicate packet
                return false;
            }

            if (diff < 3000)
            {
                // Packet gap detected: packets from (_lastSeqNum + 1) to (seqNum - 1) were missed.
                missingCount = diff - 1;
                Interlocked.Add(ref _lostPacketCount, missingCount);

                firstMissing = (ushort)(_lastSeqNum + 1);
                nackSenderSsrc = _senderSsrc;
                nackMediaSsrc = _mediaSsrc;
                sendNack = true;
                _lastSeqNum = seqNum;
                shouldRequestKeyFrame = TryAcquirePliSlotLocked();
                lossDetected = true;
            }
            else if (diff > 60000)
            {
                // Late retransmission or out-of-order packet arrived, ignore for advancing _lastSeqNum
                return false;
            }
            else
            {
                // Severe sequence discontinuity (e.g. SSRC reset or link reset)
                _lastSeqNum = seqNum;
                shouldRequestKeyFrame = TryAcquirePliSlotLocked();
                lossDetected = true;
            }
        }

        if (sendNack)
        {
            SendNackBatch(nackSenderSsrc, nackMediaSsrc, firstMissing, missingCount);
        }

        if (shouldRequestKeyFrame)
        {
            _requestKeyFrame();
        }

        return lossDetected;
    }

    /// <summary>
    /// Requests a keyframe (PLI) with rate limiting.
    /// </summary>
    public void RequestKeyFrameRateLimited()
    {
        bool shouldSend;
        lock (_sync)
        {
            shouldSend = TryAcquirePliSlotLocked();
        }

        if (shouldSend)
        {
            _requestKeyFrame();
        }
    }

    private bool TryAcquirePliSlotLocked()
    {
        var nowMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        if (nowMs - _lastPliTimestampMs < MinPliIntervalMs)
        {
            return false;
        }

        _lastPliTimestampMs = nowMs;
        return true;
    }

    /// <summary>
    /// Dispatches RFC 4585 Generic NACK feedback packets for a range of missing sequence numbers.
    /// </summary>
    private void SendNackBatch(uint senderSsrc, uint mediaSsrc, ushort firstMissingSeq, int count)
    {
        var currentFirst = firstMissingSeq;
        var remaining = count;

        while (remaining > 0)
        {
            ushort bitmask = 0;
            var inBatch = Math.Min(remaining - 1, 16);

            for (var bit = 0; bit < inBatch; bit++)
            {
                bitmask |= (ushort)(1 << bit);
            }

            try
            {
                var nack = new RTCPFeedback(
                    senderSsrc,
                    mediaSsrc,
                    RTCPFeedbackTypesEnum.NACK,
                    currentFirst,
                    bitmask);

                _sendRtcpFeedback(nack);
            }
            catch
            {
                // Transport might not be ready or closing
            }

            remaining -= (1 + inBatch);
            currentFirst = (ushort)(currentFirst + 1 + inBatch);
        }
    }

    /// <summary>
    /// Resets the sequence tracking state.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _initialized = false;
            _lastSeqNum = 0;
            _senderSsrc = 0;
            _mediaSsrc = 0;
            _lastPliTimestampMs = 0;
        }
    }
}
