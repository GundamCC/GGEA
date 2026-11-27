using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GGEA.App.Tasks;

/// <summary>在第一次消费点击前记下设备、栏位及委托。游戏已扣费但结果丢失时，重跑仍可查库存交付。</summary>
internal sealed class DeliveryProductionAttempt(string serial, int slot, DeliveryOffer offer, long refresh,
    string directory)
{
    internal static long RefreshPeriod => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (8 * 3600);
    internal static DeliveryProductionAttempt Start(string serial, int slot, DeliveryOffer offer) =>
        new(serial, slot, offer, RefreshPeriod, Path.Combine(AppContext.BaseDirectory, "logs", "character-requests"));

    internal bool Reserve()
    {
        if (refresh != RefreshPeriod) return false;
        var device = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial)));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{device}.{slot}.txt");
        var entry = FormattableString.Invariant($"{refresh}/{offer.RoleId}/{offer.Number}/{offer.UnitId}");
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var reader = new StreamReader(file, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadToEnd() == entry) return false;
        file.Position = 0;
        file.SetLength(0);
        file.Write(Encoding.UTF8.GetBytes(entry));
        file.Flush(flushToDisk: true);
        return true;
    }
}
