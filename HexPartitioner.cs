    [DisallowMultipleComponent, RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class HexPartitioner : MonoBehaviour
    {
        public HexPartitionDefinition definition;
        [Range(1, 100)] public int primaryFragmentCount = 6;
        public int seed = 12345;
        [Tooltip("Direct parent of main fragments. Falls back to this transform.")]
        public Transform primaryFragmentsParent;
        [Tooltip("Direct parent of decorative fragments. Falls back to this transform.")]
        public Transform decorativeFragmentsParent;
        public bool roundTopEdges;

        // Retained only to remove output serialized by the previous generator.
        [NonSerialized] private Transform generatedRoot;
        [NonSerialized] private bool sourceWasEnabled;
        [NonSerialized] private List<Mesh> ownedMeshes = new List<Mesh>();
        [NonSerialized] private List<GameObject> generatedObjects = new List<GameObject>();
        [NonSerialized] private List<HexFragment> primaryFragments = new List<HexFragment>();
        [NonSerialized] private List<GameObject> decorativeFragments = new List<GameObject>();
        public Transform GeneratedRoot => generatedRoot ? generatedRoot : (primaryFragmentsParent ? primaryFragmentsParent : transform);
        public int PrimaryFragmentCount => primaryFragmentCount;
        public IReadOnlyList<HexFragment> PrimaryFragments => primaryFragments.AsReadOnly();
        public IReadOnlyList<GameObject> DecorativeFragments => decorativeFragments.AsReadOnly();

        public void Generate() => Generate(primaryFragmentCount);

        public HexPartitionResult Generate(int count)
        {
            if (!definition) throw new InvalidOperationException(name + ": assign a HexPartitionDefinition.");
            definition.Validate();
            var layout = HexLayout.Build(definition, count, seed);
            HexLayout.Validate(layout);
            var mainParent = primaryFragmentsParent ? primaryFragmentsParent : transform;
            var decorParent = decorativeFragmentsParent ? decorativeFragmentsParent : transform;
            ValidateParent(mainParent);
            ValidateParent(decorParent);
            var frame = HexSurfaceFrame.FromMesh(GetComponent<MeshFilter>().sharedMesh);
            var nextObjects = new List<GameObject>();
            var nextMeshes = new List<Mesh>();
            var main = new List<HexFragment>();
            var decor = new List<GameObject>();
            try
            {
                float lift = Mathf.Max(frame.U.magnitude, frame.V.magnitude) * .0005f;
                foreach (var f in layout.fragments)
                {
                    var mesh = roundTopEdges
                        ? HexMeshBuilder.BuildRoundedTop(f, frame, frame.Bottom, frame.Top + lift, definition.topRoundness, definition.topRoundSegments, transform.localToWorldMatrix)
                        : HexMeshBuilder.Build(f.outline, frame, frame.Bottom, frame.Top + lift);
                    nextMeshes.Add(mesh);
                    var parent = f.primary ? mainParent : decorParent;
                    var obj = new GameObject((f.primary ? "Main_" : "Decor_") + f.id);
                    nextObjects.Add(obj);
                    obj.SetActive(false);
                    obj.transform.SetParent(parent, false);
                    AttachMesh(obj, mesh, f.primary ? definition.primaryMaterial : definition.decorationMaterial);
                    if (!f.primary) { decor.Add(obj); continue; }
                    var r = f.label;
                    var quad = new[] { new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax) };
                    var plane = HexMeshBuilder.Build(quad, frame, frame.Top + lift * 2, frame.Top + lift * 2);
                    nextMeshes.Add(plane);
                    var label = new GameObject("TextPlane");
                    label.transform.SetParent(obj.transform, false);
                    AttachMesh(label, plane, definition.labelMaterial);
                    var fragment = obj.AddComponent<HexFragment>();
                    fragment.Initialize(f.id, r, label.transform);
                    main.Add(fragment);
                }
            }
            catch
            {
                foreach (var obj in nextObjects) if (obj) DestroyObject(obj);
                foreach (var mesh in nextMeshes) if (mesh) DestroyObject(mesh);
                throw;
            }
            bool original = generatedRoot || generatedObjects.Count > 0 ? sourceWasEnabled : GetComponent<MeshRenderer>().enabled;
            ClearGeneratedFragments();
            sourceWasEnabled = original;
            generatedObjects = nextObjects;
            ownedMeshes = nextMeshes;
            primaryFragments = main;
            decorativeFragments = decor;
            primaryFragmentCount = count;
            foreach (var obj in nextObjects) obj.SetActive(true);
            GetComponent<MeshRenderer>().enabled = false;
            return new HexPartitionResult(main, decor);
        }

        private void ValidateParent(Transform parent)
        {
            if (Mathf.Abs(parent.localToWorldMatrix.determinant) < 1e-12f)
                throw new InvalidOperationException(parent.name + ": fragment parent has zero scale.");
            if (generatedRoot && parent.IsChildOf(generatedRoot))
                throw new InvalidOperationException(parent.name + ": parent belongs to the previous generated output.");
            foreach (var obj in generatedObjects)
                if (obj && parent.IsChildOf(obj.transform))
                    throw new InvalidOperationException(parent.name + ": parent belongs to the previous generated output.");
        }

        private void AttachMesh(GameObject obj, Mesh mesh, Material material)
        {
            // Bake the source-to-parent matrix into geometry, preserving even nonuniform
            // scale/shear while fragments remain direct children with identity transforms.
            var matrix = obj.transform.worldToLocalMatrix * transform.localToWorldMatrix;
            var normalMatrix = matrix.inverse.transpose;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
                normals[i] = normalMatrix.MultiplyVector(normals[i]).normalized;
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            if (matrix.determinant < 0)
            {
                var triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                { int swap = triangles[i + 1]; triangles[i + 1] = triangles[i + 2]; triangles[i + 2] = swap; }
                mesh.triangles = triangles;
            }
            mesh.RecalculateBounds();
            if (!Application.isPlaying)
            {
                obj.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
                mesh.hideFlags = HideFlags.DontSave;
            }
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            obj.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        public IEnumerable<MeshFilter> GetGeneratedMeshFilters()
        {
            foreach (var obj in generatedObjects)
                if (obj)
                    foreach (var filter in obj.GetComponentsInChildren<MeshFilter>(true)) yield return filter;
        }

        public void ClearGeneratedFragments()
        {
            bool hadOutput = generatedRoot || generatedObjects.Count > 0;
            if (generatedRoot) { generatedRoot.gameObject.SetActive(false); DestroyObject(generatedRoot.gameObject); }
            generatedRoot = null;
            foreach (var obj in generatedObjects) if (obj) { obj.SetActive(false); DestroyObject(obj); }
            generatedObjects = new List<GameObject>();
            primaryFragments = new List<HexFragment>();
            decorativeFragments = new List<GameObject>();
            foreach (var mesh in ownedMeshes) if (mesh) DestroyObject(mesh);
            ownedMeshes.Clear();
            var renderer = GetComponent<MeshRenderer>();
            if (hadOutput && renderer) renderer.enabled = sourceWasEnabled;
        }
        private void OnDestroy() => ClearGeneratedFragments();
        private static void DestroyObject(UnityEngine.Object obj)
        { if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj); }
    }


    public sealed class HexPartitionResult
    {
        public IReadOnlyList<HexFragment> PrimaryFragments { get; }
        public IReadOnlyList<GameObject> DecorativeFragments { get; }

        internal HexPartitionResult(List<HexFragment> primary, List<GameObject> decorative)
        {
            PrimaryFragments = primary.AsReadOnly();
            DecorativeFragments = decorative.AsReadOnly();
        }
    }

    [Serializable] public sealed class FragmentDefinition
    {
        public string id;
        public bool primary;
        public Vector2[] outline;
        public Rect label;
    }
    [Serializable] public sealed class LayoutDefinition
    {
        public int count;
        public FragmentDefinition[] fragments;
    }
    [Serializable] public sealed class LayoutLibrary { public LayoutDefinition[] layouts; }

    [CreateAssetMenu(menuName = "Mesh Generation/Hex Partition Definition")]
    public sealed class HexPartitionDefinition : ScriptableObject
    {
        public LayoutDefinition[] layouts;
        [Range(.02f,.2f)] public float cellGap = .09f;
        [Range(.05f,.4f)] public float cornerRadius = .22f;
        [Range(2,12)] public int cornerSegments = 5;
        [Range(.6f,.95f)] public float boundaryInset = .91f;
        [Range(.1f,.4f)] public float labelPadding = .18f;
        public Material primaryMaterial;
        public Material decorationMaterial;
        public Material labelMaterial;
        [Range(.01f,.2f)] public float topRoundness = .08f;
        [Range(2,12)] public int topRoundSegments = 6;

        public void Validate()
        {
            if (layouts == null || layouts.Length != 12) throw new InvalidOperationException(name + ": expected reference layouts 1–12.");
            var counts = new System.Collections.Generic.HashSet<int>();
            foreach (var layout in layouts)
            {
                if (layout == null || !counts.Add(layout.count)) throw new InvalidOperationException(name + ": duplicate/null layout.");
                HexLayout.Validate(layout);
            }
            if (!primaryMaterial || !decorationMaterial || !labelMaterial)
                throw new InvalidOperationException(name + ": primary, decoration and label materials are required.");
            if (topRoundness <= 0 || topRoundness > .2f || topRoundSegments < 2 || topRoundSegments > 12)
                throw new InvalidOperationException(name + ": invalid top rounding parameters.");
            if (cellGap <= 0 || cellGap >= .3f || cornerRadius <= 0 || cornerRadius > .4f || cornerSegments < 2)
                throw new InvalidOperationException(name + ": invalid shape parameters.");
        }
    }

    public static partial class HexMeshBuilder
    {
        public static Mesh BuildRoundedTop(FragmentDefinition fragment, HexSurfaceFrame frame,
            float bottom, float top, float roundness, int segments, Matrix4x4 sourceToWorld)
        {
            // Round in physical units AFTER source scaling. Otherwise a Y scale flattens
            // the finished quarter-circle and inverse-transpose normals hide its highlight.
            var u = sourceToWorld.MultiplyVector(frame.U);
            var v = sourceToWorld.MultiplyVector(frame.V);
            var extrusion = sourceToWorld.MultiplyVector(frame.Normal);
            var normal = sourceToWorld.inverse.transpose.MultiplyVector(frame.Normal).normalized;
            float heightScale = Vector3.Dot(extrusion, normal);
            if (heightScale <= 0)
                throw new System.InvalidOperationException("Hex source transform must have nonzero thickness scale.");
            var drift = extrusion - normal * heightScale;
            var metricFrame = new HexSurfaceFrame
            {
                Center = sourceToWorld.MultiplyPoint3x4(frame.Center) + drift * top,
                U = u, V = v, Normal = normal
            };
            var mesh = BuildRoundedTop(fragment, metricFrame, bottom * heightScale,
                top * heightScale, roundness, segments);
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var worldToSource = sourceToWorld.inverse;
            var normalToSource = sourceToWorld.transpose;
            // Under a sheared hierarchy preserve the original bottom footprint; only
            // the rounded lid uses the physical normal of the top surface.
            float worldTop = top * heightScale;
            float worldBottom = bottom * heightScale;
            for (int i = 0; i < vertices.Length; i++)
            {
                float height = Vector3.Dot(vertices[i] - metricFrame.Center, normal);
                float t = Mathf.InverseLerp(worldTop, worldBottom, height);
                vertices[i] += drift * (bottom - top) * t;
                // Undo the corresponding shear for normals before returning to source space.
                var unshearedNormal = normals[i];
                normals[i] = (unshearedNormal - normal * Vector3.Dot(drift / heightScale, unshearedNormal)).normalized;
                vertices[i] = worldToSource.MultiplyPoint3x4(vertices[i]);
                normals[i] = normalToSource.MultiplyVector(normals[i]).normalized;
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            if (sourceToWorld.determinant < 0)
            {
                var triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                { int swap = triangles[i + 1]; triangles[i + 1] = triangles[i + 2]; triangles[i + 2] = swap; }
                mesh.triangles = triangles;
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Mesh BuildRoundedTop(FragmentDefinition fragment, HexSurfaceFrame frame,
            float bottom, float top, float roundness, int segments)
        {
            if (top <= bottom) return Build(fragment.outline, frame, bottom, top);
            var x = frame.U.normalized;
            var y = (frame.V - x * Vector3.Dot(frame.V, x)).normalized;
            var p = new List<Vector2>();
            foreach (var point in fragment.outline)
            {
                var position = frame.U * point.x + frame.V * point.y;
                p.Add(new Vector2(Vector3.Dot(position, x), Vector3.Dot(position, y)));
            }
            float area = 0;
            var min = p[0]; var max = p[0];
            for (int i = 0; i < p.Count; i++)
            {
                area += HexLayout.Cross(p[i], p[(i + 1) % p.Count]);
                min = Vector2.Min(min, p[i]); max = Vector2.Max(max, p[i]);
            }
            if (area < 0) p.Reverse();
            float radius = Mathf.Min((top - bottom) * .45f, Mathf.Min(max.x - min.x, max.y - min.y) * roundness);
            var inward = new Vector2[p.Count];
            var turn = new float[p.Count];
            for (int i = 0; i < p.Count; i++)
            {
                var a = (p[i] - p[(i + p.Count - 1) % p.Count]).normalized;
                var b = (p[(i + 1) % p.Count] - p[i]).normalized;
                float denominator = Mathf.Max(.0001f, 1 + Vector2.Dot(a, b));
                inward[i] = (new Vector2(-a.y, a.x) + new Vector2(-b.y, b.x)) / denominator;
                turn[i] = Mathf.Abs(HexLayout.Cross(a, b)) / denominator;
            }
            // Prevent neighbouring offset vertices crossing around small rounded corners.
            for (int i = 0; i < p.Count; i++)
            {
                int j = (i + 1) % p.Count;
                if (turn[i] + turn[j] > .00001f)
                    radius = Mathf.Min(radius, Vector2.Distance(p[i], p[j]) * .45f / (turn[i] + turn[j]));
            }
            if (fragment.primary)
                for (int row = 0; row <= 4; row++)
                    for (int column = 0; column <= 8; column++)
                    {
                        var r = fragment.label;
                        var position = frame.U * (r.xMin + r.width * column / 8) + frame.V * (r.yMin + r.height * row / 4);
                        var labelPoint = new Vector2(Vector3.Dot(position, x), Vector3.Dot(position, y));
                        for (int i = 0; i < p.Count; i++)
                            radius = Mathf.Min(radius, DistanceToSegment(labelPoint, p[i], p[(i + 1) % p.Count]) * .65f);
                    }
            var physicalFrame = new HexSurfaceFrame { Center = frame.Center, U = x, V = y, Normal = frame.Normal };
            bool reverse = Vector3.Dot(Vector3.Cross(x, y), frame.Normal) < 0;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uv = new List<Vector2>();
            var triangles = new List<int>();
            var inner = new List<Vector2>();
            for (int i = 0; i < p.Count; i++) inner.Add(p[i] + inward[i] * radius);

            // Flat top and bottom caps; the top contour is inset, text stays on its flat area.
            for (int i = 0; i < p.Count; i++)
            {
                vertices.Add(physicalFrame.Point(inner[i], top)); normals.Add(frame.Normal); uv.Add(inner[i]);
            }
            var cap = Triangulate(inner);
            for (int i = 0; i < cap.Count; i += 3) Add(triangles, cap[i], cap[i + 1], cap[i + 2], reverse);
            int bottomStart = vertices.Count;
            for (int i = 0; i < p.Count; i++)
            {
                vertices.Add(physicalFrame.Point(p[i], bottom)); normals.Add(-frame.Normal); uv.Add(p[i]);
            }
            cap = Triangulate(p);
            for (int i = 0; i < cap.Count; i += 3) Add(triangles, bottomStart + cap[i], bottomStart + cap[i + 1], bottomStart + cap[i + 2], !reverse);

            // Shared normals make the quarter-circle profile continuous with wall and cap.
            int stripStart = vertices.Count;
            for (int ring = -1; ring <= segments; ring++)
            {
                float angle = Mathf.Max(0, ring) * Mathf.PI * .5f / segments;
                float inset = radius * (1 - Mathf.Cos(angle));
                float height = ring < 0 ? bottom : top - radius + radius * Mathf.Sin(angle);
                for (int i = 0; i < p.Count; i++)
                {
                    var position = p[i] + inward[i] * inset;
                    var direction = -inward[i].normalized;
                    var outward = x * direction.x + y * direction.y;
                    vertices.Add(physicalFrame.Point(position, height));
                    normals.Add((outward * Mathf.Cos(angle) + frame.Normal * Mathf.Sin(angle)).normalized);
                    uv.Add(position);
                }
            }
            for (int ring = 0; ring <= segments; ring++)
                for (int i = 0; i < p.Count; i++)
                {
                    int a = stripStart + ring * p.Count + i;
                    int b = stripStart + ring * p.Count + (i + 1) % p.Count;
                    Add(triangles, a, b, b + p.Count, reverse);
                    Add(triangles, a, b + p.Count, a + p.Count, reverse);
                }
            var mesh = new Mesh { name = "HexPartitionMesh" };
            mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0, uv);
            mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
            return mesh;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var edge = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, edge) / edge.sqrMagnitude);
            return Vector2.Distance(p, a + edge * t);
        }
    }

    public sealed class HexSurfaceFrame
    {
        public Vector3 Center, U, V, Normal;
        public float Bottom, Top;
        public Vector3 Point(Vector2 p,float height)=>Center+U*p.x+V*p.y+Normal*height;

        public static HexSurfaceFrame FromMesh(Mesh mesh)
        {
            if(!mesh||!mesh.isReadable)throw new InvalidOperationException("Hex source mesh is missing or Read/Write is disabled.");
            var b=mesh.bounds;int axis=b.size.x<b.size.y?(b.size.x<b.size.z?0:2):(b.size.y<b.size.z?1:2);
            float areaEpsilon=b.size.sqrMagnitude*1e-8f;
            Vector3 normal=axis==0?Vector3.right:axis==1?Vector3.up:Vector3.forward;
            Vector3 x=axis==0?Vector3.up:Vector3.right;Vector3 y=Vector3.Cross(x,normal);
            var points=new List<Vector2>();float max=float.MinValue,min=float.MaxValue;
            foreach(var p in mesh.vertices){float h=Vector3.Dot(p,normal);max=Mathf.Max(max,h);min=Mathf.Min(min,h);var q=new Vector2(Vector3.Dot(p,x),Vector3.Dot(p,y));if(!points.Exists(v=>(v-q).sqrMagnitude<areaEpsilon*.0001f))points.Add(q);}
            points.Sort((a,c)=>a.x!=c.x?a.x.CompareTo(c.x):a.y.CompareTo(c.y));
            var hull=new List<Vector2>();
            foreach(var p in points){while(hull.Count>=2&&HexLayout.Cross(hull[hull.Count-1]-hull[hull.Count-2],p-hull[hull.Count-1])<=areaEpsilon)hull.RemoveAt(hull.Count-1);hull.Add(p);}
            int lower=hull.Count;
            for(int i=points.Count-2;i>=0;i--){var p=points[i];while(hull.Count>lower&&HexLayout.Cross(hull[hull.Count-1]-hull[hull.Count-2],p-hull[hull.Count-1])<=areaEpsilon)hull.RemoveAt(hull.Count-1);hull.Add(p);}hull.RemoveAt(hull.Count-1);
            if(hull.Count!=6)throw new InvalidOperationException(mesh.name+": expected a convex six-corner hexagonal prism; projected hull has "+hull.Count+" corners.");
            Vector2 center=Vector2.zero;foreach(var p in hull)center+=p/6;
            int right=0;for(int i=1;i<6;i++)if(hull[i].x>hull[right].x)right=i;
            var u=hull[right]-center;var v=(hull[(right+1)%6]+hull[(right+2)%6])*.5f-center;
            Vector2[] expected={u,u*.5f+v,-u*.5f+v,-u,-u*.5f-v,u*.5f-v};
            float tolerance=Mathf.Max(u.magnitude,v.magnitude)*.01f;
            for(int i=0;i<6;i++)if(Vector2.Distance(hull[(right+i)%6]-center,expected[i])>tolerance)throw new InvalidOperationException(mesh.name+": hex must be an affine regular hexagon (rotation and non-uniform scale are supported).");
            return new HexSurfaceFrame{Center=x*center.x+y*center.y,U=x*u.x+y*u.y,V=x*v.x+y*v.y,Normal=normal,Bottom=min,Top=max};
        }
    }

    public static partial class HexMeshBuilder
    {
        public static Mesh Build(Vector2[] contour,HexSurfaceFrame frame,float bottom,float top)
        {
            var p=new List<Vector2>(contour);float area=0;for(int i=0;i<p.Count;i++)area+=HexLayout.Cross(p[i],p[(i+1)%p.Count]);if(area<0)p.Reverse();
            var cap=Triangulate(p);var vertices=new List<Vector3>();var uv=new List<Vector2>();var triangles=new List<int>();
            // Independent caps and side vertices keep a hard horizontal rim and stable UVs.
            for(int i=0;i<p.Count;i++){vertices.Add(frame.Point(p[i],top));uv.Add((p[i]+Vector2.one)*.5f);}
            bool reverse=Vector3.Dot(Vector3.Cross(frame.U,frame.V),frame.Normal)<0;
            for(int i=0;i<cap.Count;i+=3)Add(triangles,cap[i],cap[i+1],cap[i+2],reverse);
            if(top>bottom)
            {
                int offset=vertices.Count;for(int i=0;i<p.Count;i++){vertices.Add(frame.Point(p[i],bottom));uv.Add((p[i]+Vector2.one)*.5f);}
                for(int i=0;i<cap.Count;i+=3)Add(triangles,offset+cap[i],offset+cap[i+1],offset+cap[i+2],!reverse);
                for(int i=0;i<p.Count;i++)
                {
                    int j=(i+1)%p.Count;int k=vertices.Count;
                    vertices.Add(frame.Point(p[i],bottom));vertices.Add(frame.Point(p[j],bottom));vertices.Add(frame.Point(p[j],top));vertices.Add(frame.Point(p[i],top));
                    uv.Add(Vector2.zero);uv.Add(Vector2.right);uv.Add(Vector2.one);uv.Add(Vector2.up);
                    Add(triangles,k,k+1,k+2,reverse);Add(triangles,k,k+2,k+3,reverse);
                }
            }
            var mesh=new Mesh{name="HexPartitionMesh"};mesh.SetVertices(vertices);mesh.SetUVs(0,uv);mesh.SetTriangles(triangles,0);mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }
        private static void Add(List<int> t,int a,int b,int c,bool reverse){t.Add(a);t.Add(reverse?c:b);t.Add(reverse?b:c);}
        private static List<int> Triangulate(List<Vector2> p)
        {
            var remaining=new List<int>();for(int i=0;i<p.Count;i++)remaining.Add(i);var result=new List<int>();
            int guard=p.Count*p.Count;
            while(remaining.Count>3&&guard-->0)
            {
                bool clipped=false;
                for(int i=0;i<remaining.Count;i++)
                {
                    int a=remaining[(i+remaining.Count-1)%remaining.Count],b=remaining[i],c=remaining[(i+1)%remaining.Count];
                    if(HexLayout.Cross(p[b]-p[a],p[c]-p[b])<=.00000001f)continue;
                    bool occupied=false;
                    foreach(int j in remaining)
                    {
                        if(j==a||j==b||j==c)continue;
                        if(HexLayout.Cross(p[b]-p[a],p[j]-p[a])>=-1e-8f&&HexLayout.Cross(p[c]-p[b],p[j]-p[b])>=-1e-8f&&HexLayout.Cross(p[a]-p[c],p[j]-p[c])>=-1e-8f){occupied=true;break;}
                    }
                    if(occupied)continue;result.Add(a);result.Add(b);result.Add(c);remaining.RemoveAt(i);clipped=true;break;
                }
                if(!clipped)throw new InvalidOperationException("Cannot triangulate fragment: self-intersection or degenerate outline.");
            }
            if(remaining.Count!=3)throw new InvalidOperationException("Triangulation did not terminate.");result.AddRange(remaining);return result;
        }
    }

    /// <summary>Horizontal domino + optional endpoint leg; unused cells become decorations.</summary>
    public static class HexLayout
    {
        public static LayoutDefinition Build(HexPartitionDefinition definition, int count, int seed)
        {
            if (count < 1 || count > 100) throw new ArgumentOutOfRangeException(nameof(count));
            foreach (var layout in definition.layouts) if (layout.count == count) return layout;
            var random = new System.Random(seed);
            // Search a compact discrete footprint. Every accepted primary owns a horizontal bar.
            for (int size = 5; size <= 32; size++)
            {
                float step = 2f / size;
                var cells = new HashSet<Vector2Int>();
                for (int y = -size; y <= size; y++)
                    for (int x = -size; x <= size; x++)
                    {
                        float ax = (Mathf.Abs(x) + .5f) * step;
                        float ay = (Mathf.Abs(y) + .5f) * step;
                        if (ay < definition.boundaryInset && ax + ay * .5f < definition.boundaryInset)
                            cells.Add(new Vector2Int(x,y));
                    }
                if (cells.Count < count * 2.55f) continue;
                List<List<Vector2Int>> best = null; HashSet<Vector2Int> remainder = null; int bestScore = int.MinValue;
                for (int attempt=0; attempt<80; attempt++)
                {
                    var free = new HashSet<Vector2Int>(cells);
                    var bars = new List<List<Vector2Int>>();
                    var candidates = new List<Vector2Int>(cells);
                    candidates.Sort((a,b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
                    for (int i=candidates.Count-1;i>0;i--) { int j=random.Next(i+1); var t=candidates[i]; candidates[i]=candidates[j]; candidates[j]=t; }
                    // Reserve exactly N label bars before adding any legs.
                    foreach (var p in candidates)
                    {
                        var q=p+Vector2Int.right;
                        if (bars.Count==count) break;
                        if (!free.Contains(p)||!free.Contains(q)) continue;
                        free.Remove(p); free.Remove(q); bars.Add(new List<Vector2Int>{p,q});
                    }
                    if (bars.Count!=count) continue;
                    int legs=0;
                    foreach (var bar in bars)
                    {
                        int start=random.Next(4);
                        for (int k=0;k<4;k++)
                        {
                            int option=(start+k)%4;
                            var p=bar[option/2]+(option%2==0?Vector2Int.up:Vector2Int.down);
                            if (!free.Remove(p)) continue;
                            bar.Add(p); legs++; break;
                        }
                    }
                    int score=legs*10-free.Count;
                    if(score>bestScore) {bestScore=score; best=bars; remainder=free;}
                }
                if (best==null) continue;
                var result=new List<FragmentDefinition>();
                for (int i=0;i<best.Count;i++)
                {
                    var bar=best[i]; var p=bar[0];
                    float padding=definition.labelPadding*step;
                    result.Add(new FragmentDefinition {id="generated-"+count+"-main-"+i, primary=true,
                        outline=Shape(bar,step,definition), label=new Rect((p.x-.5f)*step+padding,(p.y-.5f)*step+padding,2*step-2*padding,step-2*padding)});
                }
                var ordered=new List<Vector2Int>(remainder);
                ordered.Sort((a,b)=>a.y!=b.y?a.y.CompareTo(b.y):a.x.CompareTo(b.x));
                foreach(var p in ordered)
                {
                    if(!remainder.Remove(p))continue;
                    var decoration=new List<Vector2Int>{p};
                    if(remainder.Remove(p+Vector2Int.right))decoration.Add(p+Vector2Int.right);
                    result.Add(new FragmentDefinition{id="generated-"+count+"-decor-"+result.Count,outline=Shape(decoration,step,definition)});
                }
                var output=new LayoutDefinition{count=count,fragments=result.ToArray()}; Validate(output); return output;
            }
            throw new InvalidOperationException("Cannot pack "+count+" labels inside hex.");
        }

        private static Vector2[] Shape(List<Vector2Int> cells,float step,HexPartitionDefinition definition)
        {
            var edges=new Dictionary<Vector2Int,Vector2Int>(); var occupied=new HashSet<Vector2Int>(cells);
            foreach(var p in cells)
            {
                var a=p*2-new Vector2Int(1,1); var b=a+new Vector2Int(2,0); var c=a+new Vector2Int(2,2); var d=a+new Vector2Int(0,2);
                if(!occupied.Contains(p+Vector2Int.down))edges.Add(a,b);
                if(!occupied.Contains(p+Vector2Int.right))edges.Add(b,c);
                if(!occupied.Contains(p+Vector2Int.up))edges.Add(c,d);
                if(!occupied.Contains(p+Vector2Int.left))edges.Add(d,a);
            }
            var start=cells[0]*2-new Vector2Int(1,1);
            if(!edges.ContainsKey(start))foreach(var key in edges.Keys){start=key;break;}
            var polygon=new List<Vector2>(); var v=start;
            do {polygon.Add((Vector2)v*step*.5f);v=edges[v];}while(v!=start);
            for(int i=polygon.Count-1;i>=0;i--)
                if(Mathf.Abs(Cross(polygon[i]-polygon[(i+polygon.Count-1)%polygon.Count],polygon[(i+1)%polygon.Count]-polygon[i]))<.000001f)polygon.RemoveAt(i);
            // Orthogonal inward offset: same gap on convex and re-entrant edges.
            var inset=new List<Vector2>(); float gap=step*definition.cellGap*.5f;
            for(int i=0;i<polygon.Count;i++)
            {
                var a=(polygon[i]-polygon[(i+polygon.Count-1)%polygon.Count]).normalized;
                var b=(polygon[(i+1)%polygon.Count]-polygon[i]).normalized;
                inset.Add(polygon[i]+new Vector2(-a.y,a.x)*gap+new Vector2(-b.y,b.x)*gap);
            }
            var rounded=new List<Vector2>();
            for(int i=0;i<inset.Count;i++)
            {
                var p=inset[i];var prev=inset[(i+inset.Count-1)%inset.Count];var next=inset[(i+1)%inset.Count];
                float r=Mathf.Min(step*definition.cornerRadius,Mathf.Min(Vector2.Distance(p,prev),Vector2.Distance(p,next))*.45f);
                var a=p+(prev-p).normalized*r;var b=p+(next-p).normalized*r;
                for(int j=0;j<=definition.cornerSegments;j++){float t=(float)j/definition.cornerSegments;rounded.Add((1-t)*(1-t)*a+2*(1-t)*t*p+t*t*b);}
            }
            return rounded.ToArray();
        }
        public static float Cross(Vector2 a,Vector2 b)=>a.x*b.y-a.y*b.x;
        public static bool Contains(Vector2[] polygon,Vector2 p)
        {
            bool inside=false;
            for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++)
                if((polygon[i].y>p.y)!=(polygon[j].y>p.y)&&p.x<(polygon[j].x-polygon[i].x)*(p.y-polygon[i].y)/(polygon[j].y-polygon[i].y)+polygon[i].x)inside=!inside;
            return inside;
        }
        public static void Validate(LayoutDefinition layout)
        {
            if(layout.fragments==null)throw new InvalidOperationException("Missing fragments: "+layout.count);
            var ids=new HashSet<string>();int count=0;
            foreach(var f in layout.fragments)
            {
                if(f==null||string.IsNullOrEmpty(f.id)||!ids.Add(f.id)||f.outline==null||f.outline.Length<3)throw new InvalidOperationException("Invalid fragment/ID in layout "+layout.count);
                foreach(var p in f.outline)if(float.IsNaN(p.x)||float.IsNaN(p.y)||Mathf.Abs(p.y)>1.001f||Mathf.Abs(p.x)+.5f*Mathf.Abs(p.y)>1.001f)throw new InvalidOperationException(f.id+": outside hex.");
                if(!f.primary)continue;count++;
                if(f.label.width<=0||f.label.height<=0)throw new InvalidOperationException(f.id+": missing text rectangle.");
                for(int y=0;y<=4;y++)for(int x=0;x<=8;x++)
                    if(!Contains(f.outline,new Vector2(f.label.xMin+f.label.width*x/8,f.label.yMin+f.label.height*y/4)))throw new InvalidOperationException(f.id+": text rectangle outside fragment.");
            }
            if(count!=layout.count)throw new InvalidOperationException("Expected "+layout.count+" primary fragments, got "+count);
        }
    }

    /// <summary>Stable definition identity and explicit surface for a future text-mesh factory.</summary>
    public sealed class HexFragment : MonoBehaviour
    {
        [SerializeField] private string definitionId;
        [SerializeField] private Rect normalizedTextRect;
        [SerializeField] private Transform textPlane;
        public string DefinitionId=>definitionId;
        public Rect NormalizedTextRect=>normalizedTextRect;
        public Transform TextPlane=>textPlane;
        public void Initialize(string id,Rect rect,Transform plane){definitionId=id;normalizedTextRect=rect;textPlane=plane;}
    }

    [CustomEditor(typeof(HexPartitioner))]
    public sealed class HexPartitionerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();var partitioner=(HexPartitioner)target;
            if(GUILayout.Button("Generate preview")){partitioner.Generate();SceneView.RepaintAll();}
            if(GUILayout.Button("Clear preview")){partitioner.ClearGeneratedFragments();SceneView.RepaintAll();}
        }
    }
    public static class HexPartitionAuthoring
    {
        public const string Root="Assets/Game/Code/MeshGeneration/Code/HexPartition/Data";
        [MenuItem("Tools/Hex Partition/Install reference definition")]
        public static void Install()
        {
            var data=AssetDatabase.LoadAssetAtPath<TextAsset>(Root+"/ReferenceLayouts.json");
            if(!data)throw new InvalidOperationException("Missing ReferenceLayouts.json");
            var definition=AssetDatabase.LoadAssetAtPath<HexPartitionDefinition>(Root+"/Reference.asset");
            if(!definition){definition=ScriptableObject.CreateInstance<HexPartitionDefinition>();AssetDatabase.CreateAsset(definition,Root+"/Reference.asset");}
            definition.layouts=JsonUtility.FromJson<LayoutLibrary>(data.text).layouts;
            definition.primaryMaterial=Material("Main",new Color32(155,155,155,255));
            definition.decorationMaterial=Material("Decoration",new Color32(130,130,130,255));

            definition.labelMaterial=Material("TextPlane",new Color32(207,151,84,255));
            definition.Validate();EditorUtility.SetDirty(definition);AssetDatabase.SaveAssets();
            Debug.Log("Hex partition reference definitions installed and validated (1–12).");
        }
        private static Material Material(string name,Color color)
        {
            var path=Root+"/"+name+".mat";var mat=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!mat){var shader=Shader.Find("Universal Render Pipeline/Unlit");if(!shader)throw new InvalidOperationException("URP Unlit shader is unavailable.");mat=new Material(shader);AssetDatabase.CreateAsset(mat,path);}
            mat.SetColor("_BaseColor",color);EditorUtility.SetDirty(mat);return mat;
        }
        [MenuItem("Tools/Hex Partition/Validate and capture 1 6 12 100")]
        public static void ValidateAndCapture()
        {
            var go=GameObject.Find("PartitionHex");if(!go)throw new InvalidOperationException("PartitionHex not found in active scene.");
            var partitioner=go.GetComponent<HexPartitioner>();if(!partitioner)partitioner=go.AddComponent<HexPartitioner>();
            partitioner.definition=AssetDatabase.LoadAssetAtPath<HexPartitionDefinition>(Root+"/Reference.asset");
            var old=go.transform.Find("__HexPartitionFragments");if(old&&old!=partitioner.GeneratedRoot)UnityEngine.Object.DestroyImmediate(old.gameObject);
            foreach(int count in new[]{1,6,12,100})
            {
                partitioner.primaryFragmentCount=count;partitioner.Generate();
                var fragments=partitioner.PrimaryFragments;
                if(fragments.Count!=count)throw new InvalidOperationException("Unexpected primary count: "+count);
                Capture(partitioner,"Hex-"+count+".png");
            }
            partitioner.primaryFragmentCount=12;partitioner.Generate();
            Selection.activeGameObject=go;
            Debug.Log("Hex partition: validated and rendered 1, 6, 12, 100; temporary preview at 12.");
        }
        public static void Capture(HexPartitioner partitioner,string filename)
        {
            var frame=HexSurfaceFrame.FromMesh(partitioner.GetComponent<MeshFilter>().sharedMesh);
            var center=partitioner.transform.TransformPoint(frame.Center+frame.Normal*frame.Top);
            var u=partitioner.transform.TransformVector(frame.U);var v=partitioner.transform.TransformVector(frame.V);
            var normal=Vector3.Cross(v,u).normalized;
            var obj=new GameObject("Hex capture camera",typeof(Camera));var camera=obj.GetComponent<Camera>();
            var rt=new RenderTexture(768,768,24);var texture=new Texture2D(768,768,TextureFormat.RGB24,false);var previous=RenderTexture.active;
            try
            {
                camera.transform.position=center+normal*30*Mathf.Max(u.magnitude,v.magnitude);camera.transform.rotation=Quaternion.LookRotation(-normal,v.normalized);
                camera.orthographic=true;camera.orthographicSize=Mathf.Max(u.magnitude,v.magnitude)*1.13f;camera.nearClipPlane=.01f;camera.farClipPlane=100*Mathf.Max(u.magnitude,v.magnitude);
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color32(94,94,94,255);camera.targetTexture=rt;
                camera.Render();camera.Render();RenderTexture.active=rt;texture.ReadPixels(new Rect(0,0,768,768),0,0);texture.Apply();
                Directory.CreateDirectory("Tools/HexPartition/Validation");File.WriteAllBytes("Tools/HexPartition/Validation/"+filename,texture.EncodeToPNG());
            }
            finally{RenderTexture.active=previous;camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(obj);}
        }
    }

    /// <summary>Editor previews never survive scene saving, script reload or entering Play Mode.</summary>
    [InitializeOnLoad]
    internal static class HexPreviewLifecycle
    {
        static HexPreviewLifecycle()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.quitting += ClearAll;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
        }

        private static void OnSceneSaving(Scene scene, string path) => ClearScene(scene);
        private static void OnSceneClosing(Scene scene, bool removingScene) => ClearScene(scene);
        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            // Also runs when domain/scene reload is disabled in Enter Play Mode options.
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredEditMode) ClearAll();
        }
        private static void ClearScene(Scene scene)
        {
            if (Application.isPlaying || !scene.IsValid() || !scene.isLoaded) return;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var generator in root.GetComponentsInChildren<HexPartitioner>(true))
                    generator.ClearGeneratedFragments();
        }
        private static void ClearAll()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) ClearScene(SceneManager.GetSceneAt(i));
        }
        private static void BeforeReload()
        {
            ClearAll();
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.quitting -= ClearAll;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
        }
    }
