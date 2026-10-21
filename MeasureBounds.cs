public Bounds MeasureBounds()
{
    RefreshTransform();

    var renderers = BoundsRenderers;
    if (renderers == null || renderers.Length == 0)
        renderers = DefaultBoundsRenderers();

    var result = new Bounds(transform.position, Vector3.zero);
    bool found = false;

    void Include(Vector3 point)
    {
        if (!found)
        {
            result = new Bounds(point, Vector3.zero);
            found = true;
        }
        else
        {
            result.Encapsulate(point);
        }
    }

    foreach (var renderer in renderers)
    {
        if (renderer == null)
            continue;

        var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh == null || !mesh.isReadable)
        {
            Include(renderer.bounds.min);
            Include(renderer.bounds.max);
            continue;
        }

        var matrix = renderer.transform.localToWorldMatrix;
        foreach (var vertex in mesh.vertices)
            Include(matrix.MultiplyPoint3x4(vertex));
    }

    return result;
}
