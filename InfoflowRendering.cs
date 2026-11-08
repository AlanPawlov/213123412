namespace Sbrt.Av.Gameplay
{
	[Serializable]
	public sealed class InfoflowMeshRange
	{
		public MeshFilter Filter;
		public MeshRenderer Renderer;
		public int StartVertex, VertexCount, StartIndex, IndexCount;
	}

	[Serializable]
	public sealed class InfoflowVisualRecord
	{
		// Принадлежность геометрии физической линии сохраняется и в пакетном режиме для будущего выбора.
		public string RouteKey, ProviderKey, ConsumerKey;
		public long ProviderId, ConsumerId;
		public int SourceIndex;
		public long FlowId;
		public LineRenderer IndividualRenderer;
		public List<InfoflowMeshRange> MeshRanges = new List<InfoflowMeshRange>();
	}

	public interface IInfoflowRenderer
	{
		void Render(Transform parent, InfoflowPlane plane, InfoflowRoutingResult result, Material material,
			List<InfoflowVisualRecord> registry, List<Mesh> meshes);
	}

	public sealed class InfoflowBatchRenderer : IInfoflowRenderer
	{
		public void Render(Transform parent, InfoflowPlane plane, InfoflowRoutingResult result, Material material,
			List<InfoflowVisualRecord> registry, List<Mesh> meshes)
		{
			const int chunkLimit = 60000;
			var vertices = new List<Vector3>();
			var indices = new List<int>();
			var ranges = new List<InfoflowMeshRange>();
			var inverse = parent.worldToLocalMatrix;
			int chunk = 0;
			foreach (var route in result.Routes)
			{
				var record = new InfoflowVisualRecord
				{
					RouteKey = route.Key, SourceIndex = route.SourceIndex,
					FlowId = route.FlowId, ProviderKey = route.ProviderKey, ConsumerKey = route.ConsumerKey
				};
				registry.Add(record);
				int first = 0;
				while (first < route.Points.Count - 1)
				{
					if (chunkLimit - vertices.Count < 4)
						Flush();
					int count = Mathf.Min(route.Points.Count - first, (chunkLimit - vertices.Count) / 2);
					var range = new InfoflowMeshRange
					{
						StartVertex = vertices.Count, VertexCount = count * 2,
						StartIndex = indices.Count, IndexCount = (count - 1) * 6
					};
					ranges.Add(range);
					record.MeshRanges.Add(range);
					int start = vertices.Count;
					for (int k = 0; k < count; k++)
					{
						int i = first + k;
						var world = plane.World(route.Points[i]);
						var before = i > 0 ? (world - plane.World(route.Points[i - 1])).normalized : Vector3.zero;
						var after = i + 1 < route.Points.Count
							? (plane.World(route.Points[i + 1]) - world).normalized
							: Vector3.zero;
						if (before == Vector3.zero)
							before = after;
						if (after == Vector3.zero)
							after = before;
						var normalBefore = Vector3.Cross(plane.Normal, before).normalized;
						var normalAfter = Vector3.Cross(plane.Normal, after).normalized;
						// Стык ленты по биссектрисе сохраняет ширину на изломе её осевой линии.
						var miter = (normalBefore + normalAfter).normalized;
						float denominator = Mathf.Max(.25f, Mathf.Abs(Vector3.Dot(miter, normalAfter)));
						var offset = miter * (result.Width * .5f / denominator);
						vertices.Add(inverse.MultiplyPoint3x4(world - offset));
						vertices.Add(inverse.MultiplyPoint3x4(world + offset));
						if (k == 0)
							continue;
						int a = start + (k - 1) * 2;
						indices.Add(a);
						indices.Add(a + 1);
						indices.Add(a + 2);
						indices.Add(a + 1);
						indices.Add(a + 3);
						indices.Add(a + 2);
					}

					// Граничная пара вершин повторяется в следующем меше, чтобы лента не разрывалась.
					// Вершины разных потоков никогда не объединяются.
					first += count - 1;
					if (first < route.Points.Count - 1)
						Flush();
				}
			}

			Flush();

			void Flush()
			{
				if (vertices.Count == 0)
					return;
				var child = new GameObject("KAFO flows batch " + chunk++);
				child.transform.SetParent(parent, false);
				var filter = child.AddComponent<MeshFilter>();
				var renderer = child.AddComponent<MeshRenderer>();
				renderer.sharedMaterial = material;
				renderer.shadowCastingMode = ShadowCastingMode.Off;
				renderer.receiveShadows = false;
				KafoMeshResizer.ConfigureDynamicRenderer(renderer);
				var mesh = new Mesh {name = "KAFO flow ribbons", hideFlags = HideFlags.HideAndDontSave};
				mesh.SetVertices(vertices);
				mesh.SetTriangles(indices, 0);
				mesh.RecalculateNormals();
				mesh.RecalculateBounds();
				filter.sharedMesh = mesh;
				meshes.Add(mesh);
				foreach (var range in ranges)
				{
					range.Filter = filter;
					range.Renderer = renderer;
				}

				vertices.Clear();
				indices.Clear();
				ranges.Clear();
			}
		}
	}

	public sealed class InfoflowIndividualRenderer : IInfoflowRenderer
	{
		public void Render(Transform parent, InfoflowPlane plane, InfoflowRoutingResult result, Material material,
			List<InfoflowVisualRecord> registry, List<Mesh> meshes)
		{
			var frame = UnscaledFrame(parent);
			foreach (var route in result.Routes)
			{
				var child = new GameObject("KAFO flow " + route.Key);
				child.transform.SetParent(frame, false);
				child.transform.SetPositionAndRotation(plane.Origin, Quaternion.LookRotation(plane.Normal, plane.Z));
				var line = child.AddComponent<LineRenderer>();
				line.sharedMaterial = material;
				line.shadowCastingMode = ShadowCastingMode.Off;
				line.receiveShadows = false;
				line.useWorldSpace = false;
				line.alignment = LineAlignment.View;
				line.widthCurve = AnimationCurve.Constant(0, 1, 1);
				line.widthMultiplier = result.Width;
				line.numCapVertices = line.numCornerVertices = 0;
				// Точки хранятся относительно плоскости потока. Перенос и поворот
				// родителя перемещают всю линию без повторной генерации маршрута.
				var inverse = child.transform.worldToLocalMatrix;
				var points = new Vector3[route.Points.Count + 1];
				points[0] = inverse.MultiplyPoint3x4(plane.World(route.Points[0]));
				// Дополнительная точка на прямом выходе удерживает начальный торец LineRenderer от наклона к дуге.
				points[1] = inverse.MultiplyPoint3x4(
					plane.World(Vector2.Lerp(route.Points[0], route.Points[1], 1f / 3)));
				for (int i = 1; i < route.Points.Count; i++)
					points[i + 1] = inverse.MultiplyPoint3x4(plane.World(route.Points[i]));
				line.positionCount = points.Length;
				line.SetPositions(points);
				
				registry.Add(new InfoflowVisualRecord
				{
					RouteKey = route.Key, SourceIndex = route.SourceIndex,
					FlowId = route.FlowId, ProviderKey = route.ProviderKey, ConsumerKey = route.ConsumerKey,
					IndividualRenderer = line
				});
			}
		}

		// Исходные масштабы и повороты всей цепочки компенсируем, чтобы сохранить
		// толщину без перекоса. Положение рамки остаётся привязано к корню KAFO.
		static Transform UnscaledFrame(Transform parent)
		{
			var frame = parent;
			for (var ancestor = parent; ancestor != null; ancestor = ancestor.parent)
			{
				if (!ancestor.localScale.Equals(Vector3.one))
				{
					var scale = ancestor.localScale;
					frame = Child(frame, "Inverse scale");
					frame.localScale = new Vector3(1 / scale.x, 1 / scale.y, 1 / scale.z);
				}

				if (!ancestor.localRotation.Equals(Quaternion.identity))
				{
					frame = Child(frame, "Inverse rotation");
					frame.localRotation = Quaternion.Inverse(ancestor.localRotation);
				}
			}

			return frame;
		}

		static Transform Child(Transform parent, string name)
		{
			var child = new GameObject(name).transform;
			child.SetParent(parent, false);
			return child;
		}
	}
}
