using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Packs Mesh-based AS containers into the convex XZ footprint of a hex mesh.
/// Assumptions: the hex mesh is convex in XZ; each AS has MeshFilters and/or
/// SkinnedMeshRenderers beneath its root; the roots are independent (not nested).
/// +Z in hex local coordinates is the far side. World Y of every AS root is preserved.
/// </summary>
public sealed class HexASLayout : MonoBehaviour
{
    [Header("Hex geometry")]
    [SerializeField] private MeshFilter hexMesh;
    [Min(0f)] [SerializeField] private float boundaryMargin = 0.10f;

    [Header("Spacing (hex local units)")]
    [Min(0f)] [SerializeField] private float columnGap = 0.20f;
    [Min(0f)] [SerializeField] private float rowGap = 0.25f;
    [Range(0.01f, 1f)] [SerializeField] private float minUniformScale = 0.35f;

    [Header("Two AS diagonal arrangement")]
    [Range(0f, 1f)] [SerializeField] private float twoItemSideOffset = 0.65f;

    [Header("Organic displacement")]
    [Range(0f, 0.5f)] [SerializeField] private float jitterFraction = 0.12f;
    [Min(1)] [SerializeField] private int jitterAttempts = 24;
    [SerializeField] private int randomSeed = 12345;

    [Header("Diagnostics")]
    [SerializeField] private bool logChosenProfile = false;

    private readonly Dictionary<Transform, Vector3> originalScales = new Dictionary<Transform, Vector3>();
    private const float Eps = 0.0001f;

    private sealed class Item
    {
        public Transform Root;
        public Vector3 OriginalScale;
        public Vector3 PreviousScale;
        public Vector3 PreviousPosition;
        // Local-hex footprint relative to the AS root, measured at OriginalScale.
        public Rect Footprint;
        public float ScaleKey;
        public float X, Z;
    }

    private sealed class Row
    {
        public readonly List<Item> Items = new List<Item>();
        public float Depth;
        public float CenterZ;
    }

    private sealed class Layout
    {
        public List<Row> Rows;
        public float Factor;
        public int Priority;
    }

    /// <summary>
    /// Moves the supplied independent AS root transforms and uniformly scales them
    /// relative to the original scale recorded on their first call. Returns false
    /// without changing their state when no valid layout can be found.
    /// </summary>
    public bool Arrange(IReadOnlyList<Transform> objects)
    {
        if (objects == null) { Debug.LogError("HexASLayout: objects is null.", this); return false; }
        if (objects.Count == 0) return true;
        if (hexMesh == null) hexMesh = GetComponent<MeshFilter>();
        if (hexMesh == null || hexMesh.sharedMesh == null)
        {
            Debug.LogError("HexASLayout: assign the hex MeshFilter.", this);
            return false;
        }

        Matrix4x4 projection = hexMesh.transform.worldToLocalMatrix;
        if (Mathf.Abs(projection.m00 * projection.m22 - projection.m02 * projection.m20) < 1e-7f)
        {
            Debug.LogError("HexASLayout: hex XZ plane has a singular horizontal projection; " +
                           "cannot preserve world Y and freely position AS in local XZ.", this);
            return false;
        }

        List<Vector2> hull = GetConvexHexFootprint();
        if (hull.Count < 3 || Mathf.Abs(PolygonArea(hull)) < Eps)
        {
            Debug.LogError("HexASLayout: hex mesh has no usable XZ footprint.", this);
            return false;
        }
        Vector2 center = PolygonCentroid(hull);
        var items = new List<Item>(objects.Count);
        var unique = new HashSet<Transform>();
        foreach (Transform root in objects)
        {
            if (root == null || !unique.Add(root))
            {
                Debug.LogError("HexASLayout: null or duplicate AS root.", this);
                return false;
            }
            items.Add(new Item { Root = root, PreviousPosition = root.position,
                                 PreviousScale = root.localScale });
        }
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
                if (items[i].Root.IsChildOf(items[j].Root) || items[j].Root.IsChildOf(items[i].Root))
                {
                    Debug.LogError("HexASLayout: AS root transforms must not be nested.", this);
                    return false;
                }

        // Restore each root's original scale only while measuring its base bounds.
        // Undo all temporary changes before searching / on every failure.
        foreach (Item item in items)
        {
            if (!originalScales.TryGetValue(item.Root, out item.OriginalScale))
            {
                item.OriginalScale = item.PreviousScale;
                originalScales.Add(item.Root, item.OriginalScale);
            }
            item.Root.localScale = item.OriginalScale;
            bool measured = TryGetFootprint(item.Root, out item.Footprint);
            // Compare actual original world scale, including parents, not mesh height.
            Vector3 ls = item.Root.lossyScale;
            item.ScaleKey = Mathf.Pow(Mathf.Abs(ls.x * ls.y * ls.z), 1f / 3f);
            item.Root.localScale = item.PreviousScale;
            if (!measured || item.Footprint.width < Eps || item.Footprint.height < Eps)
            {
                Debug.LogError("HexASLayout: AS has no usable mesh bounds: " + item.Root.name, item.Root);
                return false;
            }
        }

        // Shape has priority over size for N <= 12: try to shrink the
        // PRESET before considering any alternative profile. This prevents
        // 7 objects from turning into 7 single-object rows just to keep scale 1.
        items.Sort((a, b) =>
        {
            int byScale = b.ScaleKey.CompareTo(a.ScaleKey);
            if (byScale != 0) return byScale;
            return (b.Footprint.width * b.Footprint.height)
                .CompareTo(a.Footprint.width * a.Footprint.height);
        });

        List<int[]> profiles = CandidateProfiles(items.Count);
        float lowerLimit = Mathf.Clamp(minUniformScale, 0.01f, 1f);
        Layout best = null;

        if (items.Count <= 12)
        {
            // Only proceed to an alternative if the preset cannot fit even
            // at the minimum allowed shared scale.
            for (int p = 0; p < profiles.Count; p++)
            {
                Layout feasible = FindLargestLayoutForProfile(
                    items, profiles[p], hull, center, lowerLimit);
                if (feasible == null) continue;

                feasible.Priority = p;
                best = feasible;
                if (p != 0)
                    Debug.LogWarning(
                        "HexASLayout: preferred profile " + ProfileName(profiles[0]) +
                        " could not fit at the minimum uniform scale; using " +
                        ProfileName(profiles[p]) + " instead.", this);
                break;
            }
        }
        else
        {
            // For a large automatically generated set, maximize the common
            // scale; candidate order is the silhouette tie-breaker.
            for (int p = 0; p < profiles.Count; p++)
            {
                Layout feasible = FindLargestLayoutForProfile(
                    items, profiles[p], hull, center, lowerLimit);
                if (feasible == null) continue;

                feasible.Priority = p;
                if (best == null || feasible.Factor > best.Factor + Eps ||
                    (Mathf.Abs(feasible.Factor - best.Factor) <= Eps && p < best.Priority))
                    best = feasible;
            }
        }
        if (best == null)
        {
            Debug.LogWarning("HexASLayout: no valid arrangement above minUniformScale. " +
                             "Increase hex size, lower spacing/margin or minUniformScale.", this);
            return false;
        }

        if (logChosenProfile)
            Debug.Log("HexASLayout: profile " + ProfileName(profiles[best.Priority]) +
                      ", uniform scale factor " + best.Factor.ToString("F3") + ".", this);

        // Jitter a copy of the finished layout, retaining only valid proposals.
        var random = new System.Random(randomSeed);
        foreach (Row row in best.Rows)
            foreach (Item item in row.Items)
            {
                float baseX = item.X, baseZ = item.Z;
                float jx = Mathf.Min(item.Footprint.width * best.Factor * jitterFraction,
                                    columnGap * 0.35f);
                float jz = Mathf.Min(item.Footprint.height * best.Factor * jitterFraction,
                                    rowGap * 0.35f);
                if (items.Count == 2)
                {
                    // Do not let random movement undo the deliberately opposite
                    // left/right placement (or put either AS across the center).
                    float footprintCenterX = item.X + item.Footprint.center.x * best.Factor;
                    jx = Mathf.Min(jx, Mathf.Abs(footprintCenterX - center.x) * 0.25f);
                }
                // With no requested gap the displacement may remain zero: this is
                // intentional, as it avoids silently sacrificing separation.
                for (int attempt = 0; attempt < jitterAttempts; attempt++)
                {
                    float dx = (float)(random.NextDouble() * 2.0 - 1.0) * jx;
                    float dz = (float)(random.NextDouble() * 2.0 - 1.0) * jz;
                    item.X = baseX + dx;
                    item.Z = baseZ + dz;
                    if (ValidItem(item, best.Factor, items, hull)) break;
                    item.X = baseX;
                    item.Z = baseZ;
                }
            }

        // Only NOW mutate the scene. Preserve each root's original world Y.
        foreach (Item item in items)
        {
            item.Root.localScale = item.OriginalScale * best.Factor;
            Vector3 target = WorldPointAtLocalXZKeepingWorldY(item.PreviousPosition,
                                                               item.X, item.Z);
            item.Root.position = target;
        }
        return true;
    }

    private Layout TryPack(List<Item> sorted, int[] profile, float factor,
                           List<Vector2> hull, Vector2 center)
    {
        if (factor <= 0f) return null;
        var rows = new List<Row>(profile.Length);
        int index = 0;
        foreach (int capacity in profile)
        {
            if (capacity <= 0 || index + capacity > sorted.Count) return null;
            var row = new Row();
            for (int j = 0; j < capacity; j++)
            {
                Item item = sorted[index++];
                row.Items.Add(item);
                row.Depth = Mathf.Max(row.Depth, item.Footprint.height * factor);
            }
            rows.Add(row);
        }
        if (index != sorted.Count) return null;

        float totalDepth = (rows.Count - 1) * rowGap;
        foreach (Row row in rows) totalDepth += row.Depth;
        float zTop = center.y + totalDepth * 0.5f;
        foreach (Row row in rows)
        {
            row.CenterZ = zTop - row.Depth * 0.5f;
            zTop -= row.Depth + rowGap;
            float width = Mathf.Max(0, row.Items.Count - 1) * columnGap;
            foreach (Item item in row.Items) width += item.Footprint.width * factor;
            float cursor = center.x - width * 0.5f;
            foreach (Item item in row.Items)
            {
                // Compensate non-centered mesh pivots.
                item.X = cursor - item.Footprint.xMin * factor;
                item.Z = row.CenterZ - item.Footprint.center.y * factor;
                cursor += item.Footprint.width * factor + columnGap;
            }
        }
        // For exactly two systems: one AS per row, far one toward +X and
        // near one toward -X, like two opposite corners of the hex.
        // A safe maximum side shift is found against the actual mesh outline.
        if (sorted.Count == 2 && profile.Length == 2 &&
            profile[0] == 1 && profile[1] == 1)
        {
            if (!ArrangeTwoOpposite(rows[0].Items[0], rows[1].Items[0],
                                    factor, hull))
                return null;
        }

        foreach (Item item in sorted)
            if (!ValidItem(item, factor, sorted, hull)) return null;
        return new Layout { Rows = rows, Factor = factor };
    }

    /// <summary>
    /// Two separate rows: the larger AS has already been assigned to the far
    /// (+Z) row, so place it to the right (+X); place the near row to the left.
    /// The amount of lateral offset is relative to the maximum allowed by the
    /// REAL convex mesh footprint, not the mesh's rectangular bounds.
    /// </summary>
    private bool ArrangeTwoOpposite(Item far, Item near, float factor,
                                    List<Vector2> hull)
    {
        if (!FitsInsideHex(far, factor, hull) ||
            !FitsInsideHex(near, factor, hull))
            return false;

        far.X += MaxSafeSideShift(far, factor, hull, +1f) * twoItemSideOffset;
        near.X -= MaxSafeSideShift(near, factor, hull, -1f) * twoItemSideOffset;
        return true;
    }

    private float MaxSafeSideShift(Item item, float factor,
                                   List<Vector2> hull, float direction)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        foreach (Vector2 vertex in hull)
        {
            minX = Mathf.Min(minX, vertex.x);
            maxX = Mathf.Max(maxX, vertex.x);
        }
        float low = 0f;
        float high = maxX - minX;
        float initialX = item.X;

        // Starting position is known to be inside. Convexity guarantees that
        // feasible horizontal offsets form a continuous interval.
        for (int attempt = 0; attempt < 24; attempt++)
        {
            float mid = (low + high) * 0.5f;
            item.X = initialX + direction * mid;
            if (FitsInsideHex(item, factor, hull)) low = mid;
            else high = mid;
        }
        item.X = initialX;
        return low;
    }

    private bool FitsInsideHex(Item item, float factor, List<Vector2> hull)
    {
        Rect rect = At(item, factor);
        return InsideConvexWithMargin(new Vector2(rect.xMin, rect.yMin), hull, boundaryMargin) &&
               InsideConvexWithMargin(new Vector2(rect.xMin, rect.yMax), hull, boundaryMargin) &&
               InsideConvexWithMargin(new Vector2(rect.xMax, rect.yMin), hull, boundaryMargin) &&
               InsideConvexWithMargin(new Vector2(rect.xMax, rect.yMax), hull, boundaryMargin);
    }

    private bool ValidItem(Item item, float factor, List<Item> all, List<Vector2> hull)
    {
        Rect a = At(item, factor);
        var corners = new[] { new Vector2(a.xMin, a.yMin), new Vector2(a.xMin, a.yMax),
                              new Vector2(a.xMax, a.yMin), new Vector2(a.xMax, a.yMax) };
        foreach (Vector2 corner in corners)
            if (!InsideConvexWithMargin(corner, hull, boundaryMargin)) return false;

        float clearance = Mathf.Min(columnGap, rowGap) * 0.10f;
        foreach (Item other in all)
        {
            if (ReferenceEquals(item, other)) continue;
            Rect b = At(other, factor);
            // Touching counts as overlap when clearance > 0.
            if (a.xMin < b.xMax + clearance - Eps && a.xMax > b.xMin - clearance + Eps &&
                a.yMin < b.yMax + clearance - Eps && a.yMax > b.yMin - clearance + Eps)
                return false;
        }
        return true;
    }

    private static Rect At(Item item, float factor)
    {
        return new Rect(item.X + item.Footprint.xMin * factor,
                        item.Z + item.Footprint.yMin * factor,
                        item.Footprint.width * factor,
                        item.Footprint.height * factor);
    }

    // Reads each mesh's local Bounds rather than Renderer.bounds. Eight corners are
    // transformed through the exact hierarchy into hex local space. Using AABBs in
    // that plane is conservative for rotated AS meshes and nonuniform scaling.
    private bool TryGetFootprint(Transform root, out Rect footprint)
    {
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        bool found = false;
        Vector3 rootInHex = hexMesh.transform.InverseTransformPoint(root.position);
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>())
        {
            if (filter.sharedMesh == null) continue;
            IncludeBounds(filter.sharedMesh.bounds, filter.transform, rootInHex,
                          ref minX, ref maxX, ref minZ, ref maxZ);
            found = true;
        }
        foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            IncludeBounds(skin.localBounds, skin.transform, rootInHex,
                          ref minX, ref maxX, ref minZ, ref maxZ);
            found = true;
        }
        footprint = found ? Rect.MinMaxRect(minX, minZ, maxX, maxZ) : new Rect();
        return found;
    }

    private void IncludeBounds(Bounds bounds, Transform meshTransform, Vector3 rootInHex,
                               ref float minX, ref float maxX,
                               ref float minZ, ref float maxZ)
    {
        Vector3 low = bounds.min, high = bounds.max;
        for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
        {
            Vector3 corner = new Vector3(x == 0 ? low.x : high.x,
                                         y == 0 ? low.y : high.y,
                                         z == 0 ? low.z : high.z);
            Vector3 local = hexMesh.transform.InverseTransformPoint(meshTransform.TransformPoint(corner));
            minX = Mathf.Min(minX, local.x - rootInHex.x);
            maxX = Mathf.Max(maxX, local.x - rootInHex.x);
            minZ = Mathf.Min(minZ, local.z - rootInHex.z);
            maxZ = Mathf.Max(maxZ, local.z - rootInHex.z);
        }
    }

    private List<Vector2> GetConvexHexFootprint()
    {
        Vector3[] vertices = hexMesh.sharedMesh.vertices;
        var projected = new List<Vector2>(vertices.Length);
        foreach (Vector3 vertex in vertices)
        {
            projected.Add(new Vector2(vertex.x, vertex.z));
        }
        projected.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        var lower = new List<Vector2>();
        foreach (Vector2 p in projected)
        {
            while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0f)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }
        var upper = new List<Vector2>();
        for (int i = projected.Count - 1; i >= 0; i--)
        {
            Vector2 p = projected[i];
            while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0f)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }
        if (lower.Count > 0) lower.RemoveAt(lower.Count - 1);
        if (upper.Count > 0) upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper); // CCW polygon
        return lower;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 p) =>
        (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

    private static bool InsideConvexWithMargin(Vector2 p, List<Vector2> hull, float margin)
    {
        for (int i = 0; i < hull.Count; i++)
        {
            Vector2 a = hull[i], b = hull[(i + 1) % hull.Count];
            float length = Vector2.Distance(a, b);
            if (length < Eps) continue;
            if (Cross(a, b, p) < margin * length - Eps) return false;
        }
        return true;
    }

    private static float PolygonArea(List<Vector2> p)
    {
        float twice = 0f;
        for (int i = 0; i < p.Count; i++)
        {
            Vector2 a = p[i], b = p[(i + 1) % p.Count];
            twice += a.x * b.y - b.x * a.y;
        }
        return twice * 0.5f;
    }

    private static Vector2 PolygonCentroid(List<Vector2> p)
    {
        float a2 = 0f, sx = 0f, sy = 0f;
        for (int i = 0; i < p.Count; i++)
        {
            Vector2 a = p[i], b = p[(i + 1) % p.Count];
            float cross = a.x * b.y - b.x * a.y;
            a2 += cross;
            sx += (a.x + b.x) * cross;
            sy += (a.y + b.y) * cross;
        }
        if (Mathf.Abs(a2) < Eps) return Vector2.zero;
        return new Vector2(sx / (3f * a2), sy / (3f * a2));
    }

    private Vector3 WorldPointAtLocalXZKeepingWorldY(Vector3 current, float x, float z)
    {
        Transform hex = hexMesh.transform;
        Vector3 local = hex.InverseTransformPoint(current);
        float deltaX = x - local.x, deltaZ = z - local.z;
        // Solve the hex-local XZ displacement using only world XZ movement,
        // so root.position.y remains EXACTLY unchanged even for a tilted hex.
        Matrix4x4 m = hex.worldToLocalMatrix;
        float det = m.m00 * m.m22 - m.m02 * m.m20;
        if (Mathf.Abs(det) < 1e-7f)
        {
            // Singular horizontal projection; caller should keep hex approximately horizontal.
            return current;
        }
        float wx = (deltaX * m.m22 - deltaZ * m.m02) / det;
        float wz = (deltaZ * m.m00 - deltaX * m.m20) / det;
        return new Vector3(current.x + wx, current.y, current.z + wz);
    }

    /// <summary>
    /// Finds the largest common scale for ONE given profile. The caller controls
    /// profile priority, so a preferred silhouette is not discarded at factor 1.
    /// No scene Transform is mutated by TryPack / this method.
    /// </summary>
    private Layout FindLargestLayoutForProfile(List<Item> items, int[] profile,
                                               List<Vector2> hull, Vector2 center,
                                               float minimumFactor)
    {
        Layout fullSize = TryPack(items, profile, 1f, hull, center);
        if (fullSize != null) return fullSize;

        Layout feasible = TryPack(items, profile, minimumFactor, hull, center);
        if (feasible == null) return null;

        float lo = minimumFactor;
        float hi = 1f;
        for (int iteration = 0; iteration < 22; iteration++)
        {
            float mid = (lo + hi) * 0.5f;
            Layout test = TryPack(items, profile, mid, hull, center);
            if (test != null)
            {
                feasible = test;
                lo = mid;
            }
            else hi = mid;
        }
        return feasible;
    }

    private static string ProfileName(int[] profile)
    {
        return "[" + string.Join(",", Array.ConvertAll(profile, value => value.ToString())) + "]";
    }

    // Presets are listed far (+Z) to near (-Z). For <=12, ALWAYS try the
    // preferred profile at every allowed scale before considering alternatives.
    private static readonly int[][] Presets =
    {
        null,
        new[] { 1 },
        new[] { 1, 1 },
        new[] { 1, 2 },
        new[] { 1, 2, 1 },
        new[] { 1, 3, 1 },
        new[] { 1, 2, 2, 1 },
        new[] { 2, 3, 2 },
        new[] { 2, 4, 2 },
        new[] { 1, 2, 3, 2, 1 },
        new[] { 2, 3, 3, 2 },
        new[] { 1, 3, 3, 3, 1 },
        new[] { 2, 4, 4, 2 }
    };

    private static List<int[]> CandidateProfiles(int count)
    {
        var result = new List<int[]>();
        var keys = new HashSet<string>();
        Action<int[]> add = profile =>
        {
            string key = ProfileName(profile);
            if (keys.Add(key)) result.Add(profile);
        };

        if (count <= 12) add(Presets[count]);
        int maxRows = count <= 12 ? count :
            Mathf.Min(count, Mathf.CeilToInt(2.2f * Mathf.Sqrt(count)) + 3);
        var alternatives = new List<int[]>();
        for (int r = 1; r <= maxRows; r++)
        {
            int[] profile = GenerateProfile(count, r);
            if (keys.Add(ProfileName(profile))) alternatives.Add(profile);
        }

        // Prefer a row count near the preset (or sqrt(N) for large sets), then
        // symmetry and few singleton rows. In particular [1,1,1,1,1,1,1]
        // is an extreme fallback, not the first alternative for seven objects.
        int targetRows = count <= 12 ? Presets[count].Length :
            Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(count)));
        alternatives.Sort((a, b) =>
        {
            float sa = ProfilePenalty(a, targetRows);
            float sb = ProfilePenalty(b, targetRows);
            int comparison = sa.CompareTo(sb);
            return comparison != 0 ? comparison : a.Length.CompareTo(b.Length);
        });
        result.AddRange(alternatives);
        return result;
    }

    private static float ProfilePenalty(int[] profile, int targetRows)
    {
        float score = Mathf.Abs(profile.Length - targetRows) * 10f;
        for (int i = 0; i < profile.Length; i++)
        {
            if (profile[i] == 1 && profile.Length > 1) score += 1.5f;
            score += Mathf.Abs(profile[i] - profile[profile.Length - 1 - i]);
            if (i < profile.Length / 2 && profile[i] > profile[i + 1]) score += 3f;
            if (i >= (profile.Length + 1) / 2 && i > 0 &&
                profile[i] > profile[i - 1]) score += 3f;
        }
        return score;
    }

    private static int[] GenerateProfile(int count, int rowCount)
    {
        var profile = new int[rowCount];
        for (int i = 0; i < rowCount; i++) profile[i] = 1;
        int left = count - rowCount;
        // Symmetric triangle / plateau: middle rows have a larger target weight.
        // Greedy normalized fill also handles odd remainders deterministically.
        while (left-- > 0)
        {
            int best = 0;
            float bestValue = float.PositiveInfinity;
            for (int i = 0; i < rowCount; i++)
            {
                float weight = 1f + Mathf.Min(i, rowCount - 1 - i);
                float score = profile[i] / weight;
                // Among equal ratios, prefer the center; then favor the far side.
                score += Mathf.Abs(i - (rowCount - 1) * 0.5f) * 0.0001f;
                if (score < bestValue) { bestValue = score; best = i; }
            }
            profile[best]++;
        }
        return profile;
    }
}
