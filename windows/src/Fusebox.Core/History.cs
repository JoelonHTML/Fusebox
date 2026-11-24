namespace Fusebox.Core;

/// <summary>Fixed-size ring buffer of samples with min/avg/max. One per measurement; no allocation per sample.</summary>
public sealed class History(int capacity)
{
    readonly double[] values = new double[capacity];
    int start, count;

    public int Count => count;
    public int Capacity => capacity;

    public void Add(double v)
    {
        if (double.IsNaN(v)) return;
        if (count < capacity) values[(start + count++) % capacity] = v;
        else { values[start] = v; start = (start + 1) % capacity; }
    }

    public double this[int i] => values[(start + i) % capacity];
    public double Last => count == 0 ? double.NaN : this[count - 1];
    public double Min { get { double m = double.MaxValue; for (int i = 0; i < count; i++) m = Math.Min(m, this[i]); return count == 0 ? double.NaN : m; } }
    public double Max { get { double m = double.MinValue; for (int i = 0; i < count; i++) m = Math.Max(m, this[i]); return count == 0 ? double.NaN : m; } }
    public double Average { get { double s = 0; for (int i = 0; i < count; i++) s += this[i]; return count == 0 ? double.NaN : s / count; } }

    /// <summary>Last n values, oldest first (for graphs).</summary>
    public IEnumerable<double> Tail(int n) { for (int i = Math.Max(0, count - n); i < count; i++) yield return this[i]; }
}
