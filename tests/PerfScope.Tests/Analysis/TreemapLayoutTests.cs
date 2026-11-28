using PerfScope.Core.Analysis;

namespace PerfScope.Tests.Analysis;

/// <summary>树图布局的几何契约：面积成比例、互不重叠、不越界、过滤过小项。</summary>
public sealed class TreemapLayoutTests
{
    private const double Epsilon = 1e-9;

    [Fact]
    public void 空输入返回空结果()
    {
        Assert.Empty(TreemapLayout.Compute(Array.Empty<TreemapInput>()));
    }

    [Fact]
    public void 单个项铺满整个区域()
    {
        var rects = TreemapLayout.Compute(new[] { new TreemapInput("a", 42) });

        var rect = Assert.Single(rects);
        Assert.Equal(0, rect.X, Epsilon);
        Assert.Equal(0, rect.Y, Epsilon);
        Assert.Equal(1, rect.Width, Epsilon);
        Assert.Equal(1, rect.Height, Epsilon);
    }

    [Fact]
    public void 面积与权重成正比()
    {
        var inputs = new[]
        {
            new TreemapInput("a", 50),
            new TreemapInput("b", 30),
            new TreemapInput("c", 15),
            new TreemapInput("d", 5),
        };

        var rects = TreemapLayout.Compute(inputs);
        Assert.Equal(4, rects.Count);

        foreach (var rect in rects)
        {
            double expected = inputs.First(i => i.Key == rect.Key).Weight / 100.0;
            Assert.Equal(expected, rect.Width * rect.Height, 1e-6);
        }
    }

    [Fact]
    public void 矩形互不重叠()
    {
        var rects = TreemapLayout.Compute(SampleInputs(23));

        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                double overlapWidth = Math.Min(rects[i].X + rects[i].Width, rects[j].X + rects[j].Width)
                                      - Math.Max(rects[i].X, rects[j].X);
                double overlapHeight = Math.Min(rects[i].Y + rects[i].Height, rects[j].Y + rects[j].Height)
                                       - Math.Max(rects[i].Y, rects[j].Y);
                if (overlapWidth > Epsilon && overlapHeight > Epsilon)
                {
                    Assert.Fail($"矩形 {rects[i].Key} 与 {rects[j].Key} 重叠：{overlapWidth} x {overlapHeight}");
                }
            }
        }
    }

    [Fact]
    public void 所有矩形都在单位正方形内()
    {
        var rects = TreemapLayout.Compute(SampleInputs(40));

        Assert.Equal(40, rects.Count);
        foreach (var rect in rects)
        {
            Assert.True(rect.X >= -1e-6, $"{rect.Key} 左边界越界：{rect.X}");
            Assert.True(rect.Y >= -1e-6, $"{rect.Key} 上边界越界：{rect.Y}");
            Assert.True(rect.X + rect.Width <= 1 + 1e-6, $"{rect.Key} 右边界越界：{rect.X + rect.Width}");
            Assert.True(rect.Y + rect.Height <= 1 + 1e-6, $"{rect.Key} 下边界越界：{rect.Y + rect.Height}");
            Assert.True(rect.Width > 0 && rect.Height > 0, $"{rect.Key} 尺寸非法：{rect.Width}x{rect.Height}");
        }
    }

    [Fact]
    public void 总面积为1()
    {
        var rects = TreemapLayout.Compute(SampleInputs(17));

        double area = rects.Sum(r => r.Width * r.Height);
        Assert.Equal(1.0, area, 1e-6);
    }

    [Fact]
    public void 过滤低于阈值的碎片项()
    {
        var inputs = new[]
        {
            new TreemapInput("big", 1000),
            new TreemapInput("mid", 100),
            new TreemapInput("tiny", 1),
        };

        var rects = TreemapLayout.Compute(inputs, minWeightRatio: 0.01);

        Assert.Equal(2, rects.Count);
        Assert.DoesNotContain(rects, r => r.Key == "tiny");
    }

    [Fact]
    public void 非法权重被跳过()
    {
        var inputs = new[]
        {
            new TreemapInput("ok", 10),
            new TreemapInput("zero", 0),
            new TreemapInput("negative", -5),
            new TreemapInput("nan", double.NaN),
        };

        var rects = TreemapLayout.Compute(inputs);

        var rect = Assert.Single(rects);
        Assert.Equal("ok", rect.Key);
    }

    [Fact]
    public void 长宽比不会过分细长()
    {
        // squarified 的意义就在这里：同等权重下不应出现 1:100 这种条带
        var rects = TreemapLayout.Compute(SampleInputs(30));
        foreach (var rect in rects)
        {
            double ratio = Math.Max(rect.Width / rect.Height, rect.Height / rect.Width);
            Assert.True(ratio < 12, $"{rect.Key} 的长宽比为 {ratio:F1}，过于细长（{rect.Width:F4}x{rect.Height:F4}）");
        }
    }

    private static TreemapInput[] SampleInputs(int count)
    {
        var inputs = new TreemapInput[count];
        for (int i = 0; i < count; i++)
        {
            // 递减权重，模拟真实的应用 CPU 分布
            inputs[i] = new TreemapInput("app" + i, 1000.0 / (i + 1));
        }

        return inputs;
    }
}
