using PerfScope.Core.Models;

namespace PerfScope.Core.Sampling;

/// <summary>
/// 「自驱动」的帧合成器：先调用 <c>ICapture</c> 取原始采样，再合成一帧。
/// </summary>
/// <remarks>
/// <see cref="PerfScope.Core.Abstractions.ISnapshotComposer"/> 只接受外部传入的原始采样，
/// 而 <see cref="PerfScope.Core.Abstractions.IMonitorEngine"/> 的构造参数里没有 <c>ICapture</c>，
/// 因此由 <see cref="SnapshotComposer"/> 额外实现本接口，供引擎驱动采样。
/// </remarks>
public interface ISnapshotProducer
{
    /// <summary>采集并合成一帧。首帧（无基线）或时间源异常时返回 <c>null</c>。</summary>
    SnapshotFrame? Compose();
}
