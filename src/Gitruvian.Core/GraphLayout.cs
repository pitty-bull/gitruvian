namespace Gitruvian.Core;

public interface IHasParents
{
    string Sha { get; }
    IReadOnlyList<string> Parents { get; }
}

public enum SegmentHalf
{
    /// <summary>Da borda de cima até o meio da linha.</summary>
    Top,

    /// <summary>Do meio até a borda de baixo da linha.</summary>
    Bottom,
}

/// <summary>Trecho de raia: <see cref="Start"/> é a coluna na borda (ou no nó) e <see cref="End"/> a do outro lado.</summary>
public sealed record Segment(int Start, int End, int Color, SegmentHalf Half);

public sealed record GraphRow(string Sha, int Column, int Color, List<Segment> Segments, int Width, bool IsMerge);

/// <summary>
/// Layout do grafo de commits (raias/colunas), sem dependência de interface. Recebe commits em ordem
/// topológica (filhos antes dos pais) e calcula, para cada linha, a coluna do nó e os segmentos a desenhar
/// na metade de cima e na metade de baixo da linha.
/// </summary>
public static class GraphLayout
{
    public static List<GraphRow> Layout(IEnumerable<IHasParents> commits)
    {
        var lanes = new List<string?>();
        var colors = new List<int>();
        var nextColor = 0;
        var rows = new List<GraphRow>();

        int Allocate(string sha)
        {
            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] is null)
                {
                    lanes[i] = sha;
                    colors[i] = nextColor++;
                    return i;
                }
            }
            lanes.Add(sha);
            colors.Add(nextColor++);
            return lanes.Count - 1;
        }

        foreach (var commit in commits)
        {
            var incoming = Enumerable.Range(0, lanes.Count).Where(i => lanes[i] == commit.Sha).ToList();
            var column = incoming.Count > 0 ? incoming[0] : Allocate(commit.Sha);
            var nodeColor = colors[column];
            var segments = new List<Segment>();

            // Metade de cima: raias que passam direto e raias que chegam neste commit.
            for (var i = 0; i < lanes.Count; i++)
            {
                var value = lanes[i];
                if (value is null)
                    continue;
                if (value == commit.Sha)
                {
                    if (incoming.Contains(i))
                        segments.Add(new Segment(i, column, colors[i], SegmentHalf.Top));
                }
                else
                {
                    segments.Add(new Segment(i, i, colors[i], SegmentHalf.Top));
                }
            }
            foreach (var i in incoming.Where(i => i != column))
                lanes[i] = null;

            // Pais: o primeiro herda a coluna do nó (se outra raia já espera o mesmo pai, as duas convergem
            // na linha do pai); os demais reaproveitam uma raia que já espera o pai ou abrem uma nova.
            var targets = new List<int>();
            var parents = commit.Parents.Distinct().ToList();
            if (parents.Count == 0)
                lanes[column] = null;
            for (var n = 0; n < parents.Count; n++)
            {
                var parent = parents[n];
                if (n == 0)
                {
                    lanes[column] = parent;
                    targets.Add(column);
                    continue;
                }
                var existing = lanes.IndexOf(parent);
                targets.Add(existing >= 0 ? existing : Allocate(parent));
            }

            // Metade de baixo: do nó até cada pai e raias que continuam.
            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] is null)
                    continue;
                if (targets.Contains(i))
                {
                    segments.Add(new Segment(column, i, colors[i], SegmentHalf.Bottom));
                    if (i != column && WasPassing(segments, i))
                        segments.Add(new Segment(i, i, colors[i], SegmentHalf.Bottom));
                }
                else
                {
                    segments.Add(new Segment(i, i, colors[i], SegmentHalf.Bottom));
                }
            }

            while (lanes.Count > 0 && lanes[^1] is null)
            {
                lanes.RemoveAt(lanes.Count - 1);
                colors.RemoveAt(colors.Count - 1);
            }
            var width = segments.Select(s => Math.Max(s.Start, s.End) + 1).Append(column + 1).Max();
            rows.Add(new GraphRow(commit.Sha, column, nodeColor, segments, width, parents.Count > 1));
        }
        return rows;
    }

    /// <summary>A raia já passava por esta linha antes do nó (então continua para baixo também).</summary>
    private static bool WasPassing(List<Segment> segments, int col) =>
        segments.Any(s => s.Half == SegmentHalf.Top && s.Start == col && s.End == col);
}
