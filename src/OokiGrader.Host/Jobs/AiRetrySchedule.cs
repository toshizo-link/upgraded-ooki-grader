using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OokiGrader.Host.Jobs;

internal static class AiRetrySchedule
{
    private static readonly int[] BaseSeconds =
        [30, 120, 600, 1_800, 7_200];

    public static TimeSpan Delay(int attemptCount, string requestKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        var index = Math.Clamp(attemptCount - 1, 0, BaseSeconds.Length - 1);
        var material = Encoding.UTF8.GetBytes(
            $"{requestKey}\n{Math.Max(1, attemptCount)}");
        var hash = SHA256.HashData(material);
        var sample = BinaryPrimitives.ReadUInt32BigEndian(hash);
        var ratio = sample / (double)uint.MaxValue;
        var factor = 0.8 + (ratio * 0.4);
        return TimeSpan.FromMilliseconds(
            Math.Round(BaseSeconds[index] * factor * 1_000));
    }
}
