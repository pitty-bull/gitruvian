namespace Gitruvian.Core.Tests;

/// <summary>Layout das raias do grafo.</summary>
public sealed class GraphLayoutTests
{
    private sealed record C(string Sha, params string[] ParentList) : IHasParents
    {
        public IReadOnlyList<string> Parents => ParentList;
    }

    private static HashSet<(int, int)> Edges(GraphRow row, SegmentHalf half) =>
        row.Segments.Where(s => s.Half == half).Select(s => (s.Start, s.End)).ToHashSet();

    private static HashSet<(int, int)> Set(params (int, int)[] items) => [.. items];

    /// <summary>Toda raia que sai pela base de uma linha entra pelo topo da linha seguinte (e vice-versa).</summary>
    private static void CheckContinuity(List<GraphRow> rows)
    {
        foreach (var (upper, lower) in rows.Zip(rows.Skip(1)))
        {
            var bottoms = upper.Segments.Where(s => s.Half == SegmentHalf.Bottom).Select(s => s.End).ToHashSet();
            var tops = lower.Segments.Where(s => s.Half == SegmentHalf.Top).Select(s => s.Start).ToHashSet();
            Assert.True(bottoms.SetEquals(tops), $"{upper.Sha} -> {lower.Sha}");
        }
    }

    [Fact]
    public void LinearHistoryUsesOneColumn()
    {
        var rows = GraphLayout.Layout([new C("c", "b"), new C("b", "a"), new C("a")]);
        Assert.Equal([0, 0, 0], rows.Select(r => r.Column));
        Assert.Empty(Edges(rows[0], SegmentHalf.Top));
        Assert.Equal(Set((0, 0)), Edges(rows[0], SegmentHalf.Bottom));
        Assert.Empty(Edges(rows[2], SegmentHalf.Bottom));
        CheckContinuity(rows);
    }

    [Fact]
    public void BranchAndMerge()
    {
        // m = merge(d, f); d em main; f em feature; ambos saem de a
        var rows = GraphLayout.Layout([new C("m", "d", "f"), new C("f", "e"), new C("d", "a"), new C("e", "a"), new C("a")]);
        var by = rows.ToDictionary(r => r.Sha);
        Assert.True(by["m"].IsMerge);
        Assert.Equal(0, by["m"].Column);
        Assert.Equal(Set((0, 0), (0, 1)), Edges(by["m"], SegmentHalf.Bottom));
        Assert.Equal(1, by["f"].Column);
        Assert.Equal(0, by["d"].Column);
        Assert.Equal(0, by["a"].Column);
        // A raia da feature converge para "a" vindo da coluna 1.
        Assert.Contains((1, 0), Edges(by["a"], SegmentHalf.Top));
        Assert.Equal(2, rows.Max(r => r.Width));
        CheckContinuity(rows);
    }

    [Fact]
    public void TwoRootsAndReusedLane()
    {
        var rows = GraphLayout.Layout([new C("x", "a", "z"), new C("a"), new C("z")]);
        Assert.Equal([0, 0, 1], rows.Select(r => r.Column));
        Assert.Equal(Set((1, 1)), Edges(rows[1], SegmentHalf.Bottom)); // a raia de z continua ao lado da raiz a
        CheckContinuity(rows);
    }

    [Fact]
    public void OctopusMergeOpensOneLanePerParent()
    {
        var rows = GraphLayout.Layout([new C("o", "p1", "p2", "p3"), new C("p3", "r"), new C("p2", "r"), new C("p1", "r"), new C("r")]);
        Assert.Equal(Set((0, 0), (0, 1), (0, 2)), Edges(rows[0], SegmentHalf.Bottom));
        Assert.Equal(0, rows[^1].Column);
        Assert.Equal(Set((0, 0), (1, 0), (2, 0)), Edges(rows[^1], SegmentHalf.Top)); // as três raias convergem na raiz
        CheckContinuity(rows);
    }

    [Fact]
    public void ParentAlreadyInAnotherLaneJoinsIt()
    {
        // b e c são filhos de a; a raia de c já espera "a" quando b é processado.
        var rows = GraphLayout.Layout([new C("c", "a"), new C("b", "a"), new C("a")]);
        Assert.Equal(1, rows[1].Column);
        Assert.Equal(Set((0, 0), (1, 1)), Edges(rows[1], SegmentHalf.Bottom));
        Assert.Equal(Set((0, 0), (1, 0)), Edges(rows[2], SegmentHalf.Top));
        CheckContinuity(rows);
    }

    [Fact]
    public void SegmentsAreHashableValues()
    {
        Assert.Equal(new Segment(0, 1, 2, SegmentHalf.Top), new Segment(0, 1, 2, SegmentHalf.Top));
        Assert.Equal(new Segment(0, 1, 2, SegmentHalf.Top).GetHashCode(), new Segment(0, 1, 2, SegmentHalf.Top).GetHashCode());
    }
}
