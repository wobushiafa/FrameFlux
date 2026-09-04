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
    public int MinPliIntervalMs { get; set; } = 250;

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
    public void ProcessRtpPacket(uint senderSsrc, uint mediaSsrc, ushort seqNum)
    {
        Interlocked.Increment(ref _packetCount);

        lock (_sync)
        {
            _senderSsrc = senderSsrc != 0 ? senderSsrc : _senderSsrc;
            _mediaSsrc = mediaSsrc != 0 ? mediaSsrc : _mediaSsrc;

            if (!_initialized)
            {
                _lastSeqNum = seqNum;
                _initialized = true;
                return;
            }

            var diff = (ushort)(seqNum - _lastSeqNum);

            if (diff == 1)
            {
                // Normal sequential progression
                _lastSeqNum = seqNum;
                return;
            }

            if (diff == 0)
            {
                // Duplicate packet
                return;
            }

            if (diff < 3000)
            {
                // Packet gap detected: packets from (_lastSeqNum + 1) to (seqNum - 1) were missed.
                var missingCount = diff - 1;
                Interlocked.Add(ref _lostPacketCount, missingCount);

                SendNackBatch((ushort)(_lastSeqNum + 1), missingCount);
                _lastSeqNum = seqNum;

                // If massive packet burst loss, also request an immediate keyframe refresh
                if (missingCount >= 10)
                {
                    RequestKeyFrameRateLimited();
                }
                return;
            }

            if (diff > 60000)
            {
                // Late retransmission or out-of-order packet arrived, ignore for advancing _lastSeqNum
                return;
            }

            // Severe sequence discontinuity (e.g. SSRC reset or link reset)
            _lastSeqNum = seqNum;
            RequestKeyFrameRateLimited();
        }
    }

    /// <summary>
    /// Requests a keyframe (PLI) with rate limiting.
    /// </summary>
    public void RequestKeyFrameRateLimited()
    {
        var nowMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        lock (_sync)
        {
            if (nowMs - _lastPliTimestampMs < MinPliIntervalMs)
            {
                return;
            }
            _lastPliTimestampMs = nowMs;
        }

        _requestKeyFrame();
    }

    /// <summary>
    /// Dispatches RFC 4585 Generic NACK feedback packets for a range of missing sequence numbers.
    /// </summary>
    private void SendNackBatch(ushort firstMissingSeq, int count)
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
                    _senderSsrc,
                    _mediaSsrc,
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
