using System.Security.Cryptography;
using System.Text;

namespace GGEA.App.UI;

/// <summary>由 UI 线程持有的设备执行互斥门；拒绝重入，租约释放前不接受下一项操作。</summary>
internal sealed class DeviceExecutionGate
{
    public bool IsHeld { get; private set; }

    /// <summary>同步取得所有权，不等待旧任务，以免停止后自动执行过时的点击。</summary>
    public Lease? TryEnter(Action released)
    {
        if (IsHeld) return null;
        IsHeld = true;
        return new Lease(this, released);
    }

    /// <summary>只有取得执行权的调用者可释放；重复 Dispose 不影响后续会话。</summary>
    internal sealed class Lease(DeviceExecutionGate owner, Action released) : IDisposable
    {
        private bool _disposed;

        private Mutex? _address;
        private Mutex? _boot;

        /// <summary>地址用于保护同一 ADB 目标；启动 ID 让同一模拟器的不同 ADB 别名也互斥。</summary>
        internal bool TryClaimDevice(string serial, Guid bootId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_address is not null) throw new InvalidOperationException("执行期间不能重复绑定设备。");
            var address = TryCreateMarker("serial-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial))));
            if (address is null) return false;
            try
            {
                if (bootId != Guid.Empty)
                {
                    _boot = TryCreateMarker("boot-" + bootId.ToString("N"));
                    if (_boot is null) return false;
                }
                _address = address;
                return true;
            }
            finally
            {
                if (_address is null) address.Dispose();
            }
        }

        // 只用命名对象的原子创建与句柄生命周期，不取得线程所有权；异步收尾和进程退出均可释放。
        private static Mutex? TryCreateMarker(string key)
        {
            var marker = new Mutex(false, @"Local\GGEA.DeviceTask." + key, out var created);
            if (created) return marker;
            marker.Dispose();
            return null;
        }

        /// <summary>业务 finally 完成后解除所有权并通知界面刷新。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _boot?.Dispose();
            _address?.Dispose();
            owner.IsHeld = false;
            released();
        }
    }
}
