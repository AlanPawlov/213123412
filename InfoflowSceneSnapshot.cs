using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Sbrt.Av.Gameplay
{
    public static class InfoflowSceneSnapshot
    {
        public static InfoflowScene Capture(KafoDataModel data, GameObject root, InfoflowPlane plane)
        {
            var scene = new InfoflowScene();
            var source = KafoDataLoader.Normalize(data);
            var systems = source.Systems.ToDictionary(s => s.Key);
            // Связи серверного ID относятся к первому описанию, как в генераторе KAFO.
            var first = new Dictionary<long, string>();
            foreach (var system in source.Systems)
            {
                if (TryReadAsId(system.Original as JObject, system.Key, out long id) && !first.ContainsKey(id))
                    first.Add(id, system.Key);
            }
            var placements = source.Placements.ToDictionary(p => p.Key);
            var segmentBodies = new Dictionary<string, Rect>();
            var bridges = new List<InfoflowEndpoint>();
            bool haveBounds = false;
            foreach (var item in root.GetComponentsInChildren<KafoGeneratedItem>(true))
            {
                if (!Footprint(item, plane, out Rect body, out float height)) continue;
                scene.Bounds = haveBounds ? Rect.MinMaxRect(Mathf.Min(scene.Bounds.xMin, body.xMin),
                    Mathf.Min(scene.Bounds.yMin, body.yMin), Mathf.Max(scene.Bounds.xMax, body.xMax), Mathf.Max(scene.Bounds.yMax, body.yMax)) : body;
                haveBounds = true;
                if (item.Kind == "Group" || item.Kind == "Segment")
                {
                    if (item.Kind == "Segment")
                    {
                        if (!string.IsNullOrEmpty(item.Key)) segmentBodies[item.Key] = body;
                        var previous = scene.RoutingBounds ?? body;
                        scene.RoutingBounds = Rect.MinMaxRect(Mathf.Min(previous.xMin, body.xMin),
                            Mathf.Min(previous.yMin, body.yMin), Mathf.Max(previous.xMax, body.xMax), Mathf.Max(previous.yMax, body.yMax));
                    }
                    var label = item.GetComponentInChildren<TMP_Text>(true);
                    if (label != null && label.TryGetComponent(out Renderer renderer))
                        scene.SoftAreas.Add(ProjectBounds(renderer.bounds, plane));
                    continue;
                }
                if (item.Kind != "AS" && item.Kind != "Bridge") continue;
                // Даже дубли описаний без доступного конца связи остаются физическими препятствиями.
                scene.Obstacles.Add(body);
                if (item.SystemKey == null || !systems.TryGetValue(item.SystemKey, out var system) ||
                    !first.TryGetValue(system.Id, out string key) || key != system.Key) continue;
                var endpoint = new InfoflowEndpoint { AsId = system.Id, Key = item.Key, Body = body, Height = height };
                if (item.Geometry != null) endpoint.Border = Mathf.Max(item.Geometry.BorderX, item.Geometry.BorderZ);
                foreach (string placementKey in item.PlacementKeys)
                    if (placements.TryGetValue(placementKey, out var p) && p.SegmentKey != null && !endpoint.Segments.Contains(p.SegmentKey))
                        endpoint.Segments.Add(p.SegmentKey);
                scene.Endpoints.Add(endpoint);
                if (item.Kind == "Bridge") bridges.Add(endpoint);
            }
            scene.Endpoints.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            foreach (var bridge in bridges)
            {
                var parts = bridge.Segments.Where(segmentBodies.ContainsKey).Select(key =>
                {
                    var segment = segmentBodies[key]; var body = bridge.Body;
                    return Rect.MinMaxRect(Mathf.Max(body.xMin, segment.xMin), Mathf.Max(body.yMin, segment.yMin),
                        Mathf.Min(body.xMax, segment.xMax), Mathf.Min(body.yMax, segment.yMax));
                }).Where(part => part.width > 0 && part.height > 0).ToList();
                if (parts.Count == 0) continue;
                float spreadX = parts.Max(p => p.center.x) - parts.Min(p => p.center.x);
                float spreadZ = parts.Max(p => p.center.y) - parts.Min(p => p.center.y);
                bool alongX = spreadX >= spreadZ;
                if (parts.Count > 1) scene.BridgesAlongX = alongX;
                // Подключения находятся у правого конца моста; в поперечной компоновке — у нижнего.
                // Направление читаем из физических частей, в том числе после перезагрузки сцены.
                bridge.PortArea = parts.OrderByDescending(p => alongX ? p.center.x : -p.center.y).First();
            }
            var owner = root.GetComponentInParent<KafoGenerator>();
            // Островок остаётся доступен для своих связей; его фактическую зону только читаем.
            if (scene.RoutingBounds.HasValue && owner != null && owner.IslandZone != null &&
                scene.Endpoints.Any(e => e.Segments.Count == 0))
            {
                var main = scene.RoutingBounds.Value;
                var island = ProjectBounds(owner.IslandZone.bounds, plane);
                scene.RoutingBounds = Rect.MinMaxRect(Mathf.Min(main.xMin, island.xMin), Mathf.Min(main.yMin, island.yMin),
                    Mathf.Max(main.xMax, island.xMax), Mathf.Max(main.yMax, island.yMax));
            }
            if (owner != null && owner.LastResult != null)
            {
                scene.BridgesAlongX = owner.LastResult.BridgesAlongX;
                scene.CellSize = Mathf.Min(owner.LastResult.CellSize.x, owner.LastResult.CellSize.y);
            }
            else if (scene.Endpoints.Count > 0)
            {
                var ordinary = scene.Endpoints.Where(e => e.Segments.Count == 1).ToList();
                var sizes = (ordinary.Count > 0 ? ordinary : scene.Endpoints)
                    .Select(e => Mathf.Min(e.Body.width, e.Body.height)).OrderBy(v => v).ToList();
                scene.CellSize = sizes[sizes.Count / 2];
            }
            if (!haveBounds) scene.Bounds = new Rect(-1, -1, 2, 2);
            foreach (var endpoint in scene.Endpoints)
            {
                var r = endpoint.Body;
                // Мягкий запас видимости для принятой изометрии; положение камеры не читается.
                float shadow = endpoint.Height / Mathf.Tan(35.26439f * Mathf.Deg2Rad) / Mathf.Sqrt(2);
                scene.SoftAreas.Add(scene.BridgesAlongX
                    ? Rect.MinMaxRect(r.xMin - shadow, r.yMin - shadow, r.xMax, r.yMax)
                    : Rect.MinMaxRect(r.xMin - shadow, r.yMin, r.xMax, r.yMax + shadow));
            }
            return scene;
        }

        internal static bool TryReadAsId(JObject source, string systemKey, out long id)
        {
            string field = systemKey != null && systemKey.StartsWith("placeholder:", StringComparison.Ordinal) ? "AsId" : "Id";
            return long.TryParse(source?.GetValue(field, StringComparison.OrdinalIgnoreCase)?.ToString(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }

        static bool Footprint(KafoGeneratedItem item, InfoflowPlane plane, out Rect rect, out float height)
        {
            var geometry = item.Geometry != null ? item.Geometry : item.GetComponent<KafoMeshResizer>();
            var renderers = geometry != null ? geometry.BoundsRenderers : null;
            if (renderers == null || renderers.Length == 0)
                renderers = item.GetComponentsInChildren<Renderer>(true);
            bool found = false;
            Vector2 min = Vector2.zero, max = Vector2.zero;
            float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            var vertices = new List<Vector3>();
            foreach (var renderer in renderers)
            {
                // Текст и декоративный Glow не увеличивают запрещённую область корпуса.
                if (renderer == null || renderer.GetComponentInParent<TMP_Text>() != null) continue;
                bool glow = false;
                for (var part = renderer.transform; part != null && part.IsChildOf(item.transform); part = part.parent)
                    if (part.name.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0) { glow = true; break; }
                if (glow) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh != null && mesh.isReadable)
                {
                    // Текущие вершины и полная матрица учитывают деформацию, pivot и масштаб всех родителей.
                    mesh.GetVertices(vertices);
                    foreach (var vertex in vertices) Include(renderer.localToWorldMatrix.MultiplyPoint3x4(vertex));
                }
                else
                {
                    // Для недоступного меша используем консервативную проекцию всех углов Renderer.bounds.
                    var bounds = renderer.bounds;
                    for (int i = 0; i < 8; i++) Include(new Vector3(
                        (i & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (i & 2) == 0 ? bounds.min.y : bounds.max.y,
                        (i & 4) == 0 ? bounds.min.z : bounds.max.z));
                }
            }
            rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            height = found ? top - bottom : 0;
            return found && rect.width > 0 && rect.height > 0;

            void Include(Vector3 world)
            {
                float level = Vector3.Dot(world, plane.Normal);
                bottom = Mathf.Min(bottom, level); top = Mathf.Max(top, level);
                var point = plane.Project(world);
                if (!found) { min = max = point; found = true; }
                else { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
            }
        }

        static Rect ProjectBounds(Bounds bounds, InfoflowPlane plane)
        {
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                var p = plane.Project(new Vector3((i & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (i & 2) == 0 ? bounds.min.y : bounds.max.y, (i & 4) == 0 ? bounds.min.z : bounds.max.z));
                min = Vector2.Min(min, p); max = Vector2.Max(max, p);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
    }
}
