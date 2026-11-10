using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sbrt.Av.Gameplay
{
    internal static class InfoflowLabels
    {
        const float StraightTolerance = .0001f;
        // В штатном изометрическом ракурсе подъём над плоскостью проецируется вдоль +X и +Z.
        static readonly Vector2 UpInView = Vector2.one;

        readonly struct Straight
        {
            public readonly Vector2 Start, Direction;
            public readonly float Length;
            public readonly bool AlongX;
            public float Across => AlongX ? Start.y : Start.x;

            public Straight(Vector2 a, Vector2 b)
            {
                var delta = b - a;
                AlongX = Mathf.Abs(delta.x) >= Mathf.Abs(delta.y);
                if ((AlongX ? delta.x : delta.y) < 0)
                    (a, b) = (b, a);
                Start = a;
                Length = delta.magnitude;
                Direction = (b - a) / Length;
            }

            public Vector2 At(float distance) => Start + Direction * distance;
        }

        sealed class Label
        {
            public TextMeshPro Text;
            public Bounds Glyphs;
            public List<Straight> Candidates;
            public int Order;
            public bool Stagger;
            public Straight Preferred => Candidates[0];
        }

        public static void Render(Transform parent, InfoflowPlane plane, InfoflowRoutingResult result,
            IReadOnlyList<InfoflowVisualRecord> registry)
        {
            var labels = new List<Label>();
            Transform batchFrame = null;
            TMP_FontAsset font = null;
            float glyphHeight = result.CellSize * .10f;
            float padding = glyphHeight * .25f;
            float lift = Mathf.Max(glyphHeight * .15f, result.Width * .5f);

            for (int i = 0; i < result.Routes.Count; i++)
            {
                var route = result.Routes[i];
                if (string.IsNullOrWhiteSpace(route.Name)) continue;
                var straights = Straights(route.Points);
                if (straights.Count == 0) continue;

                if (font == null)
                    font = Resources.Load<TMP_FontAsset>("KafoFonts/KFO Sans SDF") ?? TMP_Settings.defaultFontAsset;
                if (font == null) return;

                var line = registry[i].IndividualRenderer;
                if (line == null && batchFrame == null)
                    batchFrame = InfoflowIndividualRenderer.UnscaledFrame(parent);
                var child = new GameObject("Flow name " + route.Key);
                child.transform.SetParent(line != null ? line.transform : batchFrame, false);
                var text = child.AddComponent<TextMeshPro>();
                text.font = font;
                text.fontSize = glyphHeight * 10 * font.faceInfo.pointSize / Mathf.Max(1, font.faceInfo.capLine);
                text.alignment = TextAlignmentOptions.Center;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.richText = false;
                text.parseCtrlCharacters = false;
                text.color = Color.white;
                text.text = route.Name;
                var renderer = child.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                text.ForceMeshUpdate();

                var glyphs = GlyphBounds(text.textInfo);
                if (glyphs.size.x <= 0)
                {
                    child.SetActive(false);
                    continue;
                }
                straights.Sort((a, b) => b.Length.CompareTo(a.Length));
                var longest = straights[0];
                straights.RemoveAll(s => s.Length < glyphs.size.x);
                if (straights.Count == 0) straights.Add(longest);
                labels.Add(new Label { Text = text, Glyphs = glyphs, Candidates = straights, Order = i });
            }

            // Если центры соседних дорожек заняты, укладываем подписи с одного конца пучка
            // поперёк магистрали, в порядке дорожек. Одиночная подпись остаётся по центру.
            var centered = new Rect[labels.Count];
            for (int i = 0; i < labels.Count; i++)
                centered[i] = Footprint(labels[i], labels[i].Preferred, labels[i].Preferred.Length * .5f);
            for (int i = 0; i < labels.Count; i++)
            for (int j = 0; j < i; j++)
                if (labels[i].Preferred.AlongX == labels[j].Preferred.AlongX && centered[i].Overlaps(centered[j]))
                    labels[i].Stagger = labels[j].Stagger = true;

            labels.Sort((a, b) =>
            {
                int axis = a.Preferred.AlongX.CompareTo(b.Preferred.AlongX);
                if (axis != 0) return axis;
                int lane = a.Preferred.Across.CompareTo(b.Preferred.Across);
                return lane != 0 ? lane : a.Order.CompareTo(b.Order);
            });
            var occupied = new List<Rect>(labels.Count);
            var blocked = new List<Vector2>();
            var up = plane.Normal;
            // Базис LineRenderer: координаты маршрута X и Z лежат в его локальных X и Y.
            var flowRotation = Quaternion.LookRotation(up, plane.Z);
            var textAlongX = flowRotation * Quaternion.Euler(90f, 0f, 0f);
            var textAlongY = flowRotation * Quaternion.Euler(0f, 90f, 90f);
            foreach (var label in labels)
            {
                var chosen = label.Preferred;
                float position = chosen.Length * .5f;
                for (int i = 0; i < label.Candidates.Count; i++)
                {
                    var straight = label.Candidates[i];
                    float middle = straight.Length * .5f;
                    float low = Mathf.Min(label.Glyphs.extents.x, middle);
                    float desired = i == 0 && label.Stagger ? Mathf.Min(low + padding, middle) : middle;
                    if (!FindFree(Footprint(label, straight, 0), straight.Direction,
                        low, straight.Length - low, desired, out float free)) continue;
                    chosen = straight;
                    position = free;
                    break;
                }
                // Если все места заняты, подпись остаётся по центру длинного участка на общей высоте.
                occupied.Add(Footprint(label, chosen, position));

                var rotation = chosen.AlongX ? textAlongX : textAlongY;
                var baseline = plane.World(chosen.At(position)) + up * lift;
                var glyphOffset = new Vector3(label.Glyphs.center.x, label.Glyphs.min.y, 0f);
                var origin = baseline - rotation * glyphOffset;
                label.Text.transform.SetPositionAndRotation(origin, rotation);
            }

            Rect Footprint(Label label, Straight straight, float distance)
            {
                var center = straight.At(distance) + UpInView * lift;
                var edge = straight.Direction * label.Glyphs.extents.x;
                var extent = new Vector2(Mathf.Abs(edge.x), Mathf.Abs(edge.y)) + Vector2.one * padding;
                var min = center - extent;
                var max = center + extent + UpInView * label.Glyphs.size.y;
                return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            }

            bool FindFree(Rect origin, Vector2 direction, float low, float high, float desired, out float free)
            {
                blocked.Clear();
                foreach (var other in occupied)
                {
                    float start = float.NegativeInfinity, end = float.PositiveInfinity;
                    if (IntersectRange(other.xMin - origin.xMax, other.xMax - origin.xMin, direction.x, ref start, ref end) &&
                        IntersectRange(other.yMin - origin.yMax, other.yMax - origin.yMin, direction.y, ref start, ref end) &&
                        start < high && end > low)
                        blocked.Add(new Vector2(start, end));
                }
                blocked.Sort((a, b) => a.x.CompareTo(b.x));
                float best = desired, bestDistance = float.PositiveInfinity;
                float from = low;
                foreach (var range in blocked)
                {
                    Consider(from, Mathf.Min(high, range.x));
                    from = Mathf.Max(from, range.y);
                    if (from > high) break;
                }
                Consider(from, high);
                free = best;
                return bestDistance < float.PositiveInfinity;

                void Consider(float a, float b)
                {
                    if (a > b) return;
                    float candidate = Mathf.Clamp(desired, a, b);
                    float distance = Mathf.Abs(candidate - desired);
                    if (distance >= bestDistance) return;
                    best = candidate;
                    bestDistance = distance;
                }
            }
        }

        static Bounds GlyphBounds(TMP_TextInfo info)
        {
            var bounds = new Bounds();
            bool first = true;
            for (int i = 0; i < info.characterCount; i++)
            {
                var glyph = info.characterInfo[i];
                if (!glyph.isVisible) continue;
                if (first)
                {
                    bounds = new Bounds(glyph.bottomLeft, Vector3.zero);
                    first = false;
                }
                bounds.Encapsulate(glyph.bottomLeft);
                bounds.Encapsulate(glyph.topRight);
            }
            return bounds;
        }

        static bool IntersectRange(float min, float max, float speed, ref float start, ref float end)
        {
            if (speed == 0) return min < 0 && max > 0;
            float a = min / speed, b = max / speed;
            start = Mathf.Max(start, Mathf.Min(a, b));
            end = Mathf.Min(end, Mathf.Max(a, b));
            return start < end;
        }

        static List<Straight> Straights(IReadOnlyList<Vector2> points)
        {
            var result = new List<Straight>();
            int i = 0;
            int longest = -1;
            float longestSize = 0;
            while (i + 1 < points.Count)
            {
                var delta = points[i + 1] - points[i];
                float size = delta.sqrMagnitude;
                if (size > longestSize)
                {
                    longest = i;
                    longestSize = size;
                }
                // Точки дуг и плавного подвода к порту входят в Points, но не являются прямыми магистралями.
                if (size == 0 || Mathf.Min(Mathf.Abs(delta.x), Mathf.Abs(delta.y)) >
                    Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y)) * StraightTolerance)
                {
                    i++;
                    continue;
                }
                int start = i++;
                var direction = delta.normalized;
                while (i + 1 < points.Count)
                {
                    var next = points[i + 1] - points[i];
                    float parallel = Vector2.Dot(direction, next);
                    if (parallel <= 0 || Mathf.Abs(Vector3.Cross(direction, next).z) > parallel * StraightTolerance)
                        break;
                    i++;
                }
                result.Add(new Straight(points[start], points[i]));
            }
            if (result.Count == 0 && longest >= 0)
                result.Add(new Straight(points[longest], points[longest + 1]));
            return result;
        }
    }
}
