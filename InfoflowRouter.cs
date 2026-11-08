using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace Sbrt.Av.Gameplay
{
    // Чистый расчёт: снимок сцены → пути → порядок дорожек → координаты → скругления.
    // Создание GameObject и чтение Transform остаются за пределами маршрутизатора.
    public static class InfoflowRouter
    {
        sealed class Passage
        {
            public float Low, High, Start, End;
            public bool HasExit;
            public readonly List<(FlowFamily Family, int Segment)> Segments = new List<(FlowFamily, int)>();
            public readonly HashSet<FlowFamily> Families = new HashSet<FlowFamily>();
        }

        public static InfoflowRoutingResult Calculate(InfoflowScene input, IReadOnlyList<InfoflowDataModel> flows,
            InfoflowSettings settings = null)
        {
            var watch = Stopwatch.StartNew();
            settings = settings ?? new InfoflowSettings();
            flows = flows ?? Array.Empty<InfoflowDataModel>();
            var result = new InfoflowRoutingResult { SourceCount = flows.Count };
            var scene = new InfoflowScene { Bounds = input.Bounds, RoutingBounds = input.RoutingBounds,
                CellSize = input.CellSize, BridgesAlongX = input.BridgesAlongX };
            scene.Endpoints.AddRange(input.Endpoints); scene.SoftAreas.AddRange(input.SoftAreas);
            scene.Obstacles.AddRange(input.Obstacles.Concat(input.Endpoints.Select(e => e.Body)).Distinct());

            // Разворачиваем серверные связи в физические линии и объединяем одинаковые пары подключений.
            var families = Expand(scene, flows, settings, result);
            if (families.Count == 0)
            {
                result.RoutingMilliseconds = watch.Elapsed.TotalMilliseconds;
                return result;
            }

            float cell = scene.CellSize;
            if (cell <= 0 || float.IsNaN(cell) || float.IsInfinity(cell))
                cell = scene.Endpoints.Min(e => Mathf.Min(e.Body.width, e.Body.height));
            float width = settings.MaxWidth;
            if (width <= 0 || float.IsNaN(width) || float.IsInfinity(width))
                throw new ArgumentOutOfRangeException(nameof(settings.MaxWidth), "Максимальная ширина должна быть положительным конечным числом.");
            if (settings.MinWidth < 0 || settings.MinWidth > width || float.IsNaN(settings.MinWidth) || float.IsInfinity(settings.MinWidth))
                throw new ArgumentOutOfRangeException(nameof(settings.MinWidth), "Минимальная ширина должна быть от 0 до максимальной ширины.");
            float gap = width * settings.GapRatio;
            if (settings.GapRatio < 0 || float.IsNaN(gap) || float.IsInfinity(gap))
                throw new ArgumentOutOfRangeException(nameof(settings.GapRatio), "Коэффициент зазора должен быть конечным неотрицательным числом.");
            float radius = cell * Mathf.Max(0, settings.RadiusInCells);
            var banks = families.SelectMany(f => new[] { f.Start, f.End }).Distinct().ToList();
            var graph = new InfoflowChannelGraph(scene, banks.Select(b => b.Point), cell * 2 + radius * 2);
            var obstacles = new InfoflowObstacleLookup(scene.Obstacles, cell);
            var clearSegments = new Dictionary<(Vector2 A, Vector2 B), bool>();
            int budget = Mathf.Max(0, settings.SearchBudget);

            // Сначала ищем короткий путь, затем пробуем общий проход без чрезмерного удлинения.
            foreach (var family in families.OrderByDescending(f => (f.Start.Point - f.End.Point).sqrMagnitude).ThenBy(f => f.Id))
            {
                var baseline = graph.FindPath(family.Start.Point, DirectionIndex(family.Start.Normal),
                    family.End.Point, DirectionIndex(family.End.Normal), ref budget, out bool fallback, false);
                family.Path = baseline;
                if (!fallback && budget > 0 && families.Count > 1)
                {
                    var shared = graph.FindPath(family.Start.Point, DirectionIndex(family.Start.Normal),
                        family.End.Point, DirectionIndex(family.End.Normal), ref budget, out bool sharedFallback);
                    if (!sharedFallback && Length(shared) <= Length(baseline) * 1.5f + cell * 2 && shared.Count <= baseline.Count + 2)
                        family.Path = shared;
                }
                family.Path = Shorten(family, obstacles, clearSegments);
                graph.MarkPath(family.Path);
                result.UsedFallback |= fallback;
            }

            // Сближаем совместимые пути, после чего назначаем общий порядок дорожек.
            AlignPassages(families, scene.Obstacles, graph.Frame, radius, settings.StraightExit);
            var network = PrepareNetwork(families);
            // Не более двух проходов по 16 конфликтующим семействам в пределах общего бюджета.
            for (int pass = 0; pass < 2 && budget > 0 && network.CrossingNodes.Count > 0; pass++)
            {
                var saved = families.ToDictionary(f => f.Id, f => new List<Vector2>(f.Path));
                var conflicts = network.CrossingNodes.OrderBy(n => n).ToList();
                var candidates = families.Where(f => f.Nodes.Any(conflicts.Contains))
                    .OrderByDescending(f => f.Wires.Count).ThenBy(f => f.Id).Take(16).ToList();
                foreach (var family in candidates)
                {
                    if (budget <= 0) break;
                    var junction = network.Nodes[family.Nodes.First(conflicts.Contains)].Point;
                    var avoid = new Rect(junction - Vector2.one * cell * .5f, Vector2.one * cell);
                    var route = graph.FindPath(family.Start.Point, DirectionIndex(family.Start.Normal),
                        family.End.Point, DirectionIndex(family.End.Normal), ref budget, out bool fallback, true, avoid);
                    if (!fallback) family.Path = route;
                }
                AlignPassages(families, scene.Obstacles, graph.Frame, radius, settings.StraightExit);
                var candidate = PrepareNetwork(families);
                if (candidate.CrossingNodes.Count < network.CrossingNodes.Count) network = candidate;
                else
                {
                    foreach (var family in families) family.Path = saved[family.Id];
                    network = PrepareNetwork(families);
                    break;
                }
            }
            var inputDiagnostics = result.Diagnostics.ToArray();
            List<string> errors;

            // Только после назначения порядка вычисляем размеры, координаты и дуги.
            try
            {
                BuildFittingGeometry(families, scene, graph, settings, width, gap, radius, result, ref budget, network);
                errors = InfoflowGeometry.Validate(scene, result);
            }
            catch (InvalidOperationException)
            {
                errors = new List<string> { "Ограничения выбранных проходов требуют резервной разводки." };
            }
            if (errors.Count > 0 && result.UsedCrossingFallback)
                errors = RepairCrossingRoutes(families, scene, graph, settings, radius, result, errors);
            if (errors.Count > 0)
            {
                // При ошибке один раз перестраиваем пути по заранее подготовленному дереву доступности.
                result.UsedFallback = true;
                foreach (var family in families)
                {
                    int noSearch = 0;
                    family.Path = graph.FindPath(family.Start.Point, DirectionIndex(family.Start.Normal),
                        family.End.Point, DirectionIndex(family.End.Normal), ref noSearch, out _);
                    family.Path = Shorten(family, obstacles, clearSegments);
                }
                AlignPassages(families, scene.Obstacles, graph.Frame, radius, settings.StraightExit);
                BuildFittingGeometry(families, scene, graph, settings, width, gap, radius, result, ref budget);
                errors = InfoflowGeometry.Validate(scene, result);
                if (errors.Count > 0 && result.UsedCrossingFallback)
                    errors = RepairCrossingRoutes(families, scene, graph, settings, radius, result, errors);
            }
            if (errors.Count == 0 && budget > 0 && families.Count > 1 && !result.UsedCrossingFallback)
                result = ImproveRouteChoices(families, graph, scene, settings, width, gap, radius, result, inputDiagnostics);
            if (errors.Count == 0 && result.UsedCapacityRouting && !result.UsedCrossingFallback && watch.Elapsed.TotalSeconds < 4)
                ImproveDirectConnections(families, scene, settings, radius, result, watch);
            result.SearchVisits = graph.Visits;
            result.Diagnostics.AddRange(errors);
            if (result.Scale < 1) result.Diagnostics.Add($"Общий масштаб толщины и интервалов: {result.Scale:F3}.");
            if (result.PlannedCrossings > 0) result.Diagnostics.Add($"Узлов с конфликтами порядка: {result.PlannedCrossings}.");
            if (result.UsedFallback) result.Diagnostics.Add("Часть маршрутов построена обязательным деревом проходов.");
            result.CellSize = cell;
            foreach (var route in result.Routes)
                route.Name = flows[route.SourceIndex].Name;
            result.RoutingMilliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        static List<string> RepairCrossingRoutes(List<FlowFamily> families, InfoflowScene scene, InfoflowChannelGraph graph,
            InfoflowSettings settings, float radius, InfoflowRoutingResult result, List<string> errors)
        {
            var keys = new HashSet<string>(errors.Where(e => e.Contains(": ")).Select(e => e.Substring(e.IndexOf(": ", StringComparison.Ordinal) + 2)));
            foreach (var wire in families.SelectMany(f => f.Wires).Where(w => keys.Contains(w.Route.Key)).ToList())
            {
                var family = wire.Family; var previous = wire.Route;
                var single = new FlowFamily { Id = 0, Start = family.Start, End = family.End, Path = family.Path };
                single.Wires.Add(wire); wire.Family = single;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    wire.Route = new InfoflowRoute { Key = previous.Key, SourceIndex = previous.SourceIndex, FlowId = previous.FlowId,
                        ProviderKey = previous.ProviderKey, ConsumerKey = previous.ConsumerKey,
                        ProviderSide = previous.ProviderSide, ConsumerSide = previous.ConsumerSide };
                    var one = new InfoflowRoutingResult();
                    try
                    {
                        if (attempt > 0)
                        {
                            int noSearch = 0;
                            single.Path = graph.FindPath(single.Start.Point, DirectionIndex(single.Start.Normal), single.End.Point,
                                DirectionIndex(single.End.Normal), ref noSearch, out _);
                        }
                        // Проверяем повреждённую линию отдельно, не перестраивая всю плотную сеть.
                        var isolated = new List<FlowFamily> { single };
                        BuildGeometry(isolated, scene, graph.Frame, settings, result.Width, result.Gap, radius, one,
                            PrepareNetwork(isolated, true), sizeFactor: result.Scale);
                        if (InfoflowGeometry.Validate(scene, one).Count != 0) continue;
                        result.Routes[result.Routes.FindIndex(r => r.Key == previous.Key)] = wire.Route;
                        break;
                    }
                    catch (InvalidOperationException) { }
                }
                if (!result.Routes.Contains(wire.Route)) wire.Route = previous;
                wire.Family = family;
            }
            return InfoflowGeometry.Validate(scene, result);
        }

        static void ImproveDirectConnections(List<FlowFamily> families, InfoflowScene scene, InfoflowSettings settings,
            float radius, InfoflowRoutingResult result, Stopwatch watch)
        {
            // Проверяем короткую связь целиком, включая обе дуги. Удачный перенос
            // меняет только её геометрию: соседние магистрали остаются на своих местах.
            var examined = new HashSet<FlowFamily>();
            for (int attempt = 0; attempt < 2 && watch.Elapsed.TotalSeconds < 4; attempt++)
            {
                var crossings = new InfoflowRouteAlternatives.Crossings(families, scene.CellSize);
                var family = families.Where(f => !examined.Contains(f) && f.Path.Count == 2 && f.Start.Normal == f.End.Normal &&
                    crossings.Counts[f] > 0).OrderByDescending(f => crossings.Counts[f]).ThenBy(f => f.Id).FirstOrDefault();
                if (family == null) break;
                examined.Add(family);
                var normal = family.Start.Normal;
                float oldHeight = family.Wires.Min(w => Vector2.Dot(w.Route.Corners[0], normal));
                float face = Mathf.Max(Vector2.Dot(family.Start.Point, normal), Vector2.Dot(family.End.Point, normal));
                var heights = new[] { oldHeight - result.Width - result.Gap, face + Mathf.Max(settings.StraightExit, result.Width * 2) };
                foreach (float height in heights.Distinct())
                {
                    if (height <= face + result.Width) continue;
                    var trial = new InfoflowRoutingResult { Width = result.Width, Gap = result.Gap };
                    foreach (var wire in family.Wires)
                    {
                        var source = wire.Route; var a = source.Points[0]; var b = source.Points[source.Points.Count - 1];
                        float offset = Vector2.Dot(source.Corners[0], normal) - oldHeight;
                        var c = a + normal * (height + offset - Vector2.Dot(a, normal));
                        var d = b + normal * (height + offset - Vector2.Dot(b, normal));
                        float available = Mathf.Min(Vector2.Distance(a, c), Vector2.Distance(b, d)) - result.Width * .6f;
                        float turn = Mathf.Max(0, Mathf.Min(radius + offset, Mathf.Min(available, Vector2.Distance(c, d) * .49f)));
                        var route = new InfoflowRoute { Key = source.Key, SourceIndex = source.SourceIndex, FlowId = source.FlowId,
                            ProviderKey = source.ProviderKey, ConsumerKey = source.ConsumerKey,
                            ProviderSide = source.ProviderSide, ConsumerSide = source.ConsumerSide,
                            Corners = new List<Vector2> { a, c, d, b } };
                        InfoflowGeometry.Round(route, new[] { 0f, turn, turn, 0f }, Mathf.Clamp(settings.ArcSteps, 4, 32));
                        trial.Routes.Add(route);
                    }
                    if (InfoflowGeometry.Validate(scene, trial).Count != 0 ||
                        trial.Routes.Sum(r => crossings.Count(r.Points, family)) >= crossings.Counts[family]) continue;
                    bool internalCrossing = false;
                    for (int a = 0; a < trial.Routes.Count && !internalCrossing; a++)
                    for (int b = a + 1; b < trial.Routes.Count && !internalCrossing; b++)
                    for (int i = 1; i < trial.Routes[a].Points.Count && !internalCrossing; i++)
                    for (int j = 1; j < trial.Routes[b].Points.Count; j++)
                        if (InfoflowGeometry.Intersection(trial.Routes[a].Points[i - 1], trial.Routes[a].Points[i],
                            trial.Routes[b].Points[j - 1], trial.Routes[b].Points[j], out _)) { internalCrossing = true; break; }
                    if (internalCrossing) continue;
                    foreach (var route in trial.Routes)
                    {
                        var wire = family.Wires.First(w => w.Route.Key == route.Key);
                        result.Routes[result.Routes.FindIndex(r => r.Key == route.Key)] = route; wire.Route = route;
                    }
                    break;
                }
            }
        }

        static void BuildFittingGeometry(List<FlowFamily> families, InfoflowScene scene, InfoflowChannelGraph graph,
            InfoflowSettings settings, float width, float gap, float radius, InfoflowRoutingResult result,
            ref int budget, InfoflowNetwork prepared = null)
        {
            try { BuildGeometry(families, scene, graph.Frame, settings, width, gap, radius, result, prepared); return; }
            catch (InfoflowPassageCapacityException) { }
            // Не переносим по одной линии вслепую: заново распределяем проходы,
            // резервируя место при минимальной разрешённой ширине до размещения координат.
            float reservedWidth = settings.MinWidth > 0 ? settings.MinWidth : Mathf.Min(width, scene.CellSize * .02f);
            graph.ClearUsage();
            var reservations = new InfoflowChannelGraph.Reservations(graph, reservedWidth,
                reservedWidth * settings.GapRatio, Mathf.Max(0, settings.Clearance));
            var pending = families.OrderByDescending(f => f.Wires.Count)
                .ThenByDescending(f => Length(f.Path)).ThenBy(f => f.Id).ToList();
            foreach (var family in pending)
            {
                int remaining = family.Wires.Count;
                while (remaining > 0)
                {
                    int count = remaining;
                    List<Vector2> path;
                    while (true)
                    {
                        try
                        {
                            path = graph.FindPath(graph.AccessPoint(family.Start, reservedWidth, settings.StraightExit), DirectionIndex(family.Start.Normal),
                                graph.AccessPoint(family.End, reservedWidth, settings.StraightExit), DirectionIndex(family.End.Normal), ref budget, out _,
                                preferShared: false, reservations: reservations, lineCount: count, accessPoints: true);
                            break;
                        }
                        catch (InvalidOperationException) when (count > 1) { count = Math.Max(1, count / 2); }
                        catch (InvalidOperationException)
                        {
                            // Исчерпание вместимости не отменяет последнюю линию.
                            // Оставляем доступный путь; конфликт разных потоков разрешается в свободных узлах.
                            int noSearch = 0;
                            path = graph.FindPath(graph.AccessPoint(family.Start, reservedWidth, settings.StraightExit), DirectionIndex(family.Start.Normal),
                                graph.AccessPoint(family.End, reservedWidth, settings.StraightExit), DirectionIndex(family.End.Normal), ref noSearch, out _,
                                preferShared: false, accessPoints: true);
                            break;
                        }
                    }
                    var target = family;
                    if (count < family.Wires.Count)
                    {
                        target = new FlowFamily { Id = families.Max(f => f.Id) + 1, Start = family.Start, End = family.End };
                        var wires = family.Wires.Skip(family.Wires.Count - count).ToList();
                        family.Wires.RemoveRange(family.Wires.Count - count, count);
                        foreach (var wire in wires) { wire.Family = target; target.Wires.Add(wire); }
                        families.Add(target);
                    }
                    target.Path = path;
                    reservations.Reserve(path, count); graph.MarkPath(path);
                    remaining -= count;
                }
            }
            // Размещаем ровно ту ширину, для которой зарезервированы проходы.
            try
            {
                BuildGeometry(families, scene, graph.Frame, settings, reservedWidth, reservedWidth * settings.GapRatio,
                    radius, result, PrepareNetwork(families, true), sizeFactor: reservedWidth / width);
            }
            catch (InfoflowPassageCapacityException)
            {
                // При минимальной ширине убираем пустоты повторным использованием дорожек,
                // согласуя их порядок до размещения координат и построения дуг.
                try
                {
                    BuildGeometry(families, scene, graph.Frame, settings, reservedWidth, reservedWidth * settings.GapRatio,
                        radius, result, PrepareNetwork(families, true, true), sizeFactor: reservedWidth / width);
                }
                catch (InfoflowPassageCapacityException)
                {
                    BuildGeometry(families, scene, graph.Frame, settings, reservedWidth, reservedWidth * settings.GapRatio,
                        radius, result, PrepareNetwork(families, true, false, true), sizeFactor: reservedWidth / width);
                    result.Diagnostics.Add("Магистрали разделены в свободных узлах по вместимости проходов.");
                }
            }
            result.UsedCapacityRouting = true;
            result.Diagnostics.Add("Проходы распределены по вместимости при минимальной ширине.");
        }

        static InfoflowRoutingResult ImproveRouteChoices(List<FlowFamily> families, InfoflowChannelGraph graph,
            InfoflowScene scene, InfoflowSettings settings, float width, float gap, float radius,
            InfoflowRoutingResult result, string[] inputDiagnostics)
        {
            // Два прохода, не больше двух кандидатов на проход. Сравниваем готовые линии с дугами,
            // а не только конфликты порядка и занятость графа во время первоначального поиска.
            // Следующая попытка рассматривает другие группы, а не повторяет первые 16.
            var examined = new HashSet<FlowFamily>();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var crossings = new InfoflowRouteAlternatives.Crossings(families, scene.CellSize);
                if (crossings.Total == 0) break;
                var changes = InfoflowRouteAlternatives.Find(families, graph, scene, settings, result.Width, radius, crossings, examined,
                    result.UsedCapacityRouting);
                foreach (var change in changes)
                {
                    var oldPath = change.Family.Path;
                    var routes = families.SelectMany(f => f.Wires).ToDictionary(w => w, w => w.Route);
                    // Пробная геометрия не меняет сохранённые маршруты: неудачная альтернатива
                    // откатывается целиком, включая дорожки остальных линий и общий масштаб.
                    foreach (var wire in routes.Keys)
                    {
                        var source = wire.Route;
                        wire.Route = new InfoflowRoute { Key = source.Key, SourceIndex = source.SourceIndex, FlowId = source.FlowId,
                            ProviderKey = source.ProviderKey, ConsumerKey = source.ConsumerKey,
                            ProviderSide = source.ProviderSide, ConsumerSide = source.ConsumerSide };
                    }
                    change.Family.Path = change.Path;
                    var trial = new InfoflowRoutingResult { SourceCount = result.SourceCount,
                        SkippedRecords = result.SkippedRecords, UsedFallback = result.UsedFallback, UsedCapacityRouting = result.UsedCapacityRouting };
                    trial.Diagnostics.AddRange(inputDiagnostics);
                    bool accepted = false;
                    try
                    {
                        if (result.UsedCapacityRouting)
                            BuildGeometry(families, scene, graph.Frame, settings, result.Width, result.Gap, radius, trial,
                                PrepareNetwork(families, true, true), sizeFactor: result.Scale);
                        else BuildGeometry(families, scene, graph.Frame, settings, width, gap, radius, trial);
                        // Опциональный обход не должен покупать улучшение почти незаметными лентами.
                        accepted = (trial.Scale >= result.Scale || Mathf.Approximately(trial.Scale, result.Scale)) &&
                            InfoflowGeometry.Validate(scene, trial).Count == 0 &&
                            new InfoflowRouteAlternatives.Crossings(families, scene.CellSize).Total < crossings.Total;
                    }
                    catch (InvalidOperationException) { /* Неудачная проба не отменяет уже проложенные связи. */ }
                    if (accepted) { result = trial; break; }
                    else
                    {
                        change.Family.Path = oldPath;
                        foreach (var pair in routes) pair.Key.Route = pair.Value;
                    }
                }
            }
            return result;
        }

        static List<FlowFamily> Expand(InfoflowScene scene, IReadOnlyList<InfoflowDataModel> flows,
            InfoflowSettings settings, InfoflowRoutingResult result)
        {
            var byId = scene.Endpoints.Select((e, i) => (e, i)).GroupBy(p => p.e.AsId)
                .ToDictionary(g => g.Key, g => g.Select(p => p.i).ToList());
            var counts = new int[scene.Endpoints.Count];
            var banks = new Dictionary<(int Endpoint, InfoflowPortSide Side, int Slot), FlowBank>();
            var families = new Dictionary<(int, int), FlowFamily>();
            int wireId = 0;
            var primary = settings.PreferredSide == InfoflowPortSide.Auto
                ? (scene.BridgesAlongX ? InfoflowPortSide.PositiveZ : InfoflowPortSide.PositiveX) : settings.PreferredSide;
            for (int source = 0; source < flows.Count; source++)
            {
                var flow = flows[source];
                if (flow == null || !byId.TryGetValue(flow.ProviderId, out var providers) || !byId.TryGetValue(flow.ConsumerId, out var consumers))
                {
                    result.SkippedRecords++; result.Diagnostics.Add("Нет конца связи KAFO, запись " + source + "."); continue;
                }
                foreach (int provider in providers)
                foreach (int consumer in consumers)
                {
                    // Счётчик общий для входящих и исходящих подключений; петля занимает два порта.
                    var sideA = counts[provider]++ < settings.PrimaryPortCount ? primary : Opposite(primary);
                    var sideB = counts[consumer]++ < settings.PrimaryPortCount ? primary : Opposite(primary);
                    bool loop = provider == consumer && sideA == sideB;
                    var a = Bank(provider, sideA, loop ? -1 : 0);
                    var b = Bank(consumer, sideB, loop ? 1 : 0);
                    // Направление передачи данных не меняет геометрическое семейство встречных связей.
                    var pair = (Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id));
                    if (!families.TryGetValue(pair, out var family))
                    {
                        family = new FlowFamily { Id = families.Count, Start = a.Id < b.Id ? a : b, End = a.Id < b.Id ? b : a };
                        families.Add(pair, family);
                    }
                    // Серверный Id может повторяться, поэтому ключ включает индекс записи и оба экземпляра.
                    var wire = new FlowWire
                    {
                        Id = wireId++, Provider = a, Consumer = b, Family = family,
                        Reversed = a != family.Start,
                        Route = new InfoflowRoute
                        {
                            Key = "flow:" + source + ":" + provider + ":" + consumer,
                            SourceIndex = source, FlowId = flow.Id,
                            ProviderKey = scene.Endpoints[provider].Key, ConsumerKey = scene.Endpoints[consumer].Key,
                            ProviderSide = sideA, ConsumerSide = sideB
                        }
                    };
                    family.Wires.Add(wire);
                }
            }
            return families.Values.OrderBy(f => f.Id).ToList();

            FlowBank Bank(int endpointIndex, InfoflowPortSide side, int slot)
            {
                var key = (endpointIndex, side, slot);
                if (banks.TryGetValue(key, out var bank)) return bank;
                var endpoint = scene.Endpoints[endpointIndex]; var body = endpoint.Body;
                var area = endpoint.PortArea ?? body;
                bool horizontal = side == InfoflowPortSide.PositiveX || side == InfoflowPortSide.NegativeX;
                float border = Mathf.Clamp(endpoint.Border, .05f, .45f);
                float low = horizontal ? area.yMin + area.height * border : area.xMin + area.width * border;
                float high = horizontal ? area.yMax - area.height * border : area.xMax - area.width * border;
                float middle = (low + high) * .5f;
                // Петле на одной грани выделяем разные половины допустимой области подключения.
                if (slot < 0) high = middle; else if (slot > 0) low = middle;
                float tangent = (low + high) * .5f;
                var normal = Normal(side);
                var point = horizontal ? new Vector2(normal.x > 0 ? body.xMax : body.xMin, tangent)
                    : new Vector2(tangent, normal.y > 0 ? body.yMax : body.yMin);
                bank = new FlowBank { Id = banks.Count, Endpoint = endpointIndex, Side = side,
                    Point = point, Normal = normal, Low = low, High = high };
                banks.Add(key, bank); return bank;
            }
        }

        static List<Vector2> Shorten(FlowFamily family, InfoflowObstacleLookup obstacles,
            Dictionary<(Vector2 A, Vector2 B), bool> clearSegments)
        {
            // Убираем обходы, сохраняя выход по нормали к грани и проверяя сокращение на препятствия.
            var path = InfoflowGeometry.RemoveLoops(family.Path);
            var result = new List<Vector2> { path[0] };
            for (int i = 0; i < path.Count - 1;)
            {
                bool found = false;
                for (int j = path.Count - 1; j > i + 1; j--)
                {
                    foreach (var bend in new[] { new Vector2(path[i].x, path[j].y), new Vector2(path[j].x, path[i].y) })
                    {
                        var candidate = InfoflowGeometry.Simplify(new List<Vector2> { path[i], bend, path[j] });
                        if (candidate.Count < 2) continue;
                        if (i == 0 && Vector2.Dot((candidate[1] - candidate[0]).normalized, family.Start.Normal) < .5f) continue;
                        if (j == path.Count - 1 && Vector2.Dot((candidate[candidate.Count - 2] - candidate[candidate.Count - 1]).normalized,
                            family.End.Normal) < .5f) continue;
                        bool safe = true;
                        for (int k = 1; k < candidate.Count && safe; k++)
                        {
                            var a = candidate[k - 1]; var b = candidate[k];
                            // Общие и резервные пути повторяют одни и те же отрезки.
                            // Корпуса неподвижны в течение расчёта, поэтому ответ можно сохранить.
                            if (!clearSegments.TryGetValue((a, b), out safe))
                            {
                                safe = !obstacles.Query(a, b, 0).Any(r => InfoflowGeometry.SegmentHitsRect(a, b, r));
                                clearSegments.Add((a, b), safe);
                            }
                        }
                        if (!safe) continue;
                        result.AddRange(candidate.Skip(1)); i = j; found = true; break;
                    }
                    if (found) break;
                }
                if (!found) result.Add(path[++i]);
            }
            return InfoflowGeometry.RemoveLoops(result);
        }

        static void AlignPassages(List<FlowFamily> families, List<Rect> obstacles, Rect frame, float radius, float exitLength)
        {
            // Пути можно выровнять вместе, только если у них пересекаются свободные диапазоны.
            foreach (bool horizontal in new[] { false, true })
            {
                var passages = new List<Passage>();
                foreach (var family in families.OrderBy(f => f.Id))
                for (int i = 1; i + 2 < family.Path.Count; i++)
                {
                    var a = family.Path[i]; var b = family.Path[i + 1];
                    if ((a.y == b.y) != horizontal) continue;
                    float coordinate = horizontal ? a.y : a.x;
                    float start = horizontal ? Mathf.Min(a.x, b.x) : Mathf.Min(a.y, b.y);
                    float end = horizontal ? Mathf.Max(a.x, b.x) : Mathf.Max(a.y, b.y);
                    float low = horizontal ? frame.yMin : frame.xMin, high = horizontal ? frame.yMax : frame.xMax;
                    foreach (var r in obstacles)
                    {
                        float near = horizontal ? r.xMin : r.yMin, far = horizontal ? r.xMax : r.yMax;
                        if (end < near || start > far) continue;
                        float lower = horizontal ? r.yMin : r.xMin, upper = horizontal ? r.yMax : r.xMax;
                        if (coordinate <= lower) high = Mathf.Min(high, lower);
                        else if (coordinate >= upper) low = Mathf.Max(low, upper);
                    }
                    if (i == 1) KeepExit(family.Start);
                    if (i == family.Path.Count - 3) KeepExit(family.End);
                    if (high <= low) continue;
                    var group = passages.FirstOrDefault(p => end > p.Start && start < p.End &&
                        Mathf.Min(high, p.High) > Mathf.Max(low, p.Low) && !p.Families.Contains(family));
                    if (group == null)
                    {
                        group = new Passage { Low = low, High = high, Start = start, End = end };
                        passages.Add(group);
                    }
                    else
                    {
                        group.Low = Mathf.Max(low, group.Low); group.High = Mathf.Min(high, group.High);
                        group.Start = Mathf.Min(start, group.Start); group.End = Mathf.Max(end, group.End);
                    }
                    group.Segments.Add((family, i));
                    group.Families.Add(family);
                    group.HasExit |= i == 1 || i == family.Path.Count - 3;

                    void KeepExit(FlowBank bank)
                    {
                        float sign = horizontal ? bank.Normal.y : bank.Normal.x;
                        float face = horizontal ? bank.Point.y : bank.Point.x;
                        float desired = radius + Mathf.Max(0, exitLength);
                        if (sign > 0) low = Mathf.Max(low, Mathf.Min(face + desired, high - (high - low) * .1f));
                        else if (sign < 0) high = Mathf.Min(high, Mathf.Max(face - desired, low + (high - low) * .1f));
                    }
                }
                // В этом проходе меняется только одна координата. Диапазоны по другой оси
                // остаются прежними, поэтому индекс отрезков не надо обновлять после переносов.
                float bucketSize = Mathf.Max(radius, Mathf.Max(frame.width, frame.height) / Mathf.Sqrt(Mathf.Max(1, families.Count)));
                var buckets = new Dictionary<int, List<(FlowFamily Family, int Segment)>>();
                var visited = new HashSet<(FlowFamily Family, int Segment)>();
                foreach (var family in families)
                for (int i = 0; i + 1 < family.Path.Count; i++)
                {
                    float a = horizontal ? family.Path[i].x : family.Path[i].y;
                    float b = horizontal ? family.Path[i + 1].x : family.Path[i + 1].y;
                    for (int bin = Mathf.FloorToInt(Mathf.Min(a, b) / bucketSize); bin <= Mathf.FloorToInt(Mathf.Max(a, b) / bucketSize); bin++)
                    {
                        if (!buckets.TryGetValue(bin, out var edges)) buckets.Add(bin, edges = new List<(FlowFamily, int)>());
                        edges.Add((family, i));
                    }
                }
                foreach (var passage in passages)
                {
                    float padding = passage.HasExit ? 0 : Mathf.Min(radius, (passage.High - passage.Low) * .25f);
                    float desired = passage.Segments.Average(s => horizontal ? s.Family.Path[s.Segment].y : s.Family.Path[s.Segment].x);
                    if (passage.High == (horizontal ? frame.yMax : frame.xMax)) desired = passage.Low + padding;
                    else if (passage.Low == (horizontal ? frame.yMin : frame.xMin)) desired = passage.High - padding;
                    float value = Mathf.Clamp(desired, passage.Low + padding, passage.High - padding);

                    // Проверяем весь пучок до изменения точек. Перенос не должен разворачивать
                    // соседние звенья, вытягивать лишний обход или создавать новые пересечения.
                    var moving = passage.Segments.ToDictionary(s => s.Family, s => s.Segment);
                    bool canAlign = true;
                    foreach (var segment in passage.Segments)
                    {
                        var path = segment.Family.Path;
                        int i = segment.Segment;
                        var a = path[i]; var b = path[i + 1];
                        if (horizontal) { a.y = value; b.y = value; } else { a.x = value; b.x = value; }
                        if (Vector2.Dot(path[i] - path[i - 1], a - path[i - 1]) <= 0 ||
                            Vector2.Dot(path[i + 2] - path[i + 1], path[i + 2] - b) <= 0)
                        {
                            canAlign = false;
                            break;
                        }
                        float before = Vector2.Distance(path[i - 1], path[i]);
                        float after = Vector2.Distance(path[i + 1], path[i + 2]);
                        float requiredExit = radius + Mathf.Max(0, exitLength);
                        // Удлинение оправдано только недостающим местом для выхода у своей АС.
                        float allowance = (i == 1 ? Mathf.Max(0, requiredExit - before) : 0) +
                            (i == path.Count - 3 ? Mathf.Max(0, requiredExit - after) : 0);
                        float limit = before + after + allowance;
                        float length = Vector2.Distance(path[i - 1], a) + Vector2.Distance(b, path[i + 2]);
                        if (length > limit && !Mathf.Approximately(length, limit) ||
                            AddsCrossing(segment.Family, i - 1, path[i - 1], a) ||
                            AddsCrossing(segment.Family, i, a, b) ||
                            AddsCrossing(segment.Family, i + 1, b, path[i + 2]))
                        {
                            canAlign = false;
                            break;
                        }
                    }
                    if (!canAlign) continue;

                    foreach (var segment in passage.Segments)
                    {
                        var a = segment.Family.Path[segment.Segment]; var b = segment.Family.Path[segment.Segment + 1];
                        if (horizontal) { a.y = value; b.y = value; } else { a.x = value; b.x = value; }
                        segment.Family.Path[segment.Segment] = a; segment.Family.Path[segment.Segment + 1] = b;
                    }

                    bool AddsCrossing(FlowFamily family, int edge, Vector2 a, Vector2 b)
                    {
                        var path = family.Path;
                        if (a.Equals(path[edge]) && b.Equals(path[edge + 1])) return false;
                        visited.Clear();
                        float start = horizontal ? a.x : a.y, end = horizontal ? b.x : b.y;
                        for (int bin = Mathf.FloorToInt(Mathf.Min(start, end) / bucketSize); bin <= Mathf.FloorToInt(Mathf.Max(start, end) / bucketSize); bin++)
                        {
                            if (!buckets.TryGetValue(bin, out var edges)) continue;
                            foreach (var other in edges)
                            {
                                if (!visited.Add(other) || other.Family == family && Math.Abs(other.Segment - edge) <= 1) continue;
                                // Внутри переносимого пучка центральные звенья коллинеарны,
                                // а соседние подходят к ним перпендикулярно: поперечного пересечения нет.
                                if (moving.TryGetValue(other.Family, out int middle) && Math.Abs(other.Segment - middle) <= 1) continue;
                                var c = other.Family.Path[other.Segment]; var d = other.Family.Path[other.Segment + 1];
                                if (CrossesInside(a, b, c, d) && !CrossesInside(path[edge], path[edge + 1], c, d)) return true;
                            }
                        }
                        return false;
                    }
                }
            }
            foreach (var family in families) family.Path = InfoflowGeometry.RemoveLoops(family.Path);

            bool CrossesInside(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
            {
                // Общие концы предварительных путей — узлы магистрали, их разведёт назначение дорожек.
                return InfoflowGeometry.Intersection(a, b, c, d, out var hit) &&
                    !hit.Equals(a) && !hit.Equals(b) && !hit.Equals(c) && !hit.Equals(d);
            }
        }

        static void BuildGeometry(List<FlowFamily> families, InfoflowScene scene, Rect frame, InfoflowSettings settings,
            float width, float gap, float radius, InfoflowRoutingResult result, InfoflowNetwork prepared = null,
            int capacityPass = 0, float sizeFactor = 1, InfoflowLanePlacement placement = null)
        {
            var network = prepared ?? PrepareNetwork(families);
            // При подборе масштаба сеть и ограничения остаются прежними.
            // Повторно используем их, заново вычисляя только зависящие от размеров координаты.
            placement = placement ?? new InfoflowLanePlacement(network, scene, frame, Mathf.Max(0, settings.Clearance),
                radius, Mathf.Max(settings.StraightExit, settings.MinWidth * 2));
            float scale = placement.Place(width, gap, settings.MinWidth / width);
            result.UsedCrossingFallback = network.CrossingFallback;
            result.Width = Mathf.Clamp(width * scale, settings.MinWidth, width / sizeFactor);
            result.Scale = result.Width / (width / sizeFactor);
            result.Gap = result.Width * Mathf.Max(0, settings.GapRatio);
            // Gap — зазор между краями лент, pitch — расстояние между их осевыми линиями.
            float pitch = result.Width + result.Gap;
            var wires = families.SelectMany(f => f.Wires).ToList();
            foreach (var wire in wires)
            {
                var path = network.WirePath(wire, pitch);
                var nodes = path.Select((p, i) => (Point: p, Node: wire.CornerNodes[i]))
                    .GroupBy(p => p.Point).ToDictionary(g => g.Key, g => g.First().Node);
                wire.Route.Corners = InfoflowGeometry.RemoveLoops(path);
                wire.CornerNodes.Clear();
                foreach (var point in wire.Route.Corners)
                    wire.CornerNodes.Add(nodes.TryGetValue(point, out int node) ? node : -1);
                wire.Radii = new float[wire.Route.Corners.Count];
            }
            // Общий поворот определяется узлом и парой проходов, независимо от направления линии.
            var turns = new Dictionary<(int Node, int A, int B), List<(FlowWire Wire, int Corner)>>();
            foreach (var wire in wires)
            for (int i = 1; i + 1 < wire.Route.Corners.Count; i++)
            {
                int node = wire.CornerNodes[i];
                var before = (wire.Route.Corners[i] - wire.Route.Corners[i - 1]).normalized;
                var after = (wire.Route.Corners[i + 1] - wire.Route.Corners[i]).normalized;
                if (Mathf.Abs(Vector3.Cross(before, after).z) < .5f) continue;
                var family = wire.Family;
                int index = node >= 0 ? family.Nodes.IndexOf(node) : -1;
                int a = index > 0 ? family.Edges[index - 1].Id : -1;
                int b = index >= 0 && index < family.Edges.Count ? family.Edges[index].Id : -1;
                var key = node >= 0 ? (node, Math.Min(a, b), Math.Max(a, b)) : (-1, wire.Id, i);
                if (!turns.TryGetValue(key, out var list)) turns.Add(key, list = new List<(FlowWire, int)>());
                list.Add((wire, i));
            }
            float turnCapacity = 1;
            var turnPlans = new List<(List<(FlowWire Wire, int Corner)> Group, Vector2 Inner, Vector2 Diagonal, float Radius)>();
            foreach (var group in turns.Values)
            {
                var first = group[0]; var path = first.Wire.Route.Corners;
                var incoming = (path[first.Corner] - path[first.Corner - 1]).normalized;
                var outgoing = (path[first.Corner + 1] - path[first.Corner]).normalized;
                var diagonal = incoming - outgoing;
                group.Sort((a, b) => Vector2.Dot(a.Wire.Route.Corners[a.Corner], diagonal)
                    .CompareTo(Vector2.Dot(b.Wire.Route.Corners[b.Corner], diagonal)));
                var inner = group[0].Wire.Route.Corners[group[0].Corner];
                float baseRadius = radius;
                // Внешние дорожки получают добавку к радиусу: так дуги общего поворота остаются вложенными.
                foreach (var item in group)
                {
                    var p = item.Wire.Route.Corners;
                    float offset = Vector2.Dot(p[item.Corner] - inner, diagonal) * .5f;
                    float before = Vector2.Distance(p[item.Corner - 1], p[item.Corner]);
                    float after = Vector2.Distance(p[item.Corner + 1], p[item.Corner]);
                    before = item.Corner == 1 ? before - Mathf.Min(settings.StraightExit, before * .1f) : before * .49f;
                    after = item.Corner == p.Count - 2 ? after - Mathf.Min(settings.StraightExit, after * .1f) : after * .49f;
                    baseRadius = Mathf.Min(baseRadius, Mathf.Min(before, after) - offset);
                    if (offset > 0) turnCapacity = Mathf.Min(turnCapacity, Mathf.Min(before, after) * .9f / offset);
                }
                baseRadius = Mathf.Max(0, baseRadius);
                turnPlans.Add((group, inner, diagonal, baseRadius));
            }
            if (turnCapacity < 1 && capacityPass < 4 && result.Width > settings.MinWidth)
            {
                // До подбора общего масштаба проверка дуг всё равно была бы отброшена.
                float factor = Mathf.Max(settings.MinWidth / width, Mathf.Max(.05f, turnCapacity * .8f));
                BuildGeometry(families, scene, frame, settings, width * factor, gap * factor, radius,
                    result, network, capacityPass + 1, sizeFactor * factor, placement);
                return;
            }
            if (turnCapacity < 1 && result.Width <= settings.MinWidth && !network.CompactTracks)
                throw new InfoflowPassageCapacityException();
            int reduced = 0;
            var obstacles = new InfoflowObstacleLookup(scene.Obstacles, scene.CellSize);
            foreach (var plan in turnPlans)
            {
                var group = plan.Group; var inner = plan.Inner; var diagonal = plan.Diagonal;
                float baseRadius = plan.Radius;
                float proposed = baseRadius;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    bool safe = true;
                    foreach (var item in group)
                    {
                        var p = item.Wire.Route.Corners; int i = item.Corner;
                        float offset = Vector2.Dot(p[i] - inner, diagonal) * .5f;
                        float r = Mathf.Max(0, baseRadius + offset);
                        if (network.CompactTracks) r = Mathf.Min(r, Mathf.Min(Vector2.Distance(p[i - 1], p[i]), Vector2.Distance(p[i + 1], p[i])) * .49f);
                        var d = (p[i] - p[i - 1]).normalized; var e = (p[i + 1] - p[i]).normalized;
                        var center = p[i] - d * r + e * r;
                        float angle = Mathf.Atan2(-e.y, -e.x), turn = Vector3.Cross(d, e).z;
                        var previous = p[i] - d * r;
                        for (int k = 1; k <= settings.ArcSteps && safe; k++)
                        {
                            float theta = angle + turn * Mathf.PI * .5f * k / settings.ArcSteps;
                            var next = center + new Vector2(Mathf.Cos(theta), Mathf.Sin(theta)) * r;
                            safe = !obstacles.Query(previous, next, result.Width * .5f)
                                .Any(o => InfoflowGeometry.SegmentHitsRect(previous, next, o, result.Width * .5f));
                            previous = next;
                        }
                        if (!safe) break;
                    }
                    if (safe) break;
                    baseRadius *= .5f;
                }
                if (baseRadius < radius * .9f || proposed < radius * .9f) reduced++;
                foreach (var item in group)
                {
                    var p = item.Wire.Route.Corners; int i = item.Corner;
                    float r = Mathf.Max(0, baseRadius + Vector2.Dot(p[i] - inner, diagonal) * .5f);
                    if (network.CompactTracks) r = Mathf.Min(r, Mathf.Min(Vector2.Distance(p[i - 1], p[i]), Vector2.Distance(p[i + 1], p[i])) * .49f);
                    item.Wire.Radii[i] = r;
                }
            }
            result.Routes.Clear();
            foreach (var wire in wires.OrderBy(w => w.Id))
            {
                InfoflowGeometry.Round(wire.Route, wire.Radii, Mathf.Clamp(settings.ArcSteps, 4, 32));
                wire.Route.StartFanPointCount = wire.Route.EndFanPointCount = 0;
            }
            FitPortConnections(wires, result.Width, Mathf.Clamp(settings.ArcSteps, 4, 32), result, radius, network.CompactTracks);
            foreach (var wire in wires.OrderBy(w => w.Id))
            {
                if (wire.Reversed)
                {
                    // Возвращаем направление поставщик → потребитель после геометрического упорядочивания.
                    wire.Route.Corners.Reverse(); wire.Route.Points.Reverse(); wire.Route.Arcs.Reverse();
                    foreach (var arc in wire.Route.Arcs) { arc.StartAngle += arc.Sweep; arc.Sweep = -arc.Sweep; }
                    (wire.Route.StartFanPointCount, wire.Route.EndFanPointCount) =
                        (wire.Route.EndFanPointCount, wire.Route.StartFanPointCount);
                }
                if (network.CompactTracks)
                {
                    var points = wire.Route.Points;
                    var startFan = FanBounds(points.Take(wire.Route.StartFanPointCount));
                    var endFan = FanBounds(points.Skip(points.Count - wire.Route.EndFanPointCount));
                    wire.Route.Points = InfoflowGeometry.RemoveLoops(points);
                    if (wire.Route.StartFanPointCount > 0) wire.Route.StartFanPointCount = wire.Route.Points.TakeWhile(p => Inside(startFan, p)).Count();
                    if (wire.Route.EndFanPointCount > 0) wire.Route.EndFanPointCount = wire.Route.Points.AsEnumerable().Reverse().TakeWhile(p => Inside(endFan, p)).Count();
                }
                result.Routes.Add(wire.Route);
            }
            result.PlannedCrossings = network.CrossingNodes.Count;
            if (network.CrossingFallback) result.Diagnostics.Add("Другие проходы исчерпаны: в перегруженных участках допускаются пересечения, наложения и уменьшение зазора; ширина сохранена.");
            if (reduced > 0) result.Diagnostics.Add("Радиус ограничен геометрией прохода в узлах: " + reduced + ".");

            Rect FanBounds(IEnumerable<Vector2> points)
            {
                if (!points.Any()) return new Rect();
                var low = points.Aggregate(Vector2.Min); var high = points.Aggregate(Vector2.Max);
                return Rect.MinMaxRect(low.x, low.y, high.x, high.y);
            }
            bool Inside(Rect r, Vector2 p) => p.x >= r.xMin && p.x <= r.xMax && p.y >= r.yMin && p.y <= r.yMax;
        }

        static void FitPortConnections(List<FlowWire> wires, float width, int steps, InfoflowRoutingResult result, float radius, bool compact)
        {
            var ends = wires.SelectMany(w => new[] { (Wire: w, Bank: w.Family.Start, Start: true),
                (Wire: w, Bank: w.Family.End, Start: false) });
            int compressed = 0;
            foreach (var bankEnds in ends.GroupBy(e => e.Bank))
            {
                var bank = bankEnds.Key;
                bool x = bank.Normal.y != 0;
                var ports = bankEnds.Select(e =>
                {
                    var points = e.Wire.Route.Points;
                    var p = e.Start ? points[0] : points[points.Count - 1];
                    float tangent = x ? p.x : p.y;
                    var next = e.Start ? points[1] : points[points.Count - 2];
                    // Сначала отделяем непосредственные повороты от продолжающихся прямо.
                    // Дальний поворот не должен переставлять дорожки на ближнем подключении.
                    float departure = x ? next.x - p.x : next.y - p.y;
                    return (e.Wire, e.Start, Tangent: tangent, Offset: Vector2.Dot(p - bank.Point, bank.Normal), Side: Math.Sign(departure));
                }).ToList();
                if (compact)
                    ports = ports.OrderBy(p => p.Side).ThenBy(p => p.Side == 0 ? p.Tangent : p.Side < 0 ? p.Offset : -p.Offset)
                        .ThenBy(p => p.Tangent).ThenBy(p => p.Wire.Id).ToList();
                float low = ports.Min(p => p.Tangent), high = ports.Max(p => p.Tangent);
                float availableLow = bank.Low + width * .5f, availableHigh = bank.High - width * .5f;
                bool fits = (low >= availableLow || Mathf.Approximately(low, availableLow)) &&
                    (high <= availableHigh || Mathf.Approximately(high, availableHigh));
                if (!compact && fits && ports.All(p => Mathf.Approximately(p.Offset, 0))) continue;
                if (availableLow > availableHigh) availableLow = availableHigh = (bank.Low + bank.High) * .5f;
                float pitch = width + result.Gap;
                float portStep = ports.Count > 1 ? Mathf.Min(pitch, (availableHigh - availableLow) / (ports.Count - 1)) : 0;
                if (compact ? ports.Count > 1 && portStep < pitch : !fits) compressed++;
                var connections = ports.Select((port, index) => compact
                    ? (availableLow + availableHigh) * .5f + (index - (ports.Count - 1) * .5f) * portStep
                    : fits ? port.Tangent : Mathf.Lerp(availableLow, availableHigh,
                        high > low ? (port.Tangent - low) / (high - low) : .5f)).ToArray();
                var turnCenters = new Dictionary<int, float>();
                if (compact)
                {
                    // Виртуальный порт может сразу начинать горизонтальную магистраль.
                    // Соединяемся дальше по этой прямой: подход сбоку к самому виртуальному
                    // порту заставлял кривую сначала выгибаться в обратную сторону.
                    foreach (int side in new[] { -1, 1 })
                    {
                        var group = new List<int>();
                        float inner = float.MinValue, limit = float.MaxValue;
                        for (int i = 0; i < ports.Count; i++)
                        {
                            var port = ports[i];
                            var points = port.Wire.Route.Points;
                            var first = port.Start ? points[0] : points[points.Count - 1];
                            var next = port.Start ? points[1] : points[points.Count - 2];
                            var direction = (next - first).normalized;
                            if (port.Offset <= 0 || Vector2.Dot(direction, bank.Normal) != 0 ||
                                Math.Sign(x ? direction.x : direction.y) != side) continue;
                            float end = side * (x ? next.x : next.y);
                            // У прямого соединения второй конец тоже может получить веер.
                            if (points.Count == 2) end = (end + side * port.Tangent) * .5f;
                            inner = Mathf.Max(inner, side * connections[i]);
                            limit = Mathf.Min(limit, Mathf.Min(end,
                                side * connections[i] + port.Offset - width * .6f));
                            group.Add(i);
                        }
                        if (limit < inner) continue;
                        float center = Mathf.Min(inner + radius, limit);
                        foreach (int i in group) turnCenters[i] = side * center;
                    }
                }
                float lastTurnOffset = ports.Where(p => p.Side != 0).Select(p => p.Offset).DefaultIfEmpty(0).Max();
                for (int portIndex = 0; portIndex < ports.Count; portIndex++)
                {
                    var port = ports[portIndex];
                    float tangent = connections[portIndex];
                    var points = port.Wire.Route.Points;
                    if (!port.Start) points.Reverse();
                    var original = points[0]; var connection = original;
                    if (x) connection.x = tangent; else connection.y = tangent;
                    connection -= bank.Normal * Vector2.Dot(connection - bank.Point, bank.Normal);
                    if (!Mathf.Approximately(tangent, port.Tangent) || !Mathf.Approximately(port.Offset, 0))
                    {
                        if (turnCenters.TryGetValue(portIndex, out float center))
                        {
                            var turnCorner = original;
                            if (x) turnCorner.x = tangent; else turnCorner.y = tangent;
                            var turnEnd = original;
                            if (x) turnEnd.x = center; else turnEnd.y = center;
                            float plannedRadius = Mathf.Abs(center - tangent);
                            var roundedTurn = new InfoflowRoute { Corners = new List<Vector2> { connection, turnCorner, turnEnd } };
                            // Общий поперечный центр и порядок портов дают вложенные повороты.
                            // При сжатии портов центры плавно расходятся без перестановки линий.
                            InfoflowGeometry.Round(roundedTurn, new[] { 0f, plannedRadius, 0f }, steps);
                            points.RemoveAt(0); points.InsertRange(0, roundedTurn.Points);
                            if (port.Start) port.Wire.Route.StartFanPointCount = roundedTurn.Points.Count;
                            else port.Wire.Route.EndFanPointCount = roundedTurn.Points.Count;
                            if (!port.Start) points.Reverse();
                            continue;
                        }
                        // Веер меняет только подключение. За его пределами остаются
                        // рассчитанные ширина, шаг дорожек и согласованный порядок магистрали.
                        var corners = port.Wire.Route.Corners;
                        int corner = port.Start ? 1 : corners.Count - 2;
                        int nextCorner = port.Start ? corner + 1 : corner - 1;
                        bool beyondPort = !Mathf.Approximately(port.Offset, 0);
                        var outgoing = beyondPort ? (points[1] - original).normalized : (corners[nextCorner] - corners[corner]).normalized;
                        float turnRadius = beyondPort ? 0 : port.Wire.Radii[corner];
                        var end = beyondPort ? original : corners[corner] + outgoing * turnRadius;
                        int join = beyondPort ? 0 : points.FindIndex(p => p.Equals(end));
                        if (join < 0) join = 1;
                        end = points[join];
                        float length = Vector2.Dot(end - connection, bank.Normal);
                        float exit = Mathf.Min(width * .6f, length);
                        if (compact && port.Side == 0 && portStep < pitch)
                        {
                            // У сжатой грани прямые линии расходятся только после того,
                            // как соседние дорожки уже повернули и освободили им место.
                            if (lastTurnOffset < length) exit = Mathf.Max(exit, lastTurnOffset);
                        }
                        var start = connection + bank.Normal * exit;
                        float handle = Mathf.Min((length - exit) * .5f, radius);
                        var controlA = start + bank.Normal * handle;
                        var controlB = end - outgoing * handle;
                        // В тесном подключении не разрешаем управляющим точкам разворачивать
                        // кривую за её концы. Это сохраняет монотонность оставшегося веера.
                        var fanLow = Vector2.Min(start, end);
                        var fanHigh = Vector2.Max(start, end);
                        controlA = Vector2.Max(fanLow, Vector2.Min(fanHigh, controlA));
                        controlB = Vector2.Max(fanLow, Vector2.Min(fanHigh, controlB));
                        var fan = new List<Vector2> { connection, start };
                        for (int i = 1; i <= steps; i++)
                        {
                            float u = i / (float)steps, v = 1 - u;
                            var point = v * v * v * start + 3 * v * v * u * controlA +
                                3 * v * u * u * controlB + u * u * u * end;
                            // Погрешность float не должна выводить кривую за пределы её опорных точек.
                            fan.Add(Vector2.Max(fanLow, Vector2.Min(fanHigh, point)));
                        }
                        points.RemoveRange(0, join + 1); points.InsertRange(0, fan);
                        // Первый обычный поворот заменён веером; остальные аналитические дуги сохраняются.
                        if (turnRadius > 0 && port.Wire.Route.Arcs.Count > 0)
                            port.Wire.Route.Arcs.RemoveAt(port.Start ? 0 : port.Wire.Route.Arcs.Count - 1);
                        if (port.Start) port.Wire.Route.StartFanPointCount = fan.Count;
                        else port.Wire.Route.EndFanPointCount = fan.Count;
                    }
                    if (!port.Start) points.Reverse();
                }
            }
            if (compressed > 0) result.Diagnostics.Add("Зазор сжат у перегруженных граней АС: " + compressed + ".");
        }

        static InfoflowNetwork PrepareNetwork(List<FlowFamily> families, bool compact = false, bool tightPacking = false, bool splitPassages = false)
        {
            foreach (var family in families) { family.Edges.Clear(); family.Nodes.Clear(); family.EdgeIndex.Clear(); }
            var network = new InfoflowNetwork(families);
            network.OrderLanes(families, compact, tightPacking, splitPassages);
            return network;
        }

        internal static float Length(List<Vector2> points)
        {
            float length = 0;
            for (int i = 1; i < points.Count; i++) length += Vector2.Distance(points[i - 1], points[i]);
            return length;
        }

        static int DirectionIndex(Vector2 normal) => normal.x > 0 ? 0 : normal.y > 0 ? 1 : normal.x < 0 ? 2 : 3;
        static Vector2 Normal(InfoflowPortSide side) => side == InfoflowPortSide.PositiveX ? Vector2.right :
            side == InfoflowPortSide.NegativeX ? Vector2.left : side == InfoflowPortSide.NegativeZ ? Vector2.down : Vector2.up;
        static InfoflowPortSide Opposite(InfoflowPortSide side) => side == InfoflowPortSide.PositiveX ? InfoflowPortSide.NegativeX :
            side == InfoflowPortSide.NegativeX ? InfoflowPortSide.PositiveX : side == InfoflowPortSide.NegativeZ ? InfoflowPortSide.PositiveZ : InfoflowPortSide.NegativeZ;
    }
}





