namespace PerfScope.Core.Analysis;

/// <summary>树图中的一个输入项：稳定键 + 权重（CPU 百分比或字节数）。</summary>
/// <param name="Key">稳定键（应用的归并键），用于把布局结果映射回数据行。</param>
/// <param name="Weight">权重，必须 &gt; 0；权重越大，分配到的面积越大。</param>
public readonly record struct TreemapInput(string Key, double Weight);

/// <summary>树图布局结果：归一化到 0~1 的矩形（左上角 + 宽高）。</summary>
/// <param name="Key">对应的输入键。</param>
/// <param name="X">左边界（0~1）。</param>
/// <param name="Y">上边界（0~1）。</param>
/// <param name="Width">宽度（0~1）。</param>
/// <param name="Height">高度（0~1）。</param>
public readonly record struct TreemapRect(string Key, double X, double Y, double Width, double Height);

/// <summary>
/// Squarified 树图布局（Bruls 等人的算法）：按权重把单位正方形切成面积近似相等的矩形，
/// 尽量让每个矩形接近正方形，避免出现又细又长的条带。
/// 纯几何计算，不依赖任何 UI 框架 —— 视图层只负责把 0~1 的矩形映射到像素。
/// </summary>
public static class TreemapLayout
{
    /// <summary>
    /// 计算布局。<paramref name="inputs"/> 中权重 &lt;= 0 的项会被跳过；
    /// 返回的矩形按输入顺序输出，总面积为 1（可能有极小的浮点误差）。
    /// </summary>
    /// <param name="inputs">输入项（调用方通常已按权重降序排好）。</param>
    /// <param name="minWeightRatio">低于「最大权重 × 该比例」的项直接被丢弃，避免出现像素级碎片。0 表示不过滤。</param>
    public static IReadOnlyList<TreemapRect> Compute(IReadOnlyList<TreemapInput> inputs, double minWeightRatio = 0)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var items = new List<TreemapInput>(inputs.Count);
        double max = 0;
        foreach (var input in inputs)
        {
            if (input.Weight > 0 && double.IsFinite(input.Weight))
            {
                max = Math.Max(max, input.Weight);
                items.Add(input);
            }
        }

        if (items.Count == 0)
        {
            return Array.Empty<TreemapRect>();
        }

        if (minWeightRatio > 0)
        {
            double threshold = max * Math.Clamp(minWeightRatio, 0, 1);
            items.RemoveAll(item => item.Weight < threshold);
            if (items.Count == 0)
            {
                return Array.Empty<TreemapRect>();
            }
        }

        // 权重降序：squarified 算法要求从大到小贪心放置
        items.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));

        double total = 0;
        foreach (var item in items)
        {
            total += item.Weight;
        }

        var result = new List<TreemapRect>(items.Count);
        var frame = new Bounds(0, 0, 1, 1);
        int start = 0;

        while (start < items.Count)
        {
            // 剩余区域的长边决定当前行能铺多宽
            double shortest = Math.Min(frame.Width, frame.Height);
            if (shortest <= 1e-9)
            {
                break;
            }

            // 逐项加入当前行，直到「再加一个」让最长边的宽高比变差为止
            double rowWeight = 0;
            int end = start;
            double worstBefore = double.MaxValue;
            while (end < items.Count)
            {
                double next = rowWeight + items[end].Weight;
                double worst = WorstAspectRatio(items, start, end + 1, next, total, shortest);
                if (end > start && worst > worstBefore)
                {
                    break;
                }

                worstBefore = worst;
                rowWeight = next;
                end++;
            }

            // 把这一行铺到 frame 的一条边上
            PlaceRow(items, start, end, rowWeight, total, shortest, ref frame, result);
            start = end;
        }

        return result;
    }

    private static double WorstAspectRatio(List<TreemapInput> items, int start, int end, double rowWeight,
        double total, double shortest)
    {
        if (rowWeight <= 0)
        {
            return double.MaxValue;
        }

        // 该行占用的面积与边长
        double rowArea = rowWeight / total;
        double rowThickness = rowArea / shortest;

        double min = double.MaxValue;
        double max = 0;
        for (int i = start; i < end; i++)
        {
            double side = items[i].Weight / rowWeight * shortest;
            min = Math.Min(min, side);
            max = Math.Max(max, side);
        }

        if (rowThickness <= 0 || min <= 0)
        {
            return double.MaxValue;
        }

        return Math.Max(max / rowThickness, rowThickness / min);
    }

    private static void PlaceRow(List<TreemapInput> items, int start, int end, double rowWeight, double total,
        double shortest, ref Bounds frame, List<TreemapRect> result)
    {
        double rowArea = rowWeight / total;
        bool horizontal = frame.Width >= frame.Height;   // 长边在水平方向 → 这一行竖着铺（占据左侧一条）
        double rowThickness = rowArea / shortest;

        double offset = 0;
        for (int i = start; i < end; i++)
        {
            double fraction = items[i].Weight / rowWeight;
            double side = fraction * shortest;
            if (horizontal)
            {
                result.Add(new TreemapRect(items[i].Key, frame.X, frame.Y + offset, rowThickness, side));
            }
            else
            {
                result.Add(new TreemapRect(items[i].Key, frame.X + offset, frame.Y, side, rowThickness));
            }

            offset += side;
        }

        // 剩余区域收缩
        if (horizontal)
        {
            frame = new Bounds(frame.X + rowThickness, frame.Y, frame.Width - rowThickness, frame.Height);
        }
        else
        {
            frame = new Bounds(frame.X, frame.Y + rowThickness, frame.Width, frame.Height - rowThickness);
        }
    }

    private readonly record struct Bounds(double X, double Y, double Width, double Height);
}
