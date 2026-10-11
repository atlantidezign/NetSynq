namespace NetSynq.Core;

public enum DiffOp { Equal, Insert, Delete }

/// <summary>Diff di righe (algoritmo di Myers) con prefisso/suffisso comuni tagliati per velocità.</summary>
public static class LineDiff
{
    public static List<(DiffOp Op, string Line)>? Compute(string[] a, string[] b, int maxD = 4000)
    {
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;

        var A = a.Skip(pre).Take(a.Length - pre - suf).ToArray();
        var B = b.Skip(pre).Take(b.Length - pre - suf).ToArray();
        var mid = Myers(A, B, maxD);
        if (mid == null) return null;

        var res = new List<(DiffOp, string)>(a.Length + b.Length);
        for (int i = 0; i < pre; i++) res.Add((DiffOp.Equal, a[i]));
        res.AddRange(mid);
        for (int i = a.Length - suf; i < a.Length; i++) res.Add((DiffOp.Equal, a[i]));
        return res;
    }

    private static List<(DiffOp, string)>? Myers(string[] A, string[] B, int maxD)
    {
        int n = A.Length, m = B.Length;
        var result = new List<(DiffOp, string)>();
        if (n == 0 && m == 0) return result;
        int max = Math.Min(n + m, maxD);
        var v = new int[2 * max + 3];
        int off = max + 1;
        var trace = new List<int[]>();
        int found = -1;

        for (int d = 0; d <= max && found < 0; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[off + k - 1] < v[off + k + 1])) x = v[off + k + 1];
                else x = v[off + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && A[x] == B[y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
            var snap = new int[2 * d + 1];
            Array.Copy(v, off - d, snap, 0, 2 * d + 1);
            trace.Add(snap);
        }
        if (found < 0) return null;

        var rev = new List<(DiffOp, string)>();
        int xx = n, yy = m;
        for (int d = found; d > 0; d--)
        {
            var prev = trace[d - 1];
            int k = xx - yy;
            int PV(int kk) => prev[kk + (d - 1)];
            int prevK = (k == -d || (k != d && PV(k - 1) < PV(k + 1))) ? k + 1 : k - 1;
            int prevX = PV(prevK);
            int prevY = prevX - prevK;
            while (xx > prevX && yy > prevY) { rev.Add((DiffOp.Equal, A[xx - 1])); xx--; yy--; }
            if (xx == prevX) { rev.Add((DiffOp.Insert, B[yy - 1])); yy--; }
            else { rev.Add((DiffOp.Delete, A[xx - 1])); xx--; }
        }
        while (xx > 0 && yy > 0) { rev.Add((DiffOp.Equal, A[xx - 1])); xx--; yy--; }
        rev.Reverse();
        return rev;
    }
}
