using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace Sbrt.Av.Gameplay
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class InfoflowGenerator : MonoBehaviour
    {
        public bool UseBatchRendering = true;
        public float FlowPlaneLocalY;
        public InfoflowSettings Settings = new InfoflowSettings();
        public Material Material;
        [SerializeField, HideInInspector] GameObject sourceKafoRoot, generatedRoot;
        [SerializeField, HideInInspector] InfoflowRoutingResult lastResult;
        [SerializeField, HideInInspector] int savedFormatVersion;
        [SerializeField, HideInInspector] Vector3 planeXLocal, planeZLocal;
        [SerializeField, HideInInspector] List<InfoflowVisualRecord> registry = new List<InfoflowVisualRecord>();
        [SerializeField, HideInInspector] List<Mesh> ownedMeshes = new List<Mesh>();
        [SerializeField, HideInInspector] Material ownedMaterial;
        [SerializeField, HideInInspector] string summary;
        [SerializeField, HideInInspector] List<string> diagnostics = new List<string>();
        // Исходные данные нужны только для повторного Generate в текущем сеансе.
        // Для восстановления отображения после перезагрузки сохраняется рассчитанный lastResult.
        KafoDataModel sourceData;
        List<InfoflowDataModel> sourceFlows;

        public GameObject GeneratedRoot => generatedRoot;
        public InfoflowRoutingResult LastResult => savedFormatVersion == InfoflowRoutingResult.CurrentVersion ? lastResult : null;
        public IReadOnlyList<InfoflowVisualRecord> Registry => registry;
        public IReadOnlyList<string> Diagnostics => diagnostics;
        public string Summary => summary;
        public bool CanRegenerate => sourceData != null && sourceFlows != null && sourceKafoRoot != null;
        public double RenderingMilliseconds { get; private set; }
        public double TotalMilliseconds { get; private set; }
        public int RendererCount { get; private set; }
        public long MeshBytes { get; private set; }
        public long RendererMemoryBytes { get; private set; }

        public void Generate(KafoDataModel kafo, GameObject kafoRoot, IReadOnlyList<InfoflowDataModel> flows)
        {
            if (kafoRoot == null) { Clear(); diagnostics.Add("Не создана KAFO."); return; }
            var timer = Stopwatch.StartNew();
            Settings = Settings ?? new InfoflowSettings();

            // Сцену читаем один раз; маршрутизатор работает только с полученным снимком.
            var plane = new InfoflowPlane(kafoRoot.transform, FlowPlaneLocalY);
            var scene = InfoflowSceneSnapshot.Capture(kafo, kafoRoot, plane);
            var result = InfoflowRouter.Calculate(scene, flows, Settings);
            // Заменяем результат после расчёта: исключение не должно сначала удалять все линии.
            Clear();
            sourceKafoRoot = kafoRoot;
            sourceData = kafo;
            sourceFlows = flows?.Select(f => f == null ? null : new InfoflowDataModel
            {
                Id = f.Id, ProviderId = f.ProviderId, ConsumerId = f.ConsumerId
            }).ToList() ?? new List<InfoflowDataModel>();
            lastResult = result;
            planeXLocal = kafoRoot.transform.InverseTransformVector(plane.X);
            planeZLocal = kafoRoot.transform.InverseTransformVector(plane.Z);
            planeXLocal.y = planeZLocal.y = 0;
            savedFormatVersion = InfoflowRoutingResult.CurrentVersion;
            diagnostics.AddRange(result.Diagnostics);
            RebuildVisuals();
            TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
            UpdateSummary();
        }

        public void Regenerate()
        {
            if (!CanRegenerate) return;
            Generate(sourceData, sourceKafoRoot, sourceFlows);
        }

        public void RebuildVisuals()
        {
            // Меняется только представление: идентификаторы, порядок и точки маршрутов сохраняются.
            ClearVisuals();
            if (LastResult == null || sourceKafoRoot == null) return;
            var timer = Stopwatch.StartNew();
            var frame = sourceKafoRoot.transform;
            var plane = new InfoflowPlane {
                Origin = frame.TransformPoint(new Vector3(0, FlowPlaneLocalY, 0)),
                X = frame.TransformVector(planeXLocal), Z = frame.TransformVector(planeZLocal) };
            var material = Material;
            if (material == null)
            {
                if (ownedMaterial == null)
                    ownedMaterial = new Material(Resources.Load<Shader>("Infoflow/InfoflowUnlit")) {
                        name = "KAFO infoflows", hideFlags = HideFlags.HideAndDontSave };
                material = ownedMaterial;
            }
            generatedRoot = new GameObject("Generated KAFO Infoflows");
            generatedRoot.transform.SetParent(frame, false);
            generatedRoot.AddComponent<InfoflowOutput>().Owner = this;
            IInfoflowRenderer renderer = UseBatchRendering ? (IInfoflowRenderer)new InfoflowBatchRenderer() : new InfoflowIndividualRenderer();
            renderer.Render(generatedRoot.transform, plane, lastResult, material, registry, ownedMeshes);
            var renderers = generatedRoot.GetComponentsInChildren<Renderer>();
            RendererCount = renderers.Length;
            RendererMemoryBytes = renderers.Sum(r => UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(r));
            MeshBytes = ownedMeshes.Sum(m => UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(m));
            foreach (var route in lastResult.Routes)
            {
                var bounds = new Bounds(plane.World(route.Points[0]), Vector3.zero);
                foreach (var point in route.Points) bounds.Encapsulate(plane.World(point));
                bounds.Expand(lastResult.Width); route.Bounds = bounds;
            }
            RenderingMilliseconds = timer.Elapsed.TotalMilliseconds;
            UpdateSummary();
        }

        void UpdateSummary()
        {
            if (LastResult == null) { summary = ""; return; }
            summary = $"KAFO · Записей: {lastResult.SourceCount} · Линий: {lastResult.Routes.Count} · Пропущено: {lastResult.SkippedRecords}\n" +
                $"Расчёт: {lastResult.RoutingMilliseconds:F1} мс · Отрисовка: {RenderingMilliseconds:F1} мс · Всего: {TotalMilliseconds:F1} мс\n" +
                $"Фактически: ширина {lastResult.Width:G4} · зазор {lastResult.Gap:G4} · Renderer: {RendererCount}\n" +
                $"Память Renderer: {RendererMemoryBytes / 1024f:F1} КБ · Меши: {MeshBytes / 1024f:F1} КБ";
        }

        public void Clear()
        {
            ClearVisuals();
            if (ownedMaterial != null) Dispose(ownedMaterial);
            ownedMaterial = null;
            sourceKafoRoot = null;
            lastResult = null;
            savedFormatVersion = 0;
            sourceData = null;
            sourceFlows = null;
            diagnostics.Clear();
            summary = "";
            TotalMilliseconds = RenderingMilliseconds = 0;
        }

        void ClearVisuals()
        {
            // Одной ссылки generatedRoot недостаточно: после перезагрузки могли остаться старые иерархии.
            var outputs = new HashSet<GameObject>();
            if (generatedRoot != null) outputs.Add(generatedRoot);
            if (sourceKafoRoot != null)
                foreach (Transform child in sourceKafoRoot.transform)
                {
                    var marker = child.GetComponent<InfoflowOutput>();
                    if (marker != null ? marker.Owner == this : IsLegacyOutput(child)) outputs.Add(child.gameObject);
                }
            var meshes = new HashSet<Mesh>(ownedMeshes.Where(m => m != null));
            foreach (var output in outputs)
            {
                foreach (var filter in output.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null && filter.sharedMesh.name == "KAFO flow ribbons" &&
                        filter.sharedMesh.hideFlags == HideFlags.HideAndDontSave) meshes.Add(filter.sharedMesh);
                foreach (var collider in output.GetComponentsInChildren<MeshCollider>(true))
                    if (collider.sharedMesh != null && collider.sharedMesh.name == InfoflowIndividualRenderer.ColliderMeshName &&
                        collider.sharedMesh.hideFlags == HideFlags.HideAndDontSave) meshes.Add(collider.sharedMesh);
                // В Play Mode Destroy отложен до конца кадра; скрываем прежнее поколение сразу.
                output.SetActive(false); Dispose(output);
            }
            generatedRoot = null;
            foreach (var mesh in meshes) if (mesh != null) Dispose(mesh);
            ownedMeshes.Clear(); registry.Clear(); RendererCount = 0; MeshBytes = RendererMemoryBytes = 0;
        }

        static bool IsLegacyOutput(Transform root)
        {
            if (root.name != "Generated KAFO Infoflows") return false;
            return root.GetComponentsInChildren<LineRenderer>(true).Any(l => l.name.StartsWith("KAFO flow flow:", StringComparison.Ordinal)) ||
                root.GetComponentsInChildren<MeshFilter>(true).Any(f => f.name.StartsWith("KAFO flows batch ", StringComparison.Ordinal));
        }

        void OnEnable()
        {
            if (LastResult != null && sourceKafoRoot != null) RebuildVisuals();
            else Clear();
        }

        void OnDestroy() => Clear();

        static void Dispose(UnityEngine.Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
