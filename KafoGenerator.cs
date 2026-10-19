using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Sbrt.Av.Gameplay
{
	[DisallowMultipleComponent]
	public sealed class KafoGenerator : MonoBehaviour
	{
		[TextArea(5, 20)]
		public string Json = KafoExamples.Bridges;
		public Renderer MainZone;
		public Renderer IslandZone;
		public GameObject AsPrefab, SegmentPrefab, GroupPrefab;
		public TMP_FontAsset LabelFont;
		public bool GenerateOnPlay;
		public KafoLayoutSettings Settings = new KafoLayoutSettings();
		[SerializeField, HideInInspector]
		string summary;
		[SerializeField, HideInInspector]
		List<string> diagnostics = new List<string>();
		[SerializeField, HideInInspector]
		GameObject generatedRoot;
		[SerializeField, HideInInspector]
		bool hasIslandContent;
		public string Summary => summary;
		public IReadOnlyList<string> Diagnostics => diagnostics;
		public KafoLayoutResult LastResult { get; private set; }
		public NormalizedKafo LastData { get; private set; }
		public GameObject GeneratedRoot => generatedRoot;

		void Start()
		{
			if (GenerateOnPlay)
				Generate();
		}

		public void Generate()
		{
			Settings ??= new KafoLayoutSettings();
			var source = KafoDataLoader.Load(Json);
			var stages = new List<GameObject>();
			try
			{
				var app = Prepare(AsPrefab, KafoVisualKind.Application, stages);
				var segment = Prepare(SegmentPrefab, KafoVisualKind.Segment, stages);
				var group = Prepare(GroupPrefab, KafoVisualKind.Group, stages);
				var metrics = new KafoVisualMetrics
				{
					AsSize = app.GetComponent<KafoMeshResizer>().ReferenceSize,
					SegmentSize = segment.GetComponent<KafoMeshResizer>().ReferenceSize,
					GroupSize = group.GetComponent<KafoMeshResizer>().ReferenceSize
				};
				var mainRect = Footprint(MainZone.bounds);
				var islandRect = Footprint(IslandZone.bounds);
				var result = KafoLayoutSolver.Calculate(source, mainRect, islandRect, metrics, Settings);
				var validation = result.Validate(source);
				if (validation.Count > 0)
				{
					var safe = CopySettings(Settings);
					safe.SearchAttempts = 0;
					result = KafoLayoutSolver.Calculate(source, mainRect, islandRect, metrics, safe);
					result.Diagnostics.AddRange(validation.Select(v => "Перекомпоновка: " + v));
				}

				Clear();
				ResizeIsland(result.IslandRect);
				generatedRoot = new GameObject("Generated KFO");
				generatedRoot.transform.SetParent(transform, false);
				Render(source, result, metrics, app, segment, group);
				var physical = ValidatePhysicalBounds(result);
				if (physical.Count > 0)
				{
					// A prepared prefab normally matches exactly. An incompatible one is replaced, never omitted.
					result.Diagnostics.AddRange(physical);
					Clear();
					app = Prepare(AsPrefab, KafoVisualKind.Application, stages);
					segment = Prepare(SegmentPrefab, KafoVisualKind.Segment, stages);
					group = Prepare(GroupPrefab, KafoVisualKind.Group, stages);
					metrics = new KafoVisualMetrics
					{
						AsSize = app.GetComponent<KafoMeshResizer>().ReferenceSize,
						SegmentSize = segment.GetComponent<KafoMeshResizer>().ReferenceSize,
						GroupSize = group.GetComponent<KafoMeshResizer>().ReferenceSize
					};
					var safe = CopySettings(Settings);
					safe.SearchAttempts = 0;
					result = KafoLayoutSolver.Calculate(source, mainRect, islandRect, metrics, safe);
					result.Diagnostics.Add("Использованы резервные префабы для точного соблюдения bounds.");
					ResizeIsland(result.IslandRect);
					generatedRoot = new GameObject("Generated KFO");
					generatedRoot.transform.SetParent(transform, false);
					Render(source, result, metrics, app, segment, group);
				}

				LastData = source;
				LastResult = result;
				diagnostics = new List<string>(result.Diagnostics);
				hasIslandContent = source.Placements.Any(p => p.SegmentKey == null);
				IslandZone.enabled = hasIslandContent;
				summary =
					$"{source.Name}\nСегменты: {result.Segments.Count} · Группы: {result.Groups.Count} · АС: {result.Systems.Count} · Мосты: {result.BridgeCount}\nЯчейка: {result.CellSize.x:F3} × {result.CellSize.y:F3} · Все {source.Placements.Count} размещений отображены\nМосты: {(result.BridgesAlongX ? "X" : "Z")} · Относительные размеры АС: 1×1…{Settings.MaxAsLengthCells}×{Settings.MaxAsWidthCells}";
			}
			finally
			{
				foreach (var stage in stages)
					if (stage != null)
						Dispose(stage);
			}
		}

		static KafoLayoutSettings CopySettings(KafoLayoutSettings s) =>
			JsonUtility.FromJson<KafoLayoutSettings>(JsonUtility.ToJson(s));

		GameObject Prepare(GameObject prefab, KafoVisualKind kind, List<GameObject> stages)
		{
			var stage = Instantiate(prefab);
			stage.name = "KFO Template " + kind;
			stage.hideFlags = HideFlags.HideAndDontSave;
			stage.transform.position = Vector3.zero;
			stage.SetActive(true);
			var shape = stage.GetComponent<KafoMeshResizer>();
			if (shape == null)
				shape = stage.AddComponent<KafoMeshResizer>();
			if (!shape.CaptureSource())
			{
				Dispose(stage);
				stage.hideFlags = HideFlags.HideAndDontSave;
				stage.GetComponent<KafoMeshResizer>().CaptureSource();
			}

			stages.Add(stage);
			return stage;
		}

		void ResizeIsland(Rect rect)
		{
			var shape = IslandZone.GetComponent<KafoMeshResizer>();
			if (shape == null)
				shape = IslandZone.gameObject.AddComponent<KafoMeshResizer>();
			if (shape.SourceParts.Count == 0)
				shape.CaptureSource();
			var bounds = IslandZone.bounds;
			shape.Resize(new Vector3(rect.center.x, bounds.center.y, rect.center.y),
				new Vector3(rect.width, bounds.size.y, rect.height), 1);
		}

		public static Rect Footprint(Bounds b) => new Rect(b.min.x, b.min.z, b.size.x, b.size.z);

		void Render(NormalizedKafo source, KafoLayoutResult result, KafoVisualMetrics metrics, GameObject app,
			GameObject segment, GameObject group)
		{
			float k = result.CellScale, unit = Mathf.Min(result.CellSize.x, result.CellSize.y);
			float mainBottom = MainZone.bounds.max.y + .025f * k, segmentHeight = metrics.SegmentSize.y * k;
			float groupHeight = metrics.GroupSize.y * k;
			float mainTop = mainBottom + segmentHeight, islandTop = IslandZone.bounds.max.y;
			foreach (var s in result.Segments)
			{
				var view = Spawn(segment, s.Rect, mainBottom, segmentHeight, k, "Segment", s.Key);
				view.name = "Segment · " + s.Name;
				view.GetComponent<KafoGeneratedItem>().TitleRect = s.TitleRect;
				FloorLabel(view.transform, s.Name, s.TitleRect, mainTop + .008f * k, unit, .25f,
					new Color(.45f, .8f, 1), !result.BridgesAlongX);
			}

			foreach (var g in result.Groups)
			{
				float y = (g.SegmentKey == null ? islandTop : mainTop) + .015f * k;
				var view = Spawn(group, g.Rect, y, groupHeight, k, "Group", g.Key);
				view.name = "Group · " + g.Name;
				view.GetComponent<KafoGeneratedItem>().TitleRect = g.TitleRect;
				FloorLabel(view.transform, g.Name, g.TitleRect, y + groupHeight + .008f * k, unit, .2f,
					new Color(.72f, .8f, .85f), !result.BridgesAlongX);
			}

			var systems = source.Systems.ToDictionary(s => s.Key);
			foreach (var a in result.Systems)
			{
				float y = (a.SegmentKeys.Count == 0 ? islandTop : mainTop) + groupHeight + .04f * k;
				var view = Spawn(app, a.Rect, y, metrics.AsSize.y * k, k, a.IsBridge ? "Bridge" : "AS", a.Key);
				view.name = (a.IsBridge ? "Bridge · " : "AS · ") + a.Name;
				var item = view.GetComponent<KafoGeneratedItem>();
				item.SystemKey = a.SystemKey;
				item.PlacementKeys = new List<string>(a.PlacementKeys);
				item.SourceRecord = systems[a.SystemKey].Original?.ToString(Newtonsoft.Json.Formatting.Indented);
				float inset = .16f * unit, height = metrics.AsSize.y * k;
				var origin = result.BridgesAlongX
					? new Vector3(a.Rect.xMin - .003f * k, y + height - inset, a.Rect.yMax - inset)
					: new Vector3(a.Rect.xMax - inset, y + height - inset, a.Rect.yMax + .003f * k);
				var rotation = Quaternion.Euler(0, result.BridgesAlongX ? 90 : 180, 0);
				float width = (result.BridgesAlongX ? a.Rect.height : a.Rect.width) - 2 * inset;
				Label(view.transform, a.Name, unit * .19f, new Color(.88f, 1, .91f), origin, rotation,
					new Vector2(width, height - 2 * inset));
			}
		}

		GameObject Spawn(GameObject template, Rect rect, float bottom, float height, float scale, string kind,
			string key)
		{
			var go = Instantiate(template, generatedRoot.transform, false);
			go.hideFlags = HideFlags.None;
			go.SetActive(true);
			foreach (var t in go.GetComponentsInChildren<Transform>(true))
				t.gameObject.hideFlags = HideFlags.None;
			var shape = go.GetComponent<KafoMeshResizer>();
			go.transform.position = new Vector3(rect.center.x, bottom, rect.center.y);
			shape.Resize(new Vector3(rect.center.x, bottom + height * .5f, rect.center.y),
				new Vector3(rect.width, height, rect.height), scale);
			var meta = go.GetComponent<KafoGeneratedItem>() ?? go.AddComponent<KafoGeneratedItem>();
			meta.Kind = kind;
			meta.Key = key;
			meta.PlannedRect = rect;
			meta.Geometry = shape;
			return go;
		}

		TextMeshPro Label(Transform parent, string text, float size, Color color, Vector3 origin, Quaternion rotation,
			Vector2 area)
		{
			var go = new GameObject("Label");
			go.transform.SetParent(parent, false);
			var font = LabelFont != null ? LabelFont : Resources.Load<TMP_FontAsset>("KafoFonts/KFO Sans SDF");
			if (font == null)
				font = TMP_Settings.defaultFontAsset;
			var component = go.AddComponent<KafoTextLabel>();
			component.Configure(text, font, color, origin, rotation, area, size);
			return go.GetComponent<TextMeshPro>();
		}

		void FloorLabel(Transform parent, string text, Rect rect, float y, float unit, float size, Color color,
			bool transpose)
		{
			float width = transpose ? rect.height : rect.width, height = transpose ? rect.width : rect.height;
			Label(parent, (text ?? "Без имени").ToUpperInvariant(), unit * size, color,
				new Vector3(transpose ? rect.xMax : rect.xMin, y, rect.yMax),
				Quaternion.Euler(90, transpose ? 90 : 0, 0), new Vector2(width, height));
		}

		public List<string> ValidatePhysicalBounds(KafoLayoutResult result = null)
		{
			result = result ?? LastResult;
			var problems = new List<string>();
			if (generatedRoot == null || result == null)
				return problems;
			float e = Mathf.Max(.0001f, Mathf.Min(result.CellSize.x, result.CellSize.y) * .002f);
			foreach (var item in generatedRoot.GetComponentsInChildren<KafoGeneratedItem>())
			{
				var actual = Footprint(item.Geometry.MeasureBounds());
				if (!KafoLayoutResult.Contains(item.PlannedRect, actual, e) ||
				    !KafoLayoutResult.Contains(actual, item.PlannedRect, e))
					problems.Add("Bounds отличаются от расчёта: " + item.name);
				if (item.Kind == "Segment" || item.Kind == "Group")
				{
					var title = item.GetComponentInChildren<TextMeshPro>();
					if (title == null || !KafoLayoutResult.Contains(item.TitleRect,
						    Footprint(title.GetComponent<Renderer>().bounds), e))
						problems.Add("Подпись вне зарезервированной полосы: " + item.name);
				}
			}

			return problems;
		}

		public void Clear()
		{
			if (generatedRoot != null)
			{
				generatedRoot.SetActive(false);
				Dispose(generatedRoot);
			}

			generatedRoot = null;
			LastResult = null;
			LastData = null;
			summary = "";
			diagnostics.Clear();
			hasIslandContent = false;
			if (IslandZone != null)
				IslandZone.enabled = false;
		}

		static void Dispose(UnityEngine.Object o)
		{
			if (Application.isPlaying)
				Destroy(o);
			else
				DestroyImmediate(o);
		}
	}

	public enum KafoVisualKind
	{
		Application,
		Segment,
		Group
	}
}
