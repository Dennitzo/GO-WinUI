using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Bricscad.ApplicationServices;
using Bricscad.Bim;
using Bricscad.EditorInput;
using Teigha.BoundaryRepresentation;
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace GoWinUI.BricsCad.Plugin;

internal sealed class CadService
{
    private const double Tolerance = 1e-6;
    private static readonly TimeSpan GeometrySnapshotLifetime = TimeSpan.FromMinutes(5);
    private const int MaximumGeometrySnapshots = 4;
    private sealed record PipeSegment(ObjectId SourceId, Point3d Start, Point3d End);
    private sealed record PipeNetwork(
        IReadOnlyList<PipeSegment> Segments,
        IReadOnlyList<Point3d> Nodes,
        IReadOnlyList<int> Degrees,
        bool Connected,
        bool StartNodeMatched,
        int StartNodeIndex,
        double NodeTolerance);
    private sealed record PipeClearance(bool Valid, int ConflictCount, JsonArray ConflictHandles);
    private sealed record GeometrySnapshot(
        string Id,
        string DrawingId,
        int Revision,
        DateTimeOffset CreatedAt,
        string QueryKey,
        IReadOnlyList<JsonObject> Objects);
    private sealed record HostPlacementFrame(
        FullSubentityPath FacePath,
        Point3d Origin,
        Vector3d UAxis,
        Vector3d VAxis,
        Vector3d Normal,
        string OrientationSource)
    {
        public Matrix3d Ucs => Matrix3d.AlignCoordinateSystem(
            Point3d.Origin,
            Vector3d.XAxis,
            Vector3d.YAxis,
            Vector3d.ZAxis,
            Origin,
            UAxis,
            VAxis,
            Normal);
    }
    private readonly object _lastResultLock = new();
    private readonly List<ObjectId> _lastResult = new();
    private readonly List<ObjectId> _lastExtruded = new();
    private readonly Dictionary<string, GeometrySnapshot> _geometrySnapshots = new(StringComparer.Ordinal);
    private string _drawingIdentityKey = string.Empty;
    private string _drawingId = string.Empty;
    private int _drawingSequence;
    private int _revision;

    public static void Log(string message)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "GoWinUIBricsCad.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public Task<JsonObject> ValidateAsync(JsonObject parameters)
        => RunOnDocumentAsync(() => Validate(parameters));

    public Task<JsonObject> ExecuteAsync(string method, JsonObject parameters)
        => RunOnDocumentAsync(() =>
        {
            JsonValidationResult validation = CapabilityRegistry.ValidateParameters(method, parameters);
            AddRuntimeValidation(method, parameters, validation);
            if (!validation.Valid)
                throw new ArgumentException($"Parameter verletzen den live BricsCAD-.NET-Vertrag: {validation.Summary}");
            return Execute(method, parameters);
        });

    private static async Task<JsonObject> RunOnDocumentAsync(Func<JsonObject> operation)
    {
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Application.DocumentManager.ExecuteInCommandContextAsync(_ =>
        {
            try { completion.TrySetResult(operation()); }
            catch (System.Exception ex) { completion.TrySetException(ex); }
            return Task.CompletedTask;
        }, null);
        return await completion.Task.ConfigureAwait(false);
    }

    private JsonObject Execute(string method, JsonObject p)
    {
        if (method == "layers.list") return LayersList();
        if (method == "geometry.query") return GeometryQuery(p);
        if (method == "selection.describe") return SelectionDescribe(p);
        if (method == "entity.describe") return EntityDescribe(p);
        if (method == "measurement.bbox") return Measurement(p, "bbox");
        if (method == "measurement.length") return Measurement(p, "length");
        if (method == "measurement.area") return Measurement(p, "area");
        if (method == "pipes.validateNetwork") return ValidatePipeNetwork(p);
        if (method == "bim.objects.query") return BimObjectsQuery(p);
        if (method == "bim.components.query") return BimComponentsQuery(p);
        if (method == "bim.host.point.resolve") return HostPointResolve(p);
        if (method == "layers.create") return MutateLayer(p, "create");
        if (method == "layers.rename") return MutateLayer(p, "rename");
        if (method == "layers.setColor") return MutateLayer(p, "setColor");
        if (method == "layers.batch") return LayerBatch(p);
        if (method == "entity.setLayer") return SetEntityLayer(p);
        if (method == "entity.setName") return SetEntityName(p);
        if (method == "selection.set") return SetSelection(p, false);
        if (method == "bim.selection.set") return SetSelection(p, true);
        if (method == "geometry.create") return GeometryCreate(p);
        if (method == "geometry.move") return Transform(p, "move", false);
        if (method == "bim.move") return Transform(p, "move", true);
        if (method == "geometry.copy") return Transform(p, "copy", false);
        if (method == "geometry.rotate") return Transform(p, "rotate", false);
        if (method == "geometry.scale") return Transform(p, "scale", false);
        if (method == "geometry.delete") return Delete(p);
        if (method is "profile.extrude" or "circle.extrude" or "rectangles.extrude") return Extrude(method, p);
        if (method == "pipes.createNetworkSolids") return CreatePipeNetworkSolids(p);
        if (method == "annotations.createRoomDimensions") return CreateRoomDimensions(p);
        if (method == "document.save") return SaveDocument();
        if (method is "undo.last" or "undo.redo") return Undo(p, method == "undo.redo");
        if (method == "bim.classify") return BimClassify(p);
        if (method == "bim.create") return BimCreate(p);
        if (method == "bricscad.assoc.evaluate") return EvaluateAssociations(p);
        throw new InvalidOperationException($"Unbekannte BricsCAD-.NET-Methode: {method}");
    }

    private JsonObject Validate(JsonObject p)
    {
        JsonValidationResult envelopeValidation = CapabilityRegistry.ValidateParameters("actions.validate", p);
        if (!envelopeValidation.Valid)
        {
            JsonObject validationJson = envelopeValidation.ToJson();
            return new JsonObject
            {
                ["schema"] = "barebone.bricscad.actions.validate.result.dotnet.v2",
                ["provider"] = CapabilityRegistry.Provider,
                ["contractVersion"] = CapabilityRegistry.ContractVersion,
                ["valid"] = false,
                ["errors"] = validationJson["errors"]?.DeepClone() ?? new JsonArray(),
                ["missing"] = validationJson["missing"]?.DeepClone() ?? new JsonArray(),
                ["hints"] = new JsonArray(),
                ["actions"] = new JsonArray()
            };
        }
        JsonArray actions = p["actions"]!.AsArray();
        var results = new JsonArray();
        int index = 0;
        foreach (JsonNode? actionNode in actions)
        {
            JsonObject action = actionNode?.AsObject() ?? new JsonObject();
            string tool = Str(action, "tool");
            JsonObject actionParams = action["params"] as JsonObject ?? new JsonObject();
            bool known = CapabilityRegistry.TryGetMethod(tool, out _);
            JsonValidationResult validation = known
                ? CapabilityRegistry.ValidateParameters(tool, actionParams)
                : JsonValidationResult.UnknownTool(tool);
            AddRuntimeValidation(tool, actionParams, validation);
            JsonObject validationJson = validation.ToJson();
            results.Add(new JsonObject
            {
                ["index"] = index++, ["tool"] = tool, ["valid"] = validation.Valid,
                ["errors"] = validationJson["errors"]?.DeepClone(),
                ["missing"] = validationJson["missing"]?.DeepClone(),
                ["issues"] = validationJson["issues"]?.DeepClone(),
                ["warnings"] = new JsonArray(), ["hints"] = new JsonArray(),
                ["params"] = actionParams.DeepClone()
            });
        }
        var allErrors = new JsonArray();
        var allMissing = new JsonArray();
        foreach (JsonObject action in results.Select(node => node!.AsObject()))
        {
            foreach (JsonNode? error in action["errors"]?.AsArray() ?? new JsonArray()) allErrors.Add(error?.DeepClone());
            foreach (JsonNode? missing in action["missing"]?.AsArray() ?? new JsonArray()) allMissing.Add(missing?.DeepClone());
        }
        return new JsonObject {
            ["schema"] = "barebone.bricscad.actions.validate.result.dotnet.v2",
            ["provider"] = CapabilityRegistry.Provider,
            ["contractVersion"] = CapabilityRegistry.ContractVersion,
            ["valid"] = results.All(n => n!["valid"]!.GetValue<bool>()),
            ["errors"] = allErrors,
            ["missing"] = allMissing,
            ["hints"] = new JsonArray(),
            ["actions"] = results
        };
    }

    private void AddRuntimeValidation(string tool, JsonObject p, JsonValidationResult validation)
    {
        if (!validation.Valid) return;
        if (tool == "geometry.create")
        {
            string geometry = Str(p, "geometry");
            if (geometry == "rectangle" && (Num(p, "width", 0) <= 0 || Num(p, "depth", 0) <= 0))
                validation.Issues.Add(new JsonValidationIssue("$.params", "geometry-dimensions", "rectangle benötigt positive width und depth."));
            if (geometry == "box" && (Num(p, "width", 0) <= 0 || Num(p, "depth", 0) <= 0 || Num(p, "height", 0) <= 0))
                validation.Issues.Add(new JsonValidationIssue("$.params", "geometry-dimensions", "box benötigt positive width, depth und height."));
            if ((geometry == "circle" || geometry == "arc") && Num(p, "radiusMm", 0) <= 0)
                validation.Issues.Add(new JsonValidationIssue("$.params.radiusMm", "geometry-radius", $"{geometry} benötigt radiusMm > 0."));
            if (geometry == "polyline" && (p["points"]?.AsArray().Count ?? 0) < 2)
                validation.Issues.Add(new JsonValidationIssue("$.params.points", "geometry-points", "polyline benötigt mindestens zwei Punkte."));
            if (geometry == "point" && p["point"] is null && p["position"] is null && p["origin"] is null)
                validation.Issues.Add(new JsonValidationIssue("$.params.point", "geometry-point", "point benötigt point, position oder origin.", true));
            if (geometry == "line")
            {
                if (p["start"] is null)
                    validation.Issues.Add(new JsonValidationIssue("$.params.start", "geometry-line-start", "line benötigt start.", true));
                if (p["end"] is null)
                    validation.Issues.Add(new JsonValidationIssue("$.params.end", "geometry-line-end", "line benötigt end.", true));
                if (p["start"] is JsonObject start && p["end"] is JsonObject end
                    && PointMm(start, "", Point3d.Origin).DistanceTo(PointMm(end, "", Point3d.Origin)) <= Tolerance)
                    validation.Issues.Add(new JsonValidationIssue("$.params.end", "geometry-line-length", "start und end müssen verschieden sein."));
            }
            if (geometry == "arc"
                && Math.Abs(Num(p, "endAngleDeg", 90) - Num(p, "startAngleDeg", 0)) <= Tolerance)
                validation.Issues.Add(new JsonValidationIssue("$.params.endAngleDeg", "geometry-arc-angle", "startAngleDeg und endAngleDeg müssen verschieden sein."));
        }

        if (tool == "geometry.rotate")
        {
            string basePointMode = Str(p, "basePointMode");
            if (p["basePoint"] is null && string.IsNullOrWhiteSpace(basePointMode))
                validation.Issues.Add(new JsonValidationIssue("$.params.basePoint", "rotation-base-point", "geometry.rotate benötigt basePoint oder basePointMode.", true));
            if (p["basePoint"] is not null && !string.IsNullOrWhiteSpace(basePointMode))
                validation.Issues.Add(new JsonValidationIssue("$.params.basePointMode", "rotation-base-point-ambiguous", "basePoint und basePointMode dürfen nicht gleichzeitig gesetzt sein."));
            double angle = Angle(p, "angleRad", "angleDeg", 0);
            if (!double.IsFinite(angle) || Math.Abs(angle) <= Tolerance)
                validation.Issues.Add(new JsonValidationIssue("$.params.angleDeg", "rotation-angle", "geometry.rotate benötigt einen endlichen Winkel ungleich Null."));
            try
            {
                List<string> hostedOpenings = HostedBimOpeningHandles(ResolveSelector(p));
                if (hostedOpenings.Count > 0)
                {
                    validation.Issues.Add(new JsonValidationIssue(
                        "$.params.selector",
                        "hosted-bim-opening-rotation",
                        $"Verankerte BIMWindow-/BIMDoor-Instanzen dürfen nicht mit geometry.rotate gedreht werden: {string.Join(", ", hostedOpenings)}. Ihre Grundausrichtung stammt aus der Host-Face; ein ausdrücklicher Zusatzwinkel wird beim Erzeugen als bim.create.rotationDeg angegeben."));
                }
            }
            catch (System.Exception ex)
            {
                validation.Issues.Add(new JsonValidationIssue(
                    "$.params.selector",
                    "rotation-target-inspection",
                    $"Die Rotationsziele konnten in der aktuellen Zeichnung nicht sicher geprüft werden: {ex.Message}"));
            }
        }

        if (p["selector"] is JsonObject selector)
        {
            string scope = Str(selector, "scope", "currentSpace");
            if (scope == "handles")
            {
                List<string> requestedHandles = Strings(selector, "handles")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (requestedHandles.Count == 0)
                {
                    validation.Issues.Add(new JsonValidationIssue("$.params.selector.handles", "selector-handles", "scope=handles benötigt mindestens ein Handle.", true));
                }
                else
                {
                    List<ObjectId> resolvedHandles = ResolveHandles(requestedHandles);
                    if (resolvedHandles.Count != requestedHandles.Count)
                    {
                        validation.Issues.Add(new JsonValidationIssue(
                            "$.params.selector.handles",
                            "selector-target-missing",
                            $"Der Selector findet nicht alle angeforderten Objekte; mindestens ein Handle wurde in der aktuellen Zeichnungsrevision nicht gefunden: {string.Join(", ", requestedHandles)}."));
                    }
                    else if (ResolveSelector(p).Count == 0)
                    {
                        validation.Issues.Add(new JsonValidationIssue(
                            "$.params.selector",
                            "selector-target-filtered",
                            "Der Selector findet in der aktuellen Zeichnungsrevision keine zu den Filtern passenden Objekte."));
                    }
                }
            }
            if (scope == "names" && Strings(selector, "names").Count == 0)
                validation.Issues.Add(new JsonValidationIssue("$.params.selector.names", "selector-names", "scope=names benötigt mindestens einen exakten Namen.", true));
        }

        if (tool == "bim.create" && p["source"] is JsonObject source)
        {
            try { BimComponentCatalog.Resolve(Str(p, "classification"), Str(source, "componentId")); }
            catch (System.Exception ex) { validation.Issues.Add(new JsonValidationIssue("$.params.source.componentId", "component-unavailable", ex.Message)); }
            if (p["host"] is not null)
            {
                try
                {
                    ObjectId hostId = ResolveHostId(p);
                    Point3d position = PointMm(p, "position", Point3d.Origin);
                    ResolveHostPlacementFrame(hostId, position);
                }
                catch (System.Exception ex)
                {
                    validation.Issues.Add(new JsonValidationIssue("$.params.host", "invalid-bim-host", ex.Message));
                }
            }
        }
    }

    private static Document Document => Application.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("Keine aktive BricsCAD-Zeichnung.");
    private static Database Database => Document.Database;
    private static Editor Editor => Document.Editor;

    private JsonObject LayersList()
    {
        var names = new JsonArray();
        var layers = new JsonArray();
        using var tr = Database.TransactionManager.StartTransaction();
        var table = (LayerTable)tr.GetObject(Database.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            names.Add(layer.Name);
            layers.Add(new JsonObject {
                ["name"] = layer.Name,
                ["color"] = layer.Color.ColorIndex,
                ["locked"] = layer.IsLocked,
                ["off"] = layer.IsOff,
                ["frozen"] = layer.IsFrozen,
                ["current"] = id == Database.Clayer
            });
        }
        tr.Commit();
        return Result("barebone.bricscad.layers.list.result.v2", new JsonObject {
            ["layers"] = layers,
            ["names"] = names,
            ["count"] = names.Count,
            ["currentLayer"] = LayerName(Database.Clayer)
        });
    }

    private JsonObject GeometryQuery(JsonObject p)
    {
        JsonObject selector = p["selector"] as JsonObject ?? new JsonObject();
        string scope = Str(selector, "scope");
        bool fullDrawingScope = scope is "allSpaces" or "allLayouts";
        string snapshotPolicy = Str(p, "snapshotPolicy");
        string requestedSnapshotId = Str(p, "snapshotId");
        int offset = Math.Max(0, Int(p, "offset", 0));

        if (fullDrawingScope)
        {
            bool continuation = snapshotPolicy == "continue"
                || !string.IsNullOrWhiteSpace(requestedSnapshotId)
                || offset > 0;
            if (continuation)
                return ContinueGeometrySnapshot(p);
            if (snapshotPolicy.Length > 0 && snapshotPolicy != "fresh")
                throw new ArgumentException($"Unbekannte snapshotPolicy: {snapshotPolicy}");
            return CreateGeometrySnapshot(p);
        }

        if (!string.IsNullOrWhiteSpace(snapshotPolicy)
            || !string.IsNullOrWhiteSpace(requestedSnapshotId)
            || p["revision"] is not null)
            throw new ArgumentException("Snapshot-Parameter sind nur fuer geometry.query mit selector.scope=allSpaces oder allLayouts erlaubt.");

        List<ObjectId> ids = ResolveSelector(p);
        int limit = Math.Clamp(Int(p, "limit", 500), 1, 500);
        var objects = new JsonArray();
        using var tr = Database.TransactionManager.StartTransaction();
        var filteredIds = ids.Where(id => tr.GetObject(id, OpenMode.ForRead, false) is Entity entity
            && MatchesFilters(entity, p["filters"] as JsonObject)).ToList();
        var pageIds = filteredIds.Skip(offset).Take(limit).ToList();
        foreach (ObjectId id in pageIds)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity) continue;
            objects.Add(EntityJson(tr, entity, p["include"]?.AsArray()));
        }
        tr.Commit();
        Remember(pageIds, false);
        int nextOffset = offset + pageIds.Count < filteredIds.Count ? offset + pageIds.Count : -1;
        bool complete = nextOffset < 0;
        return Result("barebone.bricscad.geometry.query.result.v2", new JsonObject
        {
            ["count"] = objects.Count, ["total"] = filteredIds.Count, ["offset"] = offset,
            ["limit"] = limit, ["nextOffset"] = nextOffset,
            ["complete"] = complete, ["objects"] = objects,
            ["drawingId"] = CurrentDrawingId(), ["revision"] = _revision,
            ["page"] = PageJson(offset, limit, objects.Count, filteredIds.Count, nextOffset, complete)
        });
    }

    private JsonObject CreateGeometrySnapshot(JsonObject p)
    {
        int offset = Math.Max(0, Int(p, "offset", 0));
        if (offset != 0)
            throw new ArgumentException("Ein frischer Zeichnungssnapshot muss bei offset=0 beginnen.");

        List<ObjectId> ids = ResolveSelector(p);
        var objects = new List<JsonObject>();
        using (var tr = Database.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity
                    || !MatchesFilters(entity, p["filters"] as JsonObject))
                    continue;
                objects.Add(EntityJson(tr, entity, p["include"]?.AsArray()));
            }
            tr.Commit();
        }

        PruneGeometrySnapshots();
        string snapshotId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var snapshot = new GeometrySnapshot(
            snapshotId,
            CurrentDrawingId(),
            _revision,
            DateTimeOffset.UtcNow,
            GeometrySnapshotQueryKey(p),
            objects);
        _geometrySnapshots[snapshotId] = snapshot;
        return GeometrySnapshotPage(snapshot, p);
    }

    private JsonObject ContinueGeometrySnapshot(JsonObject p)
    {
        string snapshotId = Str(p, "snapshotId");
        if (string.IsNullOrWhiteSpace(snapshotId))
            throw new ArgumentException("Eine Snapshot-Fortsetzung braucht snapshotId.");
        if (p["revision"] is null)
            throw new ArgumentException("Eine Snapshot-Fortsetzung braucht revision.");

        PruneGeometrySnapshots();
        if (!_geometrySnapshots.TryGetValue(snapshotId, out GeometrySnapshot? snapshot))
            throw new KeyNotFoundException("Der angeforderte Zeichnungssnapshot ist abgelaufen oder nicht mehr verfuegbar.");
        if (snapshot.DrawingId != CurrentDrawingId())
            throw new InvalidOperationException("Die aktive Zeichnung wurde seit Beginn des Snapshots gewechselt.");
        if (snapshot.Revision != Int(p, "revision", -1) || snapshot.Revision != _revision)
            throw new InvalidOperationException("Die Zeichnung wurde seit Beginn des Snapshots veraendert.");
        if (snapshot.QueryKey != GeometrySnapshotQueryKey(p))
            throw new ArgumentException("Selector, Filter und Include einer Snapshot-Fortsetzung muessen dem ersten Request entsprechen.");
        return GeometrySnapshotPage(snapshot, p);
    }

    private JsonObject GeometrySnapshotPage(GeometrySnapshot snapshot, JsonObject p)
    {
        int offset = Math.Max(0, Int(p, "offset", 0));
        int limit = Math.Clamp(Int(p, "limit", 500), 1, 500);
        if (offset > snapshot.Objects.Count)
            throw new ArgumentException($"Snapshot-offset {offset} liegt hinter dem Ende ({snapshot.Objects.Count}).");

        var objects = new JsonArray();
        foreach (JsonObject item in snapshot.Objects.Skip(offset).Take(limit))
            objects.Add(item.DeepClone());
        int returned = objects.Count;
        int nextOffset = offset + returned < snapshot.Objects.Count ? offset + returned : -1;
        bool complete = nextOffset < 0;
        return Result("barebone.bricscad.geometry.query.result.v2", new JsonObject
        {
            ["provider"] = CapabilityRegistry.Provider,
            ["snapshotId"] = snapshot.Id,
            ["snapshotPolicy"] = offset == 0 ? "fresh" : "continue",
            ["snapshotCreatedAtUtc"] = snapshot.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["drawingId"] = snapshot.DrawingId,
            ["revision"] = snapshot.Revision,
            ["revisionScope"] = "runtimeInstance",
            ["count"] = returned,
            ["total"] = snapshot.Objects.Count,
            ["offset"] = offset,
            ["limit"] = limit,
            ["nextOffset"] = nextOffset,
            ["complete"] = complete,
            ["objects"] = objects,
            ["page"] = PageJson(offset, limit, returned, snapshot.Objects.Count, nextOffset, complete),
            ["document"] = DocumentJson()
        });
    }

    private void PruneGeometrySnapshots()
    {
        DateTimeOffset oldest = DateTimeOffset.UtcNow - GeometrySnapshotLifetime;
        foreach (string id in _geometrySnapshots
                     .Where(pair => pair.Value.CreatedAt < oldest)
                     .Select(pair => pair.Key)
                     .ToArray())
            _geometrySnapshots.Remove(id);
        while (_geometrySnapshots.Count >= MaximumGeometrySnapshots)
        {
            string oldestId = _geometrySnapshots.MinBy(pair => pair.Value.CreatedAt).Key;
            _geometrySnapshots.Remove(oldestId);
        }
    }

    private static string GeometrySnapshotQueryKey(JsonObject p)
    {
        var canonical = (JsonObject)p.DeepClone();
        foreach (string key in new[] { "offset", "limit", "snapshotPolicy", "snapshotId", "revision" })
            canonical.Remove(key);
        return canonical.ToJsonString();
    }

    private static JsonObject PageJson(int offset, int limit, int returned, int total, int nextOffset, bool complete)
        => new()
        {
            ["offset"] = offset,
            ["limit"] = limit,
            ["returned"] = returned,
            ["count"] = returned,
            ["total"] = total,
            ["nextOffset"] = nextOffset,
            ["complete"] = complete
        };

    private string CurrentDrawingId()
    {
        string identityKey = $"{Document.Name}\n{Database.Filename}";
        if (_drawingId.Length == 0 || !string.Equals(identityKey, _drawingIdentityKey, StringComparison.OrdinalIgnoreCase))
        {
            _drawingIdentityKey = identityKey;
            _drawingId = $"{RuntimeIdentity.Id}:{++_drawingSequence}";
            _geometrySnapshots.Clear();
        }
        return _drawingId;
    }

    private static JsonObject DocumentJson()
        => new()
        {
            ["name"] = Document.Name,
            ["path"] = Database.Filename,
            ["saved"] = !string.IsNullOrWhiteSpace(Database.Filename),
            ["units"] = Database.Insunits.ToString(),
            ["currentLayer"] = LayerName(Database.Clayer),
            ["coordinateSystem"] = "WCS"
        };

    private JsonObject SelectionDescribe(JsonObject p)
    {
        var selected = Editor.SelectImplied();
        var ids = selected.Status == PromptStatus.OK ? selected.Value.GetObjectIds().ToList() : new List<ObjectId>();
        return DescribeIds(ids, p, "barebone.bricscad.selection.describe.result.v2");
    }

    private JsonObject EntityDescribe(JsonObject p)
    {
        List<ObjectId> ids = ResolveHandlesOrName(p);
        return DescribeIds(ids, p, "barebone.bricscad.entity.describe.result.v2");
    }

    private JsonObject DescribeIds(List<ObjectId> ids, JsonObject p, string schema)
    {
        int limit = Math.Clamp(Int(p, "limit", 500), 1, 500);
        List<ObjectId> page = ids.Take(limit).ToList();
        var objects = new JsonArray();
        using var tr = Database.TransactionManager.StartTransaction();
        foreach (ObjectId id in page)
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity) objects.Add(EntityJson(tr, entity, p["include"]?.AsArray()));
        tr.Commit();
        Remember(page, false);
        return Result(schema, new JsonObject {
            ["count"] = objects.Count,
            ["total"] = ids.Count,
            ["limit"] = limit,
            ["complete"] = page.Count >= ids.Count,
            ["objects"] = objects,
            ["handles"] = Handles(page)
        });
    }

    private JsonObject Measurement(JsonObject p, string operation)
    {
        List<ObjectId> ids = ResolveSelector(p);
        double totalDrawing = 0;
        int failed = 0;
        var objects = new JsonArray();
        Extents3d? combined = null;
        using var tr = Database.TransactionManager.StartTransaction();
        foreach (ObjectId id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity e)
            {
                failed++;
                continue;
            }
            try
            {
                double value = operation switch
                {
                    "length" when e is Curve curve => curve.GetDistanceAtParameter(curve.EndParam),
                    "length" => throw new ArgumentException($"Handle {Handle(id)} ist keine messbare Kurve."),
                    "area" => e is Solid3d solid ? solid.Area : TryArea(e),
                    _ => 0
                };
                if (operation != "bbox") totalDrawing += value;
                JsonObject item = EntityJson(tr, e, null);
                if (operation == "length") item["lengthMm"] = DrawingToMm(value);
                if (operation == "area") item["areaMm2"] = DrawingAreaToMm2(value);
                if (operation == "bbox")
                {
                    Extents3d extents = e.GeometricExtents;
                    if (combined is null) combined = extents;
                    else { Extents3d valueExtents = combined.Value; valueExtents.AddExtents(extents); combined = valueExtents; }
                }
                objects.Add(item);
            }
            catch { failed++; }
        }
        tr.Commit();
        Remember(ids, false);
        var result = new JsonObject { ["schema"] = $"barebone.bricscad.measurement.{operation}.result.v2", ["count"] = objects.Count, ["measured"] = objects.Count, ["failed"] = failed, ["units"] = "mm", ["coordinateSystem"] = "WCS", ["objects"] = objects };
        if (operation == "length") result["totalLengthMm"] = DrawingToMm(totalDrawing);
        if (operation == "area") result["totalAreaMm2"] = DrawingAreaToMm2(totalDrawing);
        if (operation == "bbox" && combined is Extents3d bbox)
        {
            result["bounds"] = new JsonObject { ["min"] = PointJsonMm(bbox.MinPoint), ["max"] = PointJsonMm(bbox.MaxPoint), ["unit"] = "mm", ["coordinateSystem"] = "WCS" };
            result["dimensions"] = new JsonObject {
                ["widthX"] = DrawingToMm(Math.Abs(bbox.MaxPoint.X - bbox.MinPoint.X)),
                ["depthY"] = DrawingToMm(Math.Abs(bbox.MaxPoint.Y - bbox.MinPoint.Y)),
                ["heightZ"] = DrawingToMm(Math.Abs(bbox.MaxPoint.Z - bbox.MinPoint.Z)),
                ["unit"] = "mm"
            };
        }
        return Result(result["schema"]!.GetValue<string>(), result);
    }

    private JsonObject MutateLayer(JsonObject p, string operation)
    {
        SaveBeforeIfRequested(p);
        string name = Str(p, "name");
        string oldName = Str(p, "oldName");
        using var tr = Database.TransactionManager.StartTransaction();
        var table = (LayerTable)tr.GetObject(Database.LayerTableId, OpenMode.ForRead);
        if (operation == "create")
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("layers.create braucht name");
            if (!table.Has(name))
            {
                table.UpgradeOpen();
                var layer = new LayerTableRecord { Name = name };
                if (p["color"] is not null) layer.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)Int(p, "color", 7));
                table.Add(layer); tr.AddNewlyCreatedDBObject(layer, true);
            }
        }
        else
        {
            string target = operation == "rename" ? oldName : name;
            if (!table.Has(target)) throw new ArgumentException($"Layer nicht gefunden: {target}");
            var layer = (LayerTableRecord)tr.GetObject(table[target], OpenMode.ForWrite);
            if (operation == "rename") layer.Name = name;
            if (operation == "setColor") layer.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)Int(p, "color", 7));
        }
        tr.Commit(); _revision++;
        return Result($"barebone.bricscad.layers.{(operation == "setColor" ? "set-color" : operation)}.result.v2", new JsonObject {
            ["success"] = true,
            ["operation"] = operation,
            ["oldName"] = string.IsNullOrEmpty(oldName) ? null : oldName,
            ["layer"] = name,
            ["revision"] = _revision
        });
    }

    private JsonObject LayerBatch(JsonObject p)
    {
        SaveBeforeIfRequested(p);
        var created = new JsonArray();
        var existing = new JsonArray();
        using var tr = Database.TransactionManager.StartTransaction();
        var table = (LayerTable)tr.GetObject(Database.LayerTableId, OpenMode.ForRead);
        foreach (JsonNode? node in p["layers"]?.AsArray() ?? new JsonArray())
        {
            JsonObject item = node?.AsObject() ?? new JsonObject();
            string name = Str(item, "name");
            if (table.Has(name)) { existing.Add(name); continue; }
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            if (item["color"] is not null)
                layer.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)Int(item, "color", 7));
            table.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            created.Add(name);
        }
        tr.Commit();
        _revision++;
        return Result("barebone.bricscad.layers.batch.result.v2", new JsonObject {
            ["success"] = true,
            ["created"] = created,
            ["existing"] = existing,
            ["revision"] = _revision
        });
    }

    private JsonObject SetEntityLayer(JsonObject p)
    {
        SaveBeforeIfRequested(p);
        string layerName = Str(p, "layer");
        if (string.IsNullOrWhiteSpace(layerName)) throw new ArgumentException("entity.setLayer braucht layer");
        List<ObjectId> ids = ResolveSelector(p);
        using var tr = Database.TransactionManager.StartTransaction();
        var table = (LayerTable)tr.GetObject(Database.LayerTableId, OpenMode.ForRead);
        if (!table.Has(layerName))
        {
            if (!Bool(p, "createIfMissing", false)) throw new ArgumentException($"Layer nicht gefunden: {layerName}");
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = layerName };
            table.Add(layer); tr.AddNewlyCreatedDBObject(layer, true);
        }
        EnsureWritableEntities(tr, ids);
        foreach (ObjectId id in ids)
            if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity e) e.Layer = layerName;
        tr.Commit(); _revision++;
        Remember(ids, false);
        return Result("barebone.bricscad.entity.set-layer.result.v2", new JsonObject { ["success"] = true, ["affectedHandles"] = Handles(ids), ["layer"] = layerName, ["revision"] = _revision });
    }

    private JsonObject SetEntityName(JsonObject p)
    {
        SaveBeforeIfRequested(p);
        string name = Str(p, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("entity.setName braucht name");
        List<ObjectId> ids = ResolveSelector(p);
        using var tr = Database.TransactionManager.StartTransaction();
        EnsureWritableEntities(tr, ids);
        EnsureRegApp(tr, "BAREBONE_ENTITY");
        foreach (ObjectId id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForWrite, false) is not Entity e) continue;
            bool nativeNameSet = false;
            try
            {
                if (!BIMClassification.IsUnclassified(id))
                {
                    BIMClassification.SetName(id, name);
                    nativeNameSet = BIMClassification.GetName(id) == name;
                }
            }
            catch { }
            if (!nativeNameSet)
                e.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, "BAREBONE_ENTITY"),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, name));
        }
        tr.Commit(); _revision++;
        return Result("barebone.bricscad.entity.set-name.result.v2", new JsonObject { ["success"] = true, ["name"] = name, ["affectedHandles"] = Handles(ids), ["revision"] = _revision });
    }

    private JsonObject SetSelection(JsonObject p, bool requireBim)
    {
        List<ObjectId> ids = ResolveSelector(p);
        string method = requireBim ? "bim.selection.set" : "selection.set";
        EnsureTargets(ids, method);
        if (requireBim)
        {
            foreach (ObjectId id in ids)
                if (string.IsNullOrWhiteSpace(NativeBimClassification(id)))
                    throw new ArgumentException($"bim.selection.set akzeptiert nur nativ klassifizierte BIM-Objekte; Handle {Handle(id)} ist nicht klassifiziert.");
        }
        Editor.SetImpliedSelection(ids.ToArray());
        Remember(ids, false);
        return Result(requireBim
                ? "barebone.bricscad.bim.selection.set.result.v2"
                : "barebone.bricscad.selection.set.result.v2",
            new JsonObject { ["success"] = true, ["handles"] = Handles(ids), ["count"] = ids.Count });
    }

    private JsonObject GeometryCreate(JsonObject p)
    {
        SaveBeforeIfRequested(p);
        string geometry = Str(p, "geometry").ToLowerInvariant();
        Point3d origin = PointMm(p, "origin", PointMm(p, "position", PointMm(p, "point", PointMm(p, "center", Point3d.Origin))));
        Entity entity = geometry switch
        {
            "point" => new DBPoint(origin),
            "line" => new Line(PointMm(p, "start", origin), PointMm(p, "end", origin + Vector3d.XAxis * Mm(Num(p, "width", 1)))),
            "circle" => new Circle(origin, Vector3d.ZAxis, Mm(Num(p, "radiusMm", 1))),
            "arc" => new Arc(origin, Mm(Num(p, "radiusMm", 1)), Angle(p, "unused", "startAngleDeg", 0), Angle(p, "unused", "endAngleDeg", Math.PI / 2)),
            "rectangle" or "polyline" => CreatePolyline(p, geometry == "rectangle"),
            "box" => CreateBox(p, origin),
            _ => throw new ArgumentException($"geometry.create unterstützt geometry={geometry} nicht")
        };
        using var tr = Database.TransactionManager.StartTransaction();
        entity.SetDatabaseDefaults(Database);
        string layer = Str(p, "layer");
        if (!string.IsNullOrWhiteSpace(layer))
        {
            EnsureLayerExists(tr, layer);
            entity.Layer = layer;
        }
        var space = (BlockTableRecord)tr.GetObject(Database.CurrentSpaceId, OpenMode.ForWrite);
        ObjectId id = space.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true); tr.Commit();
        _revision++; Remember(new List<ObjectId> { id }, false);
        return Result("barebone.bricscad.geometry.create.result.v2", new JsonObject {
            ["success"] = true,
            ["geometry"] = geometry,
            ["created"] = 1,
            ["handle"] = Handle(id),
            ["layer"] = string.IsNullOrWhiteSpace(layer) ? LayerName(Database.Clayer) : layer,
            ["units"] = "mm",
            ["coordinateSystem"] = "WCS",
            ["revision"] = _revision
        });
    }

    private static Polyline CreatePolyline(JsonObject p, bool rectangle)
    {
        var poly = new Polyline();
        if (rectangle)
        {
            Point3d o = PointMm(p, "origin", PointMm(p, "center", Point3d.Origin));
            double w = Mm(Num(p, "width", 1)), d = Mm(Num(p, "depth", 1));
            bool center = Str(p, "placementMode") == "center" || p["center"] is not null;
            double x = center ? o.X - w / 2 : o.X, y = center ? o.Y - d / 2 : o.Y;
            poly.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
            poly.AddVertexAt(1, new Point2d(x + w, y), 0, 0, 0);
            poly.AddVertexAt(2, new Point2d(x + w, y + d), 0, 0, 0);
            poly.AddVertexAt(3, new Point2d(x, y + d), 0, 0, 0);
            poly.Closed = true;
            poly.Elevation = o.Z;
            return poly;
        }
        int i = 0;
        foreach (JsonNode? pointNode in p["points"]?.AsArray() ?? throw new ArgumentException("polyline braucht points"))
        {
            Point3d point = PointMm(pointNode?.AsObject() ?? new JsonObject(), "", Point3d.Origin);
            poly.AddVertexAt(i++, new Point2d(point.X, point.Y), 0, 0, 0);
            if (i == 1) poly.Elevation = point.Z;
        }
        poly.Closed = Bool(p, "closed", false);
        return poly;
    }

    private static Solid3d CreateBox(JsonObject p, Point3d origin)
    {
        double w = Mm(Num(p, "width", 1)), d = Mm(Num(p, "depth", 1)), h = Mm(Num(p, "height", 1));
        var solid = new Solid3d(); solid.CreateBox(w, d, h);
        if (origin != Point3d.Origin) solid.TransformBy(Matrix3d.Displacement(origin - Point3d.Origin));
        return solid;
    }

    private JsonObject Transform(JsonObject p, string operation, bool requireBim)
    {
        SaveBeforeIfRequested(p);
        List<ObjectId> ids = ResolveSelector(p);
        EnsureTargets(ids, requireBim ? "bim.move" : $"geometry.{operation}");
        Point3d basePoint = PointMm(p, "basePoint", Point3d.Origin);
        double factor = Num(p, "factor", 1);
        double angle = Angle(p, "angleRad", "angleDeg", 0);
        Point3d vectorPoint = PointMm(p, "vector", Point3d.Origin);
        if (p["fromPoint"] is JsonObject && p["toPoint"] is JsonObject)
        {
            Point3d from = PointMm(p, "fromPoint", Point3d.Origin);
            Point3d to = PointMm(p, "toPoint", Point3d.Origin);
            vectorPoint = new Point3d(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
        }
        Vector3d vector = vectorPoint - Point3d.Origin;
        Vector3d axis = Point(p, "axis", new Point3d(0, 0, 1)) - Point3d.Origin;
        if (axis.Length < Tolerance) throw new ArgumentException("geometry.rotate benötigt eine von Null verschiedene axis.");
        axis = axis.GetNormal();
        int count = operation == "copy" ? Int(p, "count", 1) : 1;
        var rotatedBimClassifications = new Dictionary<ObjectId, string>();
        if (operation == "rotate")
        {
            List<string> hostedOpenings = HostedBimOpeningHandles(ids);
            if (hostedOpenings.Count > 0)
                throw new ArgumentException($"geometry.rotate ist für verankerte BIMWindow-/BIMDoor-Instanzen gesperrt: {string.Join(", ", hostedOpenings)}. Die Host-Face-Ausrichtung darf nicht nachträglich überschrieben werden.");
            foreach (ObjectId id in ids)
            {
                string classification = NativeBimClassification(id);
                if (!string.IsNullOrWhiteSpace(classification))
                    rotatedBimClassifications[id] = classification;
            }
        }
        using var tr = Database.TransactionManager.StartTransaction();
        EnsureWritableEntities(tr, ids);
        if (requireBim)
        {
            foreach (ObjectId id in ids)
                if (BIMClassification.IsUnclassified(id))
                    throw new ArgumentException($"bim.move akzeptiert nur nativ klassifizierte BIM-Objekte; Handle {Handle(id)} ist nicht klassifiziert.");
        }
        string basePointMode = Str(p, "basePointMode");
        bool eachEntityCenter = operation == "rotate" && basePointMode == "eachEntityCenter";
        if (operation == "rotate" && basePointMode == "selectionCenter")
            basePoint = CombinedBoundsCenter(tr, ids);
        var created = new List<ObjectId>();
        foreach (ObjectId id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForWrite, false) is not Entity entity) continue;
            Point3d entityBasePoint = eachEntityCenter ? EntityBoundsCenter(entity) : basePoint;
            Matrix3d matrix = operation switch
            {
                "move" => Matrix3d.Displacement(vector),
                "rotate" => Matrix3d.Rotation(angle, axis, entityBasePoint),
                "scale" => Matrix3d.Scaling(factor, entityBasePoint),
                "copy" => Matrix3d.Displacement(vector),
                _ => Matrix3d.Identity
            };
            if (operation == "copy")
            {
                var space = (BlockTableRecord)tr.GetObject(Database.CurrentSpaceId, OpenMode.ForWrite);
                for (int copyIndex = 1; copyIndex <= count; ++copyIndex)
                {
                    Entity copy = (Entity)entity.Clone();
                    copy.TransformBy(Matrix3d.Displacement(vector * copyIndex));
                    ObjectId copyId = space.AppendEntity(copy);
                    tr.AddNewlyCreatedDBObject(copy, true);
                    created.Add(copyId);
                }
            }
            else entity.TransformBy(matrix);
        }
        if (requireBim)
        {
            foreach (ObjectId id in ids)
                if (BIMClassification.IsUnclassified(id))
                    throw new InvalidOperationException($"bim.move hat die native BIM-Identität von Handle {Handle(id)} ungültig gemacht; die Transaktion wird abgebrochen.");
        }
        foreach ((ObjectId id, string classification) in rotatedBimClassifications)
        {
            string readback = NativeBimClassification(id);
            if (!readback.Equals(classification, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"geometry.rotate hat die native BIM-Identität von Handle {Handle(id)} verändert; erwartet {classification}, gelesen {readback}. Die Transaktion wird abgebrochen.");
        }
        tr.Commit(); _revision++; Remember(operation == "copy" ? created : ids, false);
        var payload = new JsonObject {
            ["success"] = true,
            ["operation"] = operation,
            ["affectedHandles"] = Handles(operation == "copy" ? created : ids),
            ["sourceHandles"] = Handles(ids),
            ["units"] = "mm",
            ["coordinateSystem"] = "WCS",
            ["revision"] = _revision
        };
        if (requireBim)
            payload["verification"] = new JsonObject { ["nativeBimIdentityPreserved"] = true };
        else if (operation == "rotate" && rotatedBimClassifications.Count > 0)
            payload["verification"] = new JsonObject {
                ["nativeBimIdentityPreserved"] = true,
                ["hostedOpeningRejected"] = false
            };
        return Result(requireBim
                ? "barebone.bricscad.bim.move.result.v2"
                : $"barebone.bricscad.geometry.{operation}.result.v2",
            payload);
    }

    private JsonObject Delete(JsonObject p)
    {
        SaveBeforeIfRequested(p);
        if (!Bool(p, "confirm", false)) throw new ArgumentException("geometry.delete braucht confirm=true");
        List<ObjectId> ids = ResolveSelector(p);
        EnsureTargets(ids, "geometry.delete");
        using var tr = Database.TransactionManager.StartTransaction();
        EnsureWritableEntities(tr, ids);
        foreach (ObjectId id in ids) if (tr.GetObject(id, OpenMode.ForWrite, false) is DBObject o) o.Erase();
        tr.Commit(); _revision++; Remember(ids, false);
        return Result("barebone.bricscad.geometry.delete.result.v2", new JsonObject { ["success"] = true, ["deleted"] = ids.Count, ["affectedHandles"] = Handles(ids), ["revision"] = _revision });
    }

    private JsonObject Extrude(string method, JsonObject p)
    {
        SaveBeforeIfRequested(p);
        double heightMm = Num(p, "heightMm", 0);
        double height = Mm(heightMm);
        if (height <= 0) throw new ArgumentException("Extrusionshöhe muss positiv sein.");
        JsonObject selectorParams = p;
        if (p["selector"] is null)
        {
            string layer = Str(p, "layer");
            selectorParams = new JsonObject
            {
                ["selector"] = new JsonObject { ["scope"] = "currentSpace", ["layer"] = layer }
            };
        }
        List<ObjectId> ids = ResolveSelector(selectorParams);
        EnsureTargets(ids, method);
        var created = new List<ObjectId>();
        using var tr = Database.TransactionManager.StartTransaction();
        EnsureWritableEntities(tr, ids);
        var space = (BlockTableRecord)tr.GetObject(Database.CurrentSpaceId, OpenMode.ForWrite);
        foreach (ObjectId id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Curve curve) continue;
            if (method == "circle.extrude" && curve is not Circle) continue;
            if (method == "rectangles.extrude" && (curve is not Polyline rectangle || !IsRectangle(rectangle))) continue;
            if (curve is Polyline poly && !poly.Closed) continue;
            DBObjectCollection curves = new(); curves.Add((DBObject)curve.Clone());
            DBObjectCollection regions = Region.CreateFromCurves(curves);
            foreach (DBObject regionObject in regions)
            {
                if (regionObject is not Region region) continue;
                var solid = new Solid3d();
                solid.SetDatabaseDefaults(Database);
                Vector3d direction = Point(p, "direction", new Point3d(0, 0, 1)) - Point3d.Origin;
                if (direction.Length < Tolerance) throw new ArgumentException("direction darf kein Nullvektor sein.");
                double taper = Num(p, "taperAngleDeg", 0) * Math.PI / 180;
                if (p["direction"] is null || direction.GetNormal().IsEqualTo(Vector3d.ZAxis))
                    solid.Extrude(region, height, taper);
                else
                    solid.CreateExtrudedSolid(region, direction.GetNormal() * height, new SweepOptions());
                solid.Layer = curve.Layer;
                ObjectId solidId = space.AppendEntity(solid); tr.AddNewlyCreatedDBObject(solid, true); created.Add(solidId);
                region.Dispose();
            }
        }
        tr.Commit(); _revision++;
        lock (_lastResultLock) { _lastExtruded.Clear(); _lastExtruded.AddRange(created); }
        Remember(created, false);
        string prefix = method.Split('.')[0];
        return Result($"barebone.bricscad.{prefix}.extrude.result.v2", new JsonObject {
            ["success"] = created.Count > 0,
            ["found"] = ids.Count,
            ["extruded"] = created.Count,
            ["errors"] = ids.Count - created.Count,
            ["heightMm"] = heightMm,
            ["affectedHandles"] = Handles(created),
            ["sourceHandles"] = Handles(ids),
            ["units"] = "mm",
            ["revision"] = _revision
        });
    }

    private JsonObject SaveDocument()
    {
        if (string.IsNullOrWhiteSpace(Database.Filename)) throw new InvalidOperationException("Die Zeichnung wurde noch nicht gespeichert.");
        Database.SaveAs(Database.Filename, true, DwgVersion.Current, Database.SecurityParameters);
        return Result("barebone.bricscad.document.save.result.v2", new JsonObject { ["success"] = true, ["path"] = Database.Filename });
    }

    private JsonObject ValidatePipeNetwork(JsonObject p)
    {
        PipeNetwork network = BuildPipeNetwork(p);
        var openEnds = new JsonArray();
        var teeNodes = new JsonArray();
        var actualEndNodes = new List<Point3d>();
        var actualTeeNodes = new List<Point3d>();
        for (int index = 0; index < network.Nodes.Count; ++index)
        {
            if (network.Degrees[index] == 1) openEnds.Add(PointJsonMm(network.Nodes[index]));
            if (network.Degrees[index] == 1 && index != network.StartNodeIndex)
                actualEndNodes.Add(network.Nodes[index]);
            if (network.Degrees[index] >= 3)
            {
                actualTeeNodes.Add(network.Nodes[index]);
                teeNodes.Add(PointJsonMm(network.Nodes[index]));
            }
        }
        JsonArray endNodes = PointsJsonMm(actualEndNodes);
        bool withinFloorPlan = network.Nodes.All(point => IsInsideBounds(point, p["floorPlanBounds"]!.AsObject()));
        bool startInsideTechniqueRoom = network.StartNodeIndex >= 0
            && IsInsideBounds(network.Nodes[network.StartNodeIndex], p["techniqueRoomBounds"]!.AsObject());
        List<Point3d> expectedEndNodes = PointsMm(p["endNodes"] as JsonArray);
        List<Point3d> expectedTeeNodes = PointsMm(p["teeNodes"] as JsonArray);
        bool endNodesMatched = p["endNodes"] is null
            || NodesMatch(expectedEndNodes, actualEndNodes, network.NodeTolerance);
        bool teeNodesMatched = p["teeNodes"] is null
            || NodesMatch(expectedTeeNodes, actualTeeNodes, network.NodeTolerance);
        PipeClearance clearance = ValidatePipeClearance(network, p);
        bool valid = network.Segments.Count > 0
            && network.Connected
            && network.StartNodeMatched
            && startInsideTechniqueRoom
            && withinFloorPlan
            && endNodesMatched
            && teeNodesMatched
            && clearance.Valid;
        Remember(network.Segments.Select(segment => segment.SourceId).Distinct(), false);
        return Result("barebone.bricscad.pipes.validate-network.result.v2", new JsonObject {
            ["valid"] = valid,
            ["system"] = Str(p, "system"),
            ["segments"] = network.Segments.Count,
            ["nodes"] = network.Nodes.Count,
            ["connected"] = network.Connected,
            ["startNodeMatched"] = network.StartNodeMatched,
            ["startInsideTechniqueRoom"] = startInsideTechniqueRoom,
            ["withinFloorPlanBounds"] = withinFloorPlan,
            ["openEnds"] = openEnds,
            ["endNodes"] = endNodes,
            ["teeNodes"] = teeNodes,
            ["endNodesMatched"] = endNodesMatched,
            ["teeNodesMatched"] = teeNodesMatched,
            ["clearanceValid"] = clearance.Valid,
            ["minimumClearanceMm"] = Num(p, "minimumClearanceMm", 0),
            ["clearanceConflictCount"] = clearance.ConflictCount,
            ["clearanceConflictHandles"] = clearance.ConflictHandles,
            ["clearanceMode"] = "axis-aligned-geometric-extents",
            ["sourceHandles"] = Handles(network.Segments.Select(segment => segment.SourceId).Distinct()),
            ["units"] = "mm"
        });
    }

    private JsonObject CreatePipeNetworkSolids(JsonObject p)
    {
        SaveBeforeIfRequested(p);
        PipeNetwork network = BuildPipeNetwork(p);
        if (!network.Connected || !network.StartNodeMatched || network.Segments.Count == 0)
            throw new InvalidOperationException("Das Rohrnetz ist nicht zusammenhängend oder der Startknoten ist nicht an das Netz gebunden.");
        if (!network.Nodes.All(point => IsInsideBounds(point, p["floorPlanBounds"]!.AsObject())))
            throw new InvalidOperationException("Das Rohrnetz liegt nicht vollständig innerhalb floorPlanBounds.");
        if (network.StartNodeIndex < 0
            || !IsInsideBounds(network.Nodes[network.StartNodeIndex], p["techniqueRoomBounds"]!.AsObject()))
            throw new InvalidOperationException("Der gebundene Startknoten liegt nicht innerhalb techniqueRoomBounds.");

        double radius = Mm(Num(p, "diameterMm", 0)) / 2;
        string layer = Str(p, "targetLayer");
        var created = new List<ObjectId>();
        int fittings = 0;
        using var tr = Database.TransactionManager.StartTransaction();
        EnsureLayerExists(tr, layer, true);
        var space = (BlockTableRecord)tr.GetObject(Database.CurrentSpaceId, OpenMode.ForWrite);
        foreach (PipeSegment segment in network.Segments)
        {
            Vector3d direction = segment.End - segment.Start;
            if (direction.Length <= Tolerance) continue;
            Vector3d zAxis = direction.GetNormal();
            Vector3d xAxis = zAxis.GetPerpendicularVector().GetNormal();
            Vector3d yAxis = zAxis.CrossProduct(xAxis).GetNormal();
            var solid = new Solid3d();
            solid.SetDatabaseDefaults(Database);
            solid.CreateFrustum(direction.Length, radius, radius, radius);
            solid.TransformBy(Matrix3d.AlignCoordinateSystem(
                Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis,
                segment.Start, xAxis, yAxis, zAxis));
            solid.Layer = layer;
            ObjectId id = space.AppendEntity(solid);
            tr.AddNewlyCreatedDBObject(solid, true);
            created.Add(id);
        }
        for (int index = 0; index < network.Nodes.Count; ++index)
        {
            if (network.Degrees[index] < 2) continue;
            var fitting = new Solid3d();
            fitting.SetDatabaseDefaults(Database);
            fitting.CreateSphere(radius);
            fitting.TransformBy(Matrix3d.Displacement(network.Nodes[index] - Point3d.Origin));
            fitting.Layer = layer;
            ObjectId id = space.AppendEntity(fitting);
            tr.AddNewlyCreatedDBObject(fitting, true);
            created.Add(id);
            fittings++;
        }
        tr.Commit();
        _revision++;
        Remember(created, false);
        return Result("barebone.bricscad.pipes.create-network-solids.result.v2", new JsonObject {
            ["success"] = created.Count > 0,
            ["system"] = Str(p, "system"),
            ["created"] = created.Count,
            ["segments"] = network.Segments.Count,
            ["fittings"] = fittings,
            ["diameterMm"] = Num(p, "diameterMm", 0),
            ["targetLayer"] = layer,
            ["startInsideTechniqueRoom"] = true,
            ["withinFloorPlanBounds"] = true,
            ["junctionGeometry"] = "spherical",
            ["handles"] = Handles(created),
            ["revision"] = _revision
        });
    }

    private PipeNetwork BuildPipeNetwork(JsonObject p)
    {
        List<ObjectId> ids = ResolveSelector(p);
        var segments = new List<PipeSegment>();
        using (var tr = Database.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is Line line)
                    segments.Add(new PipeSegment(id, line.StartPoint, line.EndPoint));
                else if (tr.GetObject(id, OpenMode.ForRead, false) is Polyline polyline && !polyline.Closed)
                    for (int index = 1; index < polyline.NumberOfVertices; ++index)
                        segments.Add(new PipeSegment(id, polyline.GetPoint3dAt(index - 1), polyline.GetPoint3dAt(index)));
            }
            tr.Commit();
        }
        double nodeTolerance = Math.Max(Mm(0.5), Tolerance);
        var nodes = new List<Point3d>();
        var edges = new List<(int Start, int End)>();
        int NodeIndex(Point3d point)
        {
            int existing = nodes.FindIndex(node => node.DistanceTo(point) <= nodeTolerance);
            if (existing >= 0) return existing;
            nodes.Add(point);
            return nodes.Count - 1;
        }
        foreach (PipeSegment segment in segments)
            edges.Add((NodeIndex(segment.Start), NodeIndex(segment.End)));
        int[] degrees = new int[nodes.Count];
        foreach ((int start, int end) in edges) { degrees[start]++; degrees[end]++; }
        bool connected = nodes.Count > 0;
        if (connected)
        {
            var visited = new HashSet<int> { 0 };
            var queue = new Queue<int>(); queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach ((int start, int end) in edges)
                {
                    int next = start == current ? end : end == current ? start : -1;
                    if (next >= 0 && visited.Add(next)) queue.Enqueue(next);
                }
            }
            connected = visited.Count == nodes.Count;
        }
        Point3d requestedStart = PointMm(p, "startNode", Point3d.Origin);
        int startNodeIndex = nodes.FindIndex(node => node.DistanceTo(requestedStart) <= nodeTolerance);
        return new PipeNetwork(segments, nodes, degrees, connected, startNodeIndex >= 0, startNodeIndex, nodeTolerance);
    }

    private static List<Point3d> PointsMm(JsonArray? values)
    {
        var result = new List<Point3d>();
        foreach (JsonNode? value in values ?? new JsonArray())
            if (value is JsonObject point) result.Add(PointMm(point, string.Empty, Point3d.Origin));
        return result;
    }

    private static JsonArray PointsJsonMm(IEnumerable<Point3d> points)
    {
        var result = new JsonArray();
        foreach (Point3d point in points) result.Add(PointJsonMm(point));
        return result;
    }

    private static bool NodesMatch(IReadOnlyList<Point3d> expected, IReadOnlyList<Point3d> actual, double tolerance)
    {
        if (expected.Count != actual.Count) return false;
        var matched = new bool[actual.Count];
        foreach (Point3d expectedPoint in expected)
        {
            int index = Enumerable.Range(0, actual.Count)
                .FirstOrDefault(candidate => !matched[candidate]
                    && actual[candidate].DistanceTo(expectedPoint) <= tolerance, -1);
            if (index < 0) return false;
            matched[index] = true;
        }
        return true;
    }

    private static PipeClearance ValidatePipeClearance(PipeNetwork network, JsonObject p)
    {
        double clearance = Mm(Num(p, "minimumClearanceMm", 0));
        var avoidLayers = new HashSet<string>(Strings(p, "avoidLayers"), StringComparer.OrdinalIgnoreCase);
        if (clearance <= Tolerance || avoidLayers.Count == 0)
            return new PipeClearance(true, 0, new JsonArray());

        var sourceIds = new HashSet<ObjectId>(network.Segments.Select(segment => segment.SourceId));
        var conflicts = new HashSet<ObjectId>();
        using var tr = Database.TransactionManager.StartTransaction();
        var space = (BlockTableRecord)tr.GetObject(Database.CurrentSpaceId, OpenMode.ForRead);
        foreach (ObjectId id in space)
        {
            if (sourceIds.Contains(id)
                || tr.GetObject(id, OpenMode.ForRead, false) is not Entity obstacle
                || !avoidLayers.Contains(obstacle.Layer)) continue;
            try
            {
                Extents3d bounds = obstacle.GeometricExtents;
                Point3d min = new(bounds.MinPoint.X - clearance, bounds.MinPoint.Y - clearance, bounds.MinPoint.Z - clearance);
                Point3d max = new(bounds.MaxPoint.X + clearance, bounds.MaxPoint.Y + clearance, bounds.MaxPoint.Z + clearance);
                if (network.Segments.Any(segment => SegmentIntersectsBounds(segment.Start, segment.End, min, max)))
                    conflicts.Add(id);
            }
            catch { }
        }
        tr.Commit();
        return new PipeClearance(conflicts.Count == 0, conflicts.Count, Handles(conflicts));
    }

    private static bool SegmentIntersectsBounds(Point3d start, Point3d end, Point3d min, Point3d max)
    {
        double minimumParameter = 0;
        double maximumParameter = 1;
        double[] starts = { start.X, start.Y, start.Z };
        double[] deltas = { end.X - start.X, end.Y - start.Y, end.Z - start.Z };
        double[] minima = { Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z) };
        double[] maxima = { Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z) };
        for (int axis = 0; axis < 3; ++axis)
        {
            if (Math.Abs(deltas[axis]) <= Tolerance)
            {
                if (starts[axis] < minima[axis] || starts[axis] > maxima[axis]) return false;
                continue;
            }
            double first = (minima[axis] - starts[axis]) / deltas[axis];
            double second = (maxima[axis] - starts[axis]) / deltas[axis];
            if (first > second) (first, second) = (second, first);
            minimumParameter = Math.Max(minimumParameter, first);
            maximumParameter = Math.Min(maximumParameter, second);
            if (minimumParameter > maximumParameter) return false;
        }
        return true;
    }

    private JsonObject CreateRoomDimensions(JsonObject p)
    {
        SaveBeforeIfRequested(p);
        List<ObjectId> ids = ResolveSelector(p);
        EnsureTargets(ids, "annotations.createRoomDimensions");
        string dimensionLayer = Str(p, "dimensionLayer");
        string labelLayer = Str(p, "labelLayer");
        double offset = Mm(Num(p, "offsetMm", 250));
        double textHeight = Mm(Num(p, "textHeightMm", 250));
        double roomHeightMm = Num(p, "roomHeightMm", 0);
        int decimalPlaces = Int(p, "decimalPlaces", 2);
        List<string> names = Strings(p, "roomNames");
        var created = new List<ObjectId>();
        int rooms = 0;
        int techniqueRooms = 0;
        using var tr = Database.TransactionManager.StartTransaction();
        EnsureLayerExists(tr, dimensionLayer, true);
        EnsureLayerExists(tr, labelLayer, true);
        var space = (BlockTableRecord)tr.GetObject(Database.CurrentSpaceId, OpenMode.ForWrite);
        foreach (ObjectId id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Polyline room || !IsRectangle(room)) continue;
            Extents3d extents = room.GeometricExtents;
            Point3d min = extents.MinPoint;
            Point3d max = extents.MaxPoint;
            Point3d centre = new((min.X + max.X) / 2, (min.Y + max.Y) / 2, min.Z);
            bool isTechniqueRoom = IsInsideBounds(centre, p["techniqueRoomBounds"]!.AsObject());
            var widthDimension = new AlignedDimension(
                new Point3d(min.X, min.Y, min.Z), new Point3d(max.X, min.Y, min.Z),
                new Point3d((min.X + max.X) / 2, min.Y - offset, min.Z), string.Empty, Database.Dimstyle);
            widthDimension.SetDatabaseDefaults(Database);
            widthDimension.Layer = dimensionLayer;
            var depthDimension = new AlignedDimension(
                new Point3d(max.X, min.Y, min.Z), new Point3d(max.X, max.Y, min.Z),
                new Point3d(max.X + offset, (min.Y + max.Y) / 2, min.Z), string.Empty, Database.Dimstyle);
            depthDimension.SetDatabaseDefaults(Database);
            depthDimension.Layer = dimensionLayer;
            string roomName = rooms < names.Count
                ? names[rooms]
                : isTechniqueRoom ? "Technikraum" : $"Raum {rooms + 1}";
            var label = new MText {
                Location = centre,
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = textHeight,
                Layer = labelLayer,
                Contents = $"{roomName}\\P{DrawingToMm(max.X - min.X).ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture)} x {DrawingToMm(max.Y - min.Y).ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture)} mm\\PH={roomHeightMm.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture)} mm"
            };
            label.SetDatabaseDefaults(Database);
            foreach (Entity annotation in new Entity[] { widthDimension, depthDimension, label })
            {
                ObjectId annotationId = space.AppendEntity(annotation);
                tr.AddNewlyCreatedDBObject(annotation, true);
                created.Add(annotationId);
            }
            if (isTechniqueRoom) techniqueRooms++;
            rooms++;
        }
        tr.Commit();
        _revision++;
        Remember(created, false);
        return Result("barebone.bricscad.annotations.room-dimensions.result.v2", new JsonObject {
            ["success"] = rooms > 0,
            ["rooms"] = rooms,
            ["techniqueRooms"] = techniqueRooms,
            ["created"] = created.Count,
            ["handles"] = Handles(created),
            ["revision"] = _revision
        });
    }

    private JsonObject EvaluateAssociations(JsonObject p)
    {
        SaveBeforeIfRequested(p);
        bool hadNetwork = AssocManager.HasAssocNetwork(Database);
        bool evaluated = AssocManager.EvaluateTopLevelNetwork(Database, null!, 0);
        Editor.Regen();
        _revision++;
        return Result("barebone.bricscad.assoc.evaluate.result.v2", new JsonObject {
            ["success"] = evaluated,
            ["hadNetwork"] = hadNetwork,
            ["evaluated"] = evaluated,
            ["revision"] = _revision
        });
    }

    private JsonObject Undo(JsonObject p, bool redo)
    {
        int steps = Math.Max(1, Int(p, "steps", 1));
        if (redo)
        {
            for (int index = 0; index < steps; index++) Editor.Command("_.REDO");
        }
        else
        {
            Editor.Command("_.UNDO", steps);
        }
        _revision++;
        return Result($"barebone.bricscad.undo.{(redo ? "redo" : "last")}.result.v2", new JsonObject
        {
            ["success"] = true,
            ["steps"] = steps,
            ["provider"] = CapabilityRegistry.Provider,
            ["revision"] = _revision
        });
    }

    private JsonObject BimObjectsQuery(JsonObject p)
    {
        List<ObjectId> candidates = ResolveSelector(p);
        var requestedClasses = new HashSet<string>(Strings(p, "classifications"), StringComparer.OrdinalIgnoreCase);
        string oneClass = Str(p, "classification");
        if (!string.IsNullOrWhiteSpace(oneClass)) requestedClasses.Add(oneClass);
        int offset = Math.Max(0, Int(p, "offset", 0));
        int limit = Math.Clamp(Int(p, "limit", 500), 1, 500);
        JsonArray? include = p["include"]?.AsArray();
        var matching = new List<ObjectId>();
        foreach (ObjectId id in candidates)
        {
            string classification = NativeBimClassification(id);
            if (string.IsNullOrWhiteSpace(classification)) continue;
            if (requestedClasses.Count > 0 && !requestedClasses.Contains(classification)) continue;
            matching.Add(id);
        }
        var objects = new JsonArray();
        IReadOnlyDictionary<ObjectId, JsonObject> bimRelations = BuildBimRelationIndex();
        using var tr = Database.TransactionManager.StartTransaction();
        foreach (ObjectId id in matching.Skip(offset).Take(limit))
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity)
                objects.Add(EntityJson(tr, entity, include, bimRelations));
        tr.Commit();
        List<ObjectId> page = matching.Skip(offset).Take(limit).ToList();
        Remember(page, false);
        return Result("barebone.bricscad.bim.objects.query.result.v2", new JsonObject {
            ["provider"] = CapabilityRegistry.Provider,
            ["bimApi"] = "Bricscad.Bim.BIMClassification",
            ["count"] = objects.Count,
            ["total"] = matching.Count,
            ["offset"] = offset,
            ["limit"] = limit,
            ["nextOffset"] = offset + objects.Count < matching.Count ? offset + objects.Count : -1,
            ["complete"] = offset + objects.Count >= matching.Count,
            ["handles"] = Handles(page),
            ["objects"] = objects,
            ["drawingId"] = Database.Filename,
            ["revision"] = _revision
        });
    }

    private static JsonObject BimComponentsQuery(JsonObject p)
    {
        string classification = Str(p, "classification");
        string componentId = Str(p, "componentId");
        bool includeUnavailable = Bool(p, "includeUnavailable", false);
        var components = new JsonArray();
        foreach (BimComponent component in BimComponentCatalog.GetAll())
        {
            if (!string.IsNullOrWhiteSpace(classification)
                && !component.Classification.Equals(classification, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(componentId)
                && !component.ComponentId.Equals(componentId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!includeUnavailable && !component.Available) continue;
            components.Add(component.ToJson());
        }
        return new JsonObject {
            ["schema"] = "barebone.bricscad.bim.components.query.result.v2",
            ["provider"] = "bricscad-dotnet-library",
            ["count"] = components.Count,
            ["components"] = components
        };
    }

    private JsonObject BimClassify(JsonObject p)
    {
        SaveBeforeIfRequested(p);
        return RunUndoGroup("bim.classify", () => BimClassifyCore(p));
    }

    private JsonObject BimClassifyCore(JsonObject p)
    {
        string classification = Str(p, "classification");
        if (string.IsNullOrWhiteSpace(classification)) throw new ArgumentException("bim.classify braucht classification");
        string apiType = BimApiType(classification);
        JsonObject selectorParams = p;
        if (p["selector"] is null && !string.IsNullOrWhiteSpace(Str(p, "target")))
            selectorParams = new JsonObject { ["selector"] = new JsonObject { ["scope"] = Str(p, "target") } };
        List<ObjectId> ids = ResolveSelector(selectorParams);
        EnsureTargets(ids, "bim.classify");
        var classified = new List<ObjectId>();
        var errors = new JsonArray();
        foreach (ObjectId id in ids)
        {
            try
            {
                string readback = NativeBimClassification(id);
                BimResStatus status = BimResStatus.Ok;
                if (!readback.Equals(classification, StringComparison.OrdinalIgnoreCase))
                {
                    status = BIMClassification.ClassifyAs(id, apiType, false);
                    readback = NativeBimClassification(id);
                }
                if (readback.Equals(classification, StringComparison.OrdinalIgnoreCase))
                    classified.Add(id);
                else
                    errors.Add(new JsonObject { ["handle"] = Handle(id), ["status"] = status.ToString(), ["readback"] = readback });
            }
            catch (System.Exception ex)
            {
                errors.Add(new JsonObject { ["handle"] = Handle(id), ["error"] = ex.Message });
            }
        }
        if (errors.Count > 0 || classified.Count != ids.Count)
            throw new InvalidOperationException($"bim.classify konnte nicht alle Ziele nativ klassifizieren: {errors.ToJsonString()}");
        _revision++;
        Remember(classified, false);
        return Result("barebone.bricscad.bim.classify.result.v2", new JsonObject {
            ["success"] = classified.Count == ids.Count,
            ["classification"] = classification,
            ["found"] = ids.Count,
            ["classified"] = classified.Count,
            ["errors"] = errors,
            ["affectedHandles"] = Handles(classified),
            ["verification"] = new JsonObject { ["nativeReadbackMatched"] = classified.Count == ids.Count },
            ["revision"] = _revision
        });
    }

    private JsonObject BimCreate(JsonObject p)
    {
        SaveBeforeIfRequested(p);
        return RunUndoGroup("bim.create", () => BimCreateCore(p));
    }

    private static JsonObject RunUndoGroup(string operation, Func<JsonObject> action)
    {
        bool undoGroupOpen = false;
        try
        {
            Editor.Command("_.UNDO", "_Begin");
            undoGroupOpen = true;
            JsonObject result = action();
            Editor.Command("_.UNDO", "_End");
            undoGroupOpen = false;
            return result;
        }
        catch (System.Exception operationError)
        {
            System.Exception? rollbackError = null;
            try
            {
                if (undoGroupOpen) Editor.Command("_.UNDO", "_End");
                Editor.Command("_.UNDO", 1);
            }
            catch (System.Exception exception) { rollbackError = exception; }
            if (rollbackError is not null)
                throw new InvalidOperationException($"{operation} ist fehlgeschlagen; auch das Zurückrollen der Undo-Gruppe ist fehlgeschlagen.", new AggregateException(operationError, rollbackError));
            throw new InvalidOperationException($"{operation} ist fehlgeschlagen und wurde vollständig zurückgerollt.", operationError);
        }
    }

    private JsonObject BimCreateCore(JsonObject p)
    {
        string classification = Str(p, "classification");
        JsonObject source = p["source"] as JsonObject ?? throw new ArgumentException("bim.create braucht source");
        if (Str(source, "provider") != "bricscad-dotnet-library")
            throw new ArgumentException("bim.create akzeptiert ausschließlich provider=bricscad-dotnet-library.");
        BimComponent component = BimComponentCatalog.Resolve(classification, Str(source, "componentId"));
        Point3d requestedPosition = PointMm(p, "position", Point3d.Origin);
        double scale = Num(p, "scale", 1);
        double rotation = Num(p, "rotationDeg", 0) * Math.PI / 180;
        ObjectId hostId = ResolveHostId(p);
        HostPlacementFrame? hostFrame = hostId.IsNull
            ? null
            : ResolveHostPlacementFrame(hostId, requestedPosition);
        Point3d position = hostFrame?.Origin ?? requestedPosition;
        double? hostVolumeBefore = hostId.IsNull ? null : SolidVolume(hostId);
        HashSet<string> before = CurrentSpaceBlockReferences();
        Matrix3d previousUcs = Editor.CurrentUserCoordinateSystem;
        try
        {
            Matrix3d insertionUcs = hostFrame?.Ucs ?? previousUcs;
            Editor.CurrentUserCoordinateSystem = insertionUcs;
            Point3d commandPoint = position.TransformBy(insertionUcs.Inverse());
            var command = new List<object> { "_.-INSERT", component.ResolvedPath!, "_T", "_Local", "_S", scale, "_R", rotation };
            if (!hostId.IsNull)
            {
                command.Add("_Change");
                command.Add(SelectionSet.FromObjectIds(new[] { hostId }));
                command.Add("");
            }
            command.Add(commandPoint);
            Editor.Command(command.ToArray());
        }
        finally { Editor.CurrentUserCoordinateSystem = previousUcs; }

        List<ObjectId> created = ResolveHandles(CurrentSpaceBlockReferences().Except(before));
        if (created.Count != 1)
            throw new InvalidOperationException($"bim.create erwartete genau eine neue BlockReference, erhielt aber {created.Count}.");
        ObjectId id = created[0];
        JsonObject transformVerification;
        bool transformValid;
        string expectedContractType = classification.Equals("Door", StringComparison.OrdinalIgnoreCase) ? "BIMDoor" : "BIMWindow";
        string expectedApiType = BimApiType(expectedContractType);
        if (!BIMClassification.IsClassifiedAs(id, expectedApiType, false))
        {
            BimResStatus status = BIMClassification.ClassifyAs(id, expectedApiType, false);
            if (status != BimResStatus.Ok || !BIMClassification.IsClassifiedAs(id, expectedApiType, false))
                throw new InvalidOperationException($"Die eingefügte Komponente konnte nicht als {expectedContractType} verifiziert werden ({status}).");
        }
        bool hostRelationApplied = hostId.IsNull;
        bool associationEvaluated = false;
        if (!hostId.IsNull)
        {
            try
            {
                FullSubentityPath anchorFace = hostFrame?.FacePath
                    ?? throw new InvalidOperationException("Der planare Host-Face-Rahmen fehlt.");
                if (anchorFace.IsNull || !anchorFace.GetObjectIds().Contains(hostId))
                    throw new InvalidOperationException("Die verwaltete BricsCAD-.NET-API konnte am angeforderten Punkt keine passende Face des angegebenen Hosts ermitteln.");
                if (!AnchoredBlocks.IsAnchoredBlockReference(id))
                    AnchoredBlocks.CreateAnchoredBlockReference(id, anchorFace, position, true);
                FullSubentityPath readback = AnchoredBlocks.GetAnchorFace(id);
                hostRelationApplied = AnchoredBlocks.IsAnchoredBlockReference(id)
                    && !readback.IsNull
                    && readback.GetObjectIds().Contains(hostId);
                Editor.Command("_.BIMWINDOWUPDATE", "_MOde", "_Automatic",
                    SelectionSet.FromObjectIds(new[] { id }), "");
                associationEvaluated = AssocManager.EvaluateTopLevelNetwork(Database, null!, 0);
                Editor.Regen();
                if (!hostRelationApplied)
                    throw new InvalidOperationException("Die verwaltete BricsCAD-.NET-Anchor-Beziehung stimmt beim Rücklesen nicht mit dem angegebenen Host überein.");
            }
            catch (System.Exception ex) { throw new InvalidOperationException("Die BIM-Host-Verknüpfung konnte nicht hergestellt werden.", ex); }
        }
        transformVerification = BlockTransformVerification(
            id,
            position,
            scale,
            rotation,
            hostFrame,
            out transformValid);
        if (!transformValid)
            throw new InvalidOperationException($"Die eingefügte BIM-Komponente hat nach der Host-Verknüpfung nicht den angeforderten 3D-Transform: {transformVerification.ToJsonString()}");
        _revision++; Remember(created, false);
        string nativeClass = NativeBimClassification(id);
        if (!nativeClass.Equals(expectedContractType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Die native BIM-Klassifikation stimmt nach dem Einfügen nicht überein: erwartet {expectedContractType}, gelesen {nativeClass}.");
        double? hostVolumeAfter = hostId.IsNull ? null : SolidVolume(hostId);
        double openingVolumeMm3 = hostVolumeBefore.HasValue && hostVolumeAfter.HasValue
            ? DrawingVolumeToMm3(hostVolumeBefore.Value - hostVolumeAfter.Value)
            : 0;
        bool openingGeometryValid = hostId.IsNull
            || (hostVolumeBefore.HasValue && hostVolumeAfter.HasValue
                && openingVolumeMm3 > Math.Max(1e-3, DrawingVolumeToMm3(hostVolumeBefore.Value) * 1e-9));
        if (!openingGeometryValid)
            throw new InvalidOperationException("Die BIMWINDOWUPDATE-Nachbedingung ist fehlgeschlagen: Im Host-Solid wurde keine messbare Window-/Door-Öffnung erzeugt.");
        return Result("barebone.bricscad.bim.create.result.v6", new JsonObject {
            ["method"] = "bim.create", ["success"] = true, ["mutationApplied"] = true, ["classification"] = classification,
            ["created"] = new JsonObject { ["handle"] = Handle(id), ["entityType"] = ObjectClassName(id), ["bimType"] = nativeClass },
            ["component"] = new JsonObject { ["componentId"] = component.ComponentId, ["provider"] = "bricscad-dotnet-library", ["mode"] = "managed-editor-command" },
            ["host"] = p["host"]?.DeepClone() ?? new JsonObject(),
            ["placement"] = new JsonObject {
                ["requestedPosition"] = PointJsonMm(requestedPosition),
                ["insertedPosition"] = PointJsonMm(position),
                ["rotationDeg"] = rotation * 180 / Math.PI,
                ["coordinateFrame"] = hostFrame is null ? WorldCoordinateFrameJson(position) : HostPlacementFrameJson(hostFrame),
                ["orientationSource"] = hostFrame?.OrientationSource ?? "wcs"
            },
            ["opening"] = new JsonObject {
                ["requested"] = !hostId.IsNull,
                ["updateMethod"] = hostId.IsNull ? "not-required" : "BIMWINDOWUPDATE/Automatic",
                ["hostVolumeBeforeMm3"] = hostVolumeBefore.HasValue ? DrawingVolumeToMm3(hostVolumeBefore.Value) : null,
                ["hostVolumeAfterMm3"] = hostVolumeAfter.HasValue ? DrawingVolumeToMm3(hostVolumeAfter.Value) : null,
                ["removedVolumeMm3"] = openingVolumeMm3,
                ["geometryVerified"] = openingGeometryValid
            },
            ["verification"] = new JsonObject {
                ["entityExists"] = true,
                ["classificationValid"] = true,
                ["hostRelationValid"] = hostRelationApplied,
                ["openingGeometryValid"] = openingGeometryValid,
                ["associationEvaluated"] = associationEvaluated,
                ["componentInstanceValid"] = true,
                ["transformValid"] = transformValid,
                ["hostFrameValid"] = hostFrame is not null || hostId.IsNull,
                ["transform"] = transformVerification,
                ["ucsRestored"] = true
            },
            ["revision"] = _revision
        });
    }

    private ObjectId ResolveHostId(JsonObject p)
    {
        if (p["host"] is not JsonObject host) return ObjectId.Null;
        string handle = Str(host, "handle");
        List<ObjectId> ids = ResolveHandles(new[] { handle });
        if (ids.Count != 1)
            throw new ArgumentException($"Der BIM-Host konnte nicht eindeutig aufgelöst werden: {handle}.");
        ObjectId id = ids[0];
        using var tr = Database.TransactionManager.StartTransaction();
        EnsureWritableEntities(tr, ids);
        Entity entity = (Entity)tr.GetObject(id, OpenMode.ForRead, false);
        string classification = NativeBimClassification(id);
        if (entity is not Solid3d || !classification.Equals("BIMWall", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"bim.create akzeptiert als Window-/Door-Host nur ein als BIMWall klassifiziertes Solid3d; {handle} ist {entity.GetType().Name}/{classification}.");
        tr.Commit();
        return id;
    }

    private static HostPlacementFrame ResolveHostPlacementFrame(
        ObjectId hostId,
        Point3d requestedPosition,
        FullSubentityPath? resolvedFace = null)
    {
        FullSubentityPath anchorFace = resolvedFace ?? AnchoredBlocks.QueryValidAnchorPt(requestedPosition, Database);
        if (anchorFace.IsNull || !anchorFace.GetObjectIds().Contains(hostId))
            throw new InvalidOperationException("Am angeforderten WCS-Punkt wurde keine gültige Face des angegebenen BIMWall-Hosts gefunden.");

        using var brep = new Brep(anchorFace);
        Teigha.BoundaryRepresentation.Face? selectedFace = null;
        foreach (Teigha.BoundaryRepresentation.Face face in brep.Faces)
        {
            selectedFace ??= face;
            if (face.SubentityPath == anchorFace)
            {
                selectedFace = face;
                break;
            }
        }
        if (selectedFace is null)
            throw new InvalidOperationException("Die verwaltete BRep-API lieferte für die Anchor-Face keine Face-Geometrie.");

        Teigha.Geometry.Surface surface = selectedFace.Surface;
        Plane? plane = surface as Plane;
        if (plane is null && surface is ExternalBoundedSurface boundedSurface && boundedSurface.IsPlane)
            plane = boundedSurface.BaseSurface as Plane;
        if (plane is null)
            throw new InvalidOperationException("Window-/Door-Komponenten benötigen eine planare BIMWall-Hostfläche.");

        Vector3d normal = plane.Normal.GetNormal();
        if (!selectedFace.IsOrientToSurface)
            normal = normal.Negate();
        Vector3d vertical = Vector3d.ZAxis.Subtract(
            normal.MultiplyBy(Vector3d.ZAxis.DotProduct(normal)));
        if (vertical.Length <= Tolerance)
            throw new InvalidOperationException("Window-/Door-Komponenten benötigen eine vertikale BIMWall-Hostfläche.");
        Vector3d vAxis = vertical.GetNormal();
        Vector3d uAxis = vAxis.CrossProduct(normal).GetNormal();
        Point3d projected = plane.ClosestPointTo(requestedPosition);
        return new HostPlacementFrame(
            anchorFace,
            projected,
            uAxis,
            vAxis,
            normal,
            "managed-anchor-planar-brep");
    }

    private static JsonObject HostPlacementFrameJson(HostPlacementFrame frame) => new()
    {
        ["origin"] = PointArrayMm(frame.Origin),
        ["uAxis"] = VectorJson(frame.UAxis),
        ["vAxis"] = VectorJson(frame.VAxis),
        ["normal"] = VectorJson(frame.Normal)
    };

    private static JsonObject WorldCoordinateFrameJson(Point3d origin) => new()
    {
        ["origin"] = PointArrayMm(origin),
        ["uAxis"] = VectorJson(Vector3d.XAxis),
        ["vAxis"] = VectorJson(Vector3d.YAxis),
        ["normal"] = VectorJson(Vector3d.ZAxis)
    };

    private static double DirectionErrorDeg(Vector3d actual, Vector3d expected)
    {
        if (actual.Length <= Tolerance || expected.Length <= Tolerance)
            return double.PositiveInfinity;
        return actual.GetNormal().GetAngleTo(expected.GetNormal()) * 180 / Math.PI;
    }

    private static JsonObject BlockTransformVerification(
        ObjectId id,
        Point3d expectedPosition,
        double expectedScale,
        double expectedRotation,
        HostPlacementFrame? hostFrame,
        out bool valid)
    {
        using var tr = Database.TransactionManager.StartTransaction();
        if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference block)
        {
            tr.Commit();
            valid = false;
            return new JsonObject { ["valid"] = false, ["reason"] = "created-entity-is-not-a-block-reference" };
        }
        Point3d actualPosition = block.Position;
        Scale3d actualScale = block.ScaleFactors;
        double unitFactor = block.UnitFactor;
        double positionErrorMm = DrawingToMm(actualPosition.DistanceTo(expectedPosition));
        Vector3d baseU = hostFrame?.UAxis ?? Vector3d.XAxis;
        Vector3d baseV = hostFrame?.VAxis ?? Vector3d.YAxis;
        Vector3d expectedNormal = hostFrame?.Normal ?? Vector3d.ZAxis;
        Vector3d expectedXAxis = baseU.RotateBy(expectedRotation, expectedNormal).GetNormal();
        Vector3d expectedYAxis = baseV.RotateBy(expectedRotation, expectedNormal).GetNormal();
        CoordinateSystem3d actualFrame = block.BlockTransform.CoordinateSystem3d;
        Vector3d actualXAxis = actualFrame.Xaxis.GetNormal();
        Vector3d actualYAxis = actualFrame.Yaxis.GetNormal();
        Vector3d actualNormal = actualFrame.Zaxis.GetNormal();
        double xAxisErrorDeg = DirectionErrorDeg(actualXAxis, expectedXAxis);
        double yAxisErrorDeg = DirectionErrorDeg(actualYAxis, expectedYAxis);
        double normalErrorDeg = DirectionErrorDeg(actualNormal, expectedNormal);
        double frameErrorDeg = Math.Max(normalErrorDeg, Math.Max(xAxisErrorDeg, yAxisErrorDeg));
        bool uniformScale = NearlyEqual(actualScale.X, actualScale.Y) && NearlyEqual(actualScale.X, actualScale.Z);
        bool requestedScaleApplied = NearlyEqual(actualScale.X, expectedScale)
            || (unitFactor > Tolerance && NearlyEqual(actualScale.X / unitFactor, expectedScale));
        bool finite = double.IsFinite(actualScale.X)
            && double.IsFinite(actualScale.Y)
            && double.IsFinite(actualScale.Z)
            && double.IsFinite(block.Rotation);
        valid = positionErrorMm <= 1.0
            && frameErrorDeg <= 0.25
            && finite
            && actualScale.X > 0
            && uniformScale
            && requestedScaleApplied;
        var result = new JsonObject {
            ["valid"] = valid,
            ["expectedPosition"] = PointJsonMm(expectedPosition),
            ["actualPosition"] = PointJsonMm(actualPosition),
            ["positionErrorMm"] = positionErrorMm,
            ["expectedRotationDeg"] = expectedRotation * 180 / Math.PI,
            ["actualRotationDeg"] = block.Rotation * 180 / Math.PI,
            ["frameErrorDeg"] = frameErrorDeg,
            ["xAxisErrorDeg"] = xAxisErrorDeg,
            ["yAxisErrorDeg"] = yAxisErrorDeg,
            ["normalErrorDeg"] = normalErrorDeg,
            ["expectedCoordinateFrame"] = new JsonObject {
                ["origin"] = PointArrayMm(expectedPosition),
                ["xAxis"] = VectorJson(expectedXAxis),
                ["yAxis"] = VectorJson(expectedYAxis),
                ["normal"] = VectorJson(expectedNormal)
            },
            ["actualCoordinateFrame"] = new JsonObject {
                ["origin"] = PointArrayMm(actualFrame.Origin),
                ["xAxis"] = VectorJson(actualXAxis),
                ["yAxis"] = VectorJson(actualYAxis),
                ["normal"] = VectorJson(actualNormal)
            },
            ["expectedScale"] = expectedScale,
            ["actualScale"] = new JsonObject { ["x"] = actualScale.X, ["y"] = actualScale.Y, ["z"] = actualScale.Z },
            ["blockUnitFactor"] = unitFactor,
            ["uniformScale"] = uniformScale,
            ["requestedScaleApplied"] = requestedScaleApplied
        };
        tr.Commit();
        return result;
    }

    private JsonObject HostPointResolve(JsonObject p)
    {
        JsonObject host = p["host"] as JsonObject ?? throw new ArgumentException("host fehlt");
        List<ObjectId> ids = ResolveHandles(new[] { Str(host, "handle") });
        if (ids.Count != 1) throw new ArgumentException("Host konnte nicht eindeutig aufgelöst werden.");
        using var tr = Database.TransactionManager.StartTransaction();
        if (tr.GetObject(ids[0], OpenMode.ForRead, false) is not Entity e) throw new ArgumentException("Host ist keine Entity");
        string nativeClassification = NativeBimClassification(ids[0]);
        if (e is not Solid3d || !nativeClassification.Equals("BIMWall", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("bim.host.point.resolve akzeptiert nur ein als BIMWall klassifiziertes Solid3d.");
        Extents3d extents = e.GeometricExtents;
        Point3d boundsCentre = new((extents.MinPoint.X + extents.MaxPoint.X) / 2,
            (extents.MinPoint.Y + extents.MaxPoint.Y) / 2,
            (extents.MinPoint.Z + extents.MaxPoint.Z) / 2);
        Point3d axisOrigin = boundsCentre;
        Point3d defaultPoint = new(axisOrigin.X, axisOrigin.Y, boundsCentre.Z);
        Point3d requested = PointMm(p, "requestedPoint", defaultPoint);
        Vector3d axis = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X)
            >= Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y)
            ? Vector3d.XAxis
            : Vector3d.YAxis;
        Vector3d vAxis = Vector3d.ZAxis;
        Vector3d baseNormal = axis.CrossProduct(vAxis).GetNormal();
        double hostLength = BoundsSpanAlong(extents, axis);
        double hostThickness = BoundsSpanAlong(extents, baseNormal);
        JsonObject placement = p["placement"] as JsonObject ?? new JsonObject();
        double footprintWidth = placement["width"] is null ? 0 : Mm(Num(placement, "width", 0));
        double footprintHeight = placement["height"] is null ? 0 : Mm(Num(placement, "height", 0));
        double hostHeight = Math.Abs(extents.MaxPoint.Z - extents.MinPoint.Z);
        bool dimensionsFit = footprintWidth <= hostLength + Tolerance
            && footprintHeight <= hostHeight + Tolerance;

        Vector3d requestedOffset = requested - axisOrigin;
        double requestedAlong = requestedOffset.DotProduct(axis);
        double requestedAcross = requestedOffset.DotProduct(baseNormal);
        double maximumAlong = Math.Max(0, (hostLength - footprintWidth) / 2);
        double projectedAlong = Math.Clamp(requestedAlong, -maximumAlong, maximumAlong);
        double minimumZ = extents.MinPoint.Z + footprintHeight / 2;
        double maximumZ = extents.MaxPoint.Z - footprintHeight / 2;
        double projectedZ = dimensionsFit
            ? Math.Clamp(requested.Z, minimumZ, maximumZ)
            : boundsCentre.Z;
        double side = requestedAcross < 0 ? -1 : 1;
        Vector3d normal = baseNormal * side;
        Vector3d uAxis = axis * side;
        Point3d projected = new Point3d(axisOrigin.X, axisOrigin.Y, 0)
            + axis * projectedAlong
            + normal * (hostThickness / 2)
            + Vector3d.ZAxis * projectedZ;
        bool pointInsideHostVolume = Math.Abs(requestedAlong) <= hostLength / 2 + Tolerance
            && Math.Abs(requestedAcross) <= hostThickness / 2 + Tolerance
            && requested.Z >= extents.MinPoint.Z - Tolerance
            && requested.Z <= extents.MaxPoint.Z + Tolerance;
        bool footprintInside = dimensionsFit;
        FullSubentityPath managedFace = FullSubentityPath.Null;
        bool managedFaceMatchesHost = false;
        HostPlacementFrame? managedFrame = null;
        if (dimensionsFit && hostLength > Tolerance && hostHeight > Tolerance)
        {
            try
            {
                managedFace = AnchoredBlocks.QueryValidAnchorPt(projected, Database);
                managedFaceMatchesHost = !managedFace.IsNull
                    && managedFace.GetObjectIds().Contains(ids[0]);
                if (managedFaceMatchesHost)
                {
                    managedFrame = ResolveHostPlacementFrame(ids[0], projected, managedFace);
                    projected = managedFrame.Origin;
                    uAxis = managedFrame.UAxis;
                    vAxis = managedFrame.VAxis;
                    normal = managedFrame.Normal;
                }
            }
            catch
            {
                managedFrame = null;
                managedFaceMatchesHost = false;
            }
        }
        bool usable = dimensionsFit
            && hostLength > Tolerance
            && hostHeight > Tolerance
            && managedFaceMatchesHost
            && managedFrame is not null;
        string status = usable
            ? "managed-anchor-planar-brep"
            : !dimensionsFit ? "placement-does-not-fit-host" : "managed-planar-host-frame-unavailable";
        string facePath = managedFaceMatchesHost
            ? $"{string.Join('/', managedFace.GetObjectIds().Select(Handle))}/{managedFace.SubentId.Type}:{managedFace.SubentId.IndexPtr.ToInt64()}"
            : string.Empty;
        tr.Commit();
        return Result("barebone.bricscad.bim.host.point.resolve.result.v2", new JsonObject {
            ["resolved"] = usable,
            ["usable"] = usable,
            ["status"] = status,
            ["host"] = host.DeepClone(),
            ["classification"] = Str(p, "classification"),
            ["point"] = new JsonObject {
                ["requested"] = PointArrayMm(requested),
                ["projected"] = PointArrayMm(projected),
                ["status"] = requested.DistanceTo(projected) <= Mm(0.1) ? "unchanged" : "projected-to-nearest-valid-face"
            },
            ["coordinateFrame"] = new JsonObject {
                ["origin"] = PointArrayMm(projected),
                ["uAxis"] = VectorJson(uAxis),
                ["vAxis"] = VectorJson(vAxis),
                ["normal"] = VectorJson(normal)
            },
            ["face"] = new JsonObject {
                ["path"] = facePath,
                ["orientationSource"] = managedFrame?.OrientationSource ?? "unavailable",
                ["side"] = side < 0 ? "negative" : "positive",
                ["subentityType"] = managedFaceMatchesHost ? managedFace.SubentId.Type.ToString() : string.Empty,
                ["subentityIndex"] = managedFaceMatchesHost ? managedFace.SubentId.IndexPtr.ToInt64() : -1
            },
            ["validation"] = new JsonObject {
                ["hostFaceUsable"] = usable,
                ["nativeBimHost"] = true,
                ["managedAnchorFaceMatched"] = managedFaceMatchesHost,
                ["planarHostFrameResolved"] = managedFrame is not null,
                ["footprintInside"] = footprintInside,
                ["pointInsideHostVolume"] = pointInsideHostVolume,
                ["hostLengthMm"] = DrawingToMm(hostLength),
                ["hostHeightMm"] = DrawingToMm(hostHeight),
                ["hostThicknessMm"] = DrawingToMm(hostThickness),
                ["footprintWidthMm"] = DrawingToMm(footprintWidth),
                ["footprintHeightMm"] = DrawingToMm(footprintHeight)
            }
        });
    }

    private List<ObjectId> ResolveSelector(JsonObject p)
    {
        JsonObject selector = p["selector"] as JsonObject ?? p;
        string scope = Str(selector, "scope");
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("selector.scope ist für die BricsCAD-.NET-API erforderlich.");
        List<ObjectId> candidates;
        if (scope == "lastResult")
        {
            lock (_lastResultLock) candidates = _lastResult.ToList();
            return FilterSelector(candidates, selector);
        }
        if (scope == "lastExtruded")
        {
            lock (_lastResultLock) candidates = _lastExtruded.ToList();
            return FilterSelector(candidates, selector);
        }
        if (scope == "selection")
        {
            var selection = Editor.SelectImplied();
            candidates = selection.Status == PromptStatus.OK ? selection.Value.GetObjectIds().ToList() : new List<ObjectId>();
            return FilterSelector(candidates, selector);
        }
        if (scope == "handles") return FilterSelector(ResolveHandles(Strings(selector, "handles")), selector);
        if (scope == "names") return FilterSelector(ResolveNames(Strings(selector, "names"), Bool(selector, "allMatches", false)), selector);
        var ids = new List<ObjectId>();
        using var tr = Database.TransactionManager.StartTransaction();
        IEnumerable<ObjectId> spaces;
        if (scope is "allSpaces" or "allLayouts")
        {
            var blockTable = (BlockTable)tr.GetObject(Database.BlockTableId, OpenMode.ForRead);
            spaces = blockTable.Cast<ObjectId>().Where(id =>
            {
                var record = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                return record.IsLayout && !record.IsFromExternalReference;
            }).ToArray();
        }
        else spaces = new[] { Database.CurrentSpaceId };
        foreach (ObjectId spaceId in spaces)
        {
            var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is Entity) ids.Add(id);
            }
        }
        tr.Commit();
        return FilterSelector(ids, selector);
    }

    private static List<ObjectId> FilterSelector(IEnumerable<ObjectId> candidates, JsonObject selector)
    {
        var result = new List<ObjectId>();
        using var tr = Database.TransactionManager.StartTransaction();
        foreach (ObjectId id in candidates.Distinct())
        {
            if (id.IsNull || !id.IsValid || id.IsErased) continue;
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity && MatchesSelector(entity, selector))
                result.Add(id);
        }
        tr.Commit();
        return result;
    }

    private List<ObjectId> ResolveHandlesOrName(JsonObject p)
    {
        List<string> handles = Strings(p, "handles");
        if (!string.IsNullOrWhiteSpace(Str(p, "handle"))) handles.Add(Str(p, "handle"));
        if (handles.Count > 0) return ResolveHandles(handles);
        List<string> names = Strings(p, "names");
        if (!string.IsNullOrWhiteSpace(Str(p, "name"))) names.Add(Str(p, "name"));
        return ResolveNames(names, true);
    }

    private List<ObjectId> ResolveNames(IEnumerable<string> names, bool allMatches)
    {
        var requested = new HashSet<string>(names.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return new List<ObjectId>();
        var result = new List<ObjectId>();
        using var tr = Database.TransactionManager.StartTransaction();
        var space = (BlockTableRecord)tr.GetObject(Database.CurrentSpaceId, OpenMode.ForRead);
        foreach (ObjectId id in space)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity) continue;
            string name = EntityName(entity);
            if (requested.Contains(name)) result.Add(id);
        }
        tr.Commit();
        if (!allMatches)
        {
            foreach (string name in requested)
            {
                int count = result.Count(id => EntityName(id).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (count > 1) throw new ArgumentException($"BIM-/Entity-Name ist nicht eindeutig: {name}. Setze selector.allMatches=true für eine bewusste Mehrfachauswahl.");
            }
        }
        return result;
    }

    private List<ObjectId> ResolveHandles(IEnumerable<string> handles)
    {
        var result = new List<ObjectId>();
        foreach (string text in handles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)) continue;
            try { ObjectId id = Database.GetObjectId(false, new Handle(value), 0); if (!id.IsNull && id.IsValid) result.Add(id); } catch { }
        }
        return result;
    }

    private static bool MatchesFilters(Entity entity, JsonObject? filters)
    {
        if (filters is null) return true;
        string typeContains = Str(filters, "typeContains");
        if (!string.IsNullOrEmpty(typeContains) && !entity.GetType().Name.Contains(typeContains, StringComparison.OrdinalIgnoreCase)) return false;
        string layer = Str(filters, "layer");
        if (!string.IsNullOrEmpty(layer) && !entity.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase)) return false;
        if (filters["isClosed"] is not null && entity is Polyline poly && poly.Closed != filters["isClosed"]!.GetValue<bool>()) return false;
        string classification = Str(filters, "classification");
        if (!string.IsNullOrWhiteSpace(classification)
            && !NativeBimClassification(entity.ObjectId).Equals(classification, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool MatchesSelector(Entity entity, JsonObject selector)
    {
        string layer = Str(selector, "layer");
        if (!string.IsNullOrWhiteSpace(layer) && !entity.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase)) return false;
        string kind = Str(selector, "kind");
        if (kind.Equals("bim", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(NativeBimClassification(entity.ObjectId))) return false;
        }
        else if (kind.Equals("profile", StringComparison.OrdinalIgnoreCase))
        {
            if (entity is not Curve profile || !profile.Closed) return false;
        }
        else if (!string.IsNullOrWhiteSpace(kind)
            && !kind.Equals("entity", StringComparison.OrdinalIgnoreCase)
            && !EntityKind(entity).Equals(kind, StringComparison.OrdinalIgnoreCase)) return false;
        string shape = Str(selector, "shape");
        if (shape == "rectangle" && (entity is not Polyline poly || !IsRectangle(poly))) return false;
        if (shape == "circle" && entity is not Circle) return false;
        return true;
    }

    private static JsonObject EntityJson(
        Transaction tr,
        Entity entity,
        JsonArray? include,
        IReadOnlyDictionary<ObjectId, JsonObject>? bimRelations = null)
    {
        var item = new JsonObject {
            ["handle"] = entity.Handle.ToString(),
            ["entityType"] = entity.GetType().Name,
            ["layer"] = entity.Layer,
            ["entityKind"] = EntityKind(entity),
            ["is3D"] = entity is Solid3d,
            ["coordinateSystem"] = "WCS"
        };
        try
        {
            if (!entity.OwnerId.IsNull
                && tr.GetObject(entity.OwnerId, OpenMode.ForRead, false) is BlockTableRecord owner)
            {
                item["spaceName"] = owner.Name;
                item["spaceType"] = owner.IsLayout
                    ? (owner.Name == BlockTableRecord.ModelSpace ? "modelSpace" : "paperSpace")
                    : "blockDefinition";
                if (!owner.LayoutId.IsNull
                    && tr.GetObject(owner.LayoutId, OpenMode.ForRead, false) is Layout layout)
                    item["layout"] = layout.LayoutName;
            }
        }
        catch { item["spaceStatus"] = "unavailable"; }
        string name = EntityName(entity);
        if (!string.IsNullOrWhiteSpace(name)) item["name"] = name;
        string bimClassification = NativeBimClassification(entity.ObjectId);
        if (!string.IsNullOrWhiteSpace(bimClassification))
        {
            item["isBimObject"] = true;
            item["objectDomain"] = "bim";
            item["bimClassification"] = bimClassification;
            string description = string.Empty;
            try { description = BIMClassification.GetDescription(entity.ObjectId); }
            catch { }
            var bim = new JsonObject { ["type"] = bimClassification, ["name"] = name };
            if (!string.IsNullOrWhiteSpace(description))
            {
                item["description"] = description;
                bim["description"] = description;
            }
            if (bimRelations is not null && bimRelations.TryGetValue(entity.ObjectId, out JsonObject? relation))
                bim["relations"] = relation.DeepClone();
            item["bim"] = bim;
        }
        else item["isBimObject"] = false;
        try
        {
            Extents3d b = entity.GeometricExtents;
            item["bounds"] = new JsonObject { ["min"] = PointJsonMm(b.MinPoint), ["max"] = PointJsonMm(b.MaxPoint), ["coordinateSystem"] = "WCS", ["unit"] = "mm" };
            item["dimensions"] = new JsonObject {
                ["widthX"] = DrawingToMm(Math.Abs(b.MaxPoint.X - b.MinPoint.X)),
                ["depthY"] = DrawingToMm(Math.Abs(b.MaxPoint.Y - b.MinPoint.Y)),
                ["heightZ"] = DrawingToMm(Math.Abs(b.MaxPoint.Z - b.MinPoint.Z)),
                ["unit"] = "mm"
            };
        }
        catch { item["boundsStatus"] = "unavailable"; }
        var metrics = new JsonObject();
        if (entity is Curve curve)
        {
            try { metrics["length"] = DrawingToMm(curve.GetDistanceAtParameter(curve.EndParam)); }
            catch { }
        }
        double area = entity is Solid3d solid ? solid.Area : TryArea(entity);
        if (area > 0) metrics["area"] = DrawingAreaToMm2(area);
        if (metrics.Count > 0) item["metrics"] = metrics;
        if (entity is Polyline poly) { item["closed"] = poly.Closed; item["areaMm2"] = DrawingAreaToMm2(TryArea(entity)); }
        if (entity is Circle circle) { item["radiusMm"] = DrawingToMm(circle.Radius); item["center"] = PointJsonMm(circle.Center); }
        JsonObject geometry = GeometryJson(entity);
        if (geometry.Count > 0) item["geometry"] = geometry;
        if (include?.Any(value => value?.GetValue<string>() == "properties") == true)
            item["properties"] = BimProperties(entity.ObjectId);
        return item;
    }

    private static JsonObject GeometryJson(Entity entity)
    {
        var geometry = new JsonObject {
            ["kind"] = EntityKind(entity),
            ["unit"] = "mm",
            ["coordinateSystem"] = "WCS"
        };
        switch (entity)
        {
            case DBPoint point:
                geometry["position"] = PointJsonMm(point.Position);
                break;
            case Line line:
                geometry["start"] = PointJsonMm(line.StartPoint);
                geometry["end"] = PointJsonMm(line.EndPoint);
                break;
            case Polyline polyline:
                var points = new JsonArray();
                for (int index = 0; index < polyline.NumberOfVertices; ++index)
                    points.Add(PointJsonMm(polyline.GetPoint3dAt(index)));
                geometry["points"] = points;
                geometry["closed"] = polyline.Closed;
                break;
            case Circle circle:
                geometry["center"] = PointJsonMm(circle.Center);
                geometry["radiusMm"] = DrawingToMm(circle.Radius);
                geometry["normal"] = VectorJson(circle.Normal);
                break;
            case Arc arc:
                geometry["center"] = PointJsonMm(arc.Center);
                geometry["radiusMm"] = DrawingToMm(arc.Radius);
                geometry["startAngleDeg"] = arc.StartAngle * 180 / Math.PI;
                geometry["endAngleDeg"] = arc.EndAngle * 180 / Math.PI;
                geometry["normal"] = VectorJson(arc.Normal);
                break;
            case BlockReference block:
                CoordinateSystem3d blockFrame = block.BlockTransform.CoordinateSystem3d;
                geometry["position"] = PointJsonMm(block.Position);
                geometry["rotationDeg"] = block.Rotation * 180 / Math.PI;
                geometry["normal"] = VectorJson(blockFrame.Zaxis.GetNormal());
                geometry["coordinateFrame"] = new JsonObject {
                    ["origin"] = PointArrayMm(blockFrame.Origin),
                    ["xAxis"] = VectorJson(blockFrame.Xaxis.GetNormal()),
                    ["yAxis"] = VectorJson(blockFrame.Yaxis.GetNormal()),
                    ["normal"] = VectorJson(blockFrame.Zaxis.GetNormal())
                };
                geometry["scale"] = new JsonObject {
                    ["x"] = block.ScaleFactors.X,
                    ["y"] = block.ScaleFactors.Y,
                    ["z"] = block.ScaleFactors.Z
                };
                break;
        }
        return geometry;
    }

    private static string EntityKind(Entity entity) => entity switch
    {
        DBPoint => "point",
        Line => "line",
        Polyline poly when IsRectangle(poly) => "rectangle",
        Polyline => "polyline",
        Circle => "circle",
        Arc => "arc",
        Solid3d => "solid",
        BlockReference => "block",
        _ => "entity"
    };
    private static double TryArea(Entity e) { try { return e switch { Curve c when c.Closed => c.Area, _ => 0 }; } catch { return 0; } }

    private static void SaveBeforeIfRequested(JsonObject p)
    {
        if (!Bool(p, "saveBefore", false)) return;
        if (string.IsNullOrWhiteSpace(Database.Filename))
            throw new InvalidOperationException("saveBefore=true ist für eine noch nicht gespeicherte Zeichnung nicht möglich.");
        Database.SaveAs(Database.Filename, true, DwgVersion.Current, Database.SecurityParameters);
    }

    private static double Mm(double value)
    {
        UnitsValue units = Database.Insunits;
        return units == UnitsValue.Undefined || units == UnitsValue.Millimeters
            ? value
            : value * UnitsConverter.GetConversionFactor(UnitsValue.Millimeters, units);
    }

    private static double DrawingToMm(double value)
    {
        UnitsValue units = Database.Insunits;
        return units == UnitsValue.Undefined || units == UnitsValue.Millimeters
            ? value
            : value * UnitsConverter.GetConversionFactor(units, UnitsValue.Millimeters);
    }

    private static double DrawingAreaToMm2(double value)
    {
        double factor = DrawingToMm(1);
        return value * factor * factor;
    }

    private static double DrawingVolumeToMm3(double value)
    {
        double factor = DrawingToMm(1);
        return value * factor * factor * factor;
    }

    private static Point3d PointMm(JsonObject o, string key, Point3d fallback)
    {
        JsonObject? value = key.Length == 0 ? o : o[key] as JsonObject;
        if (value is null) return fallback;
        return new Point3d(Mm(Num(value, "x", DrawingToMm(fallback.X))),
            Mm(Num(value, "y", DrawingToMm(fallback.Y))),
            Mm(Num(value, "z", DrawingToMm(fallback.Z))));
    }

    private static JsonObject PointJsonMm(Point3d point) => new()
    {
        ["x"] = DrawingToMm(point.X),
        ["y"] = DrawingToMm(point.Y),
        ["z"] = DrawingToMm(point.Z)
    };

    private static JsonArray PointArrayMm(Point3d point) => new(
        DrawingToMm(point.X),
        DrawingToMm(point.Y),
        DrawingToMm(point.Z));

    private static JsonObject VectorJson(Vector3d vector) => new()
    {
        ["x"] = vector.X,
        ["y"] = vector.Y,
        ["z"] = vector.Z
    };

    private static double BoundsSpanAlong(Extents3d bounds, Vector3d direction)
    {
        Vector3d normalized = direction.GetNormal();
        return Math.Abs(normalized.X) * Math.Abs(bounds.MaxPoint.X - bounds.MinPoint.X)
            + Math.Abs(normalized.Y) * Math.Abs(bounds.MaxPoint.Y - bounds.MinPoint.Y)
            + Math.Abs(normalized.Z) * Math.Abs(bounds.MaxPoint.Z - bounds.MinPoint.Z);
    }

    private static string LayerName(ObjectId id)
    {
        using var tr = Database.TransactionManager.StartOpenCloseTransaction();
        string name = ((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
        tr.Commit();
        return name;
    }

    private static void EnsureLayerExists(Transaction tr, string layerName, bool create = false)
    {
        var table = (LayerTable)tr.GetObject(Database.LayerTableId, OpenMode.ForRead);
        if (table.Has(layerName)) return;
        if (!create) throw new ArgumentException($"Layer nicht gefunden: {layerName}");
        table.UpgradeOpen();
        var layer = new LayerTableRecord { Name = layerName };
        table.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    private static void EnsureWritableEntities(Transaction tr, IReadOnlyCollection<ObjectId> ids)
    {
        if (ids.Count == 0) throw new ArgumentException("Der Selector hat keine Entities aufgelöst.");
        var layerTable = (LayerTable)tr.GetObject(Database.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in ids)
        {
            if (!id.IsValid || id.IsErased) throw new ArgumentException($"Ungültiges oder gelöschtes Entity-Handle: {Handle(id)}");
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
                throw new ArgumentException($"Handle {Handle(id)} ist keine Entity.");
            if (layerTable.Has(entity.Layer))
            {
                var layer = (LayerTableRecord)tr.GetObject(layerTable[entity.Layer], OpenMode.ForRead);
                if (layer.IsLocked) throw new InvalidOperationException($"Entity {Handle(id)} liegt auf dem gesperrten Layer {entity.Layer}.");
            }
        }
    }

    private static void EnsureTargets(IReadOnlyCollection<ObjectId> ids, string method)
    {
        if (ids.Count == 0) throw new ArgumentException($"{method}: Der Selector hat keine Ziele aufgelöst.");
    }

    private static Point3d CombinedBoundsCenter(Transaction tr, IEnumerable<ObjectId> ids)
    {
        Extents3d? combined = null;
        foreach (ObjectId id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity) continue;
            try
            {
                Extents3d extents = entity.GeometricExtents;
                if (combined is null) combined = extents;
                else { Extents3d current = combined.Value; current.AddExtents(extents); combined = current; }
            }
            catch { }
        }
        if (combined is not Extents3d bounds) return Point3d.Origin;
        return new Point3d((bounds.MinPoint.X + bounds.MaxPoint.X) / 2,
            (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2,
            (bounds.MinPoint.Z + bounds.MaxPoint.Z) / 2);
    }

    private static Point3d EntityBoundsCenter(Entity entity)
    {
        try
        {
            Extents3d bounds = entity.GeometricExtents;
            return new Point3d((bounds.MinPoint.X + bounds.MaxPoint.X) / 2,
                (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2,
                (bounds.MinPoint.Z + bounds.MaxPoint.Z) / 2);
        }
        catch (System.Exception ex)
        {
            throw new InvalidOperationException($"Der Mittelpunkt von Entity {entity.Handle} konnte nicht ermittelt werden.", ex);
        }
    }

    private static IReadOnlyDictionary<ObjectId, JsonObject> BuildBimRelationIndex()
    {
        var result = new Dictionary<ObjectId, JsonObject>();
        try
        {
            foreach (ObjectId componentId in AnchoredBlocks.GetAnchoredBlockReferences(Database))
            {
                if (componentId.IsNull || !componentId.IsValid) continue;
                FullSubentityPath face = AnchoredBlocks.GetAnchorFace(componentId);
                if (face.IsNull) continue;
                ObjectId[] pathIds = face.GetObjectIds();
                ObjectId hostId = pathIds.LastOrDefault(id => !id.IsNull && id.IsValid && id != componentId);
                if (hostId.IsNull) continue;

                JsonObject componentRelations = RelationEntry(result, componentId);
                componentRelations["anchored"] = true;
                componentRelations["anchorHostHandle"] = Handle(hostId);
                componentRelations["anchorFace"] = new JsonObject {
                    ["pathHandles"] = new JsonArray(pathIds.Select(id => (JsonNode?)Handle(id)).ToArray()),
                    ["subentityType"] = face.SubentId.Type.ToString(),
                    ["subentityIndex"] = face.SubentId.IndexPtr.ToInt64()
                };

                JsonObject hostRelations = RelationEntry(result, hostId);
                JsonArray hosted = hostRelations["hostedAnchoredHandles"] as JsonArray ?? new JsonArray();
                if (!hosted.Any(node => node?.GetValue<string>() == Handle(componentId)))
                    hosted.Add(Handle(componentId));
                hostRelations["hostedAnchoredHandles"] = hosted;
            }
        }
        catch (System.Exception ex)
        {
            Log($"Managed BIM relation query failed: {ex.Message}");
        }
        return result;
    }

    private static JsonObject RelationEntry(IDictionary<ObjectId, JsonObject> relations, ObjectId id)
    {
        if (!relations.TryGetValue(id, out JsonObject? value))
        {
            value = new JsonObject();
            relations[id] = value;
        }
        return value;
    }

    private static double SolidVolume(ObjectId id)
    {
        using var tr = Database.TransactionManager.StartOpenCloseTransaction();
        if (tr.GetObject(id, OpenMode.ForRead, false) is not Solid3d solid)
            throw new ArgumentException($"Handle {Handle(id)} ist kein Solid3d und kann nicht als Öffnungshost verifiziert werden.");
        double volume = solid.MassProperties.Volume;
        tr.Commit();
        if (!double.IsFinite(volume) || volume <= 0)
            throw new InvalidOperationException($"Das Host-Solid {Handle(id)} besitzt kein auswertbares positives Volumen.");
        return volume;
    }

    private static bool IsRectangle(Polyline polyline)
    {
        if (!polyline.Closed || polyline.NumberOfVertices != 4) return false;
        var points = Enumerable.Range(0, 4).Select(polyline.GetPoint2dAt).ToArray();
        var vectors = Enumerable.Range(0, 4).Select(index => points[(index + 1) % 4] - points[index]).ToArray();
        if (vectors.Any(vector => vector.Length <= Tolerance)) return false;
        for (int index = 0; index < 4; ++index)
        {
            Vector2d left = vectors[index].GetNormal();
            Vector2d right = vectors[(index + 1) % 4].GetNormal();
            if (Math.Abs(left.DotProduct(right)) > 1e-5) return false;
        }
        return Math.Abs(vectors[0].Length - vectors[2].Length) <= Tolerance
            && Math.Abs(vectors[1].Length - vectors[3].Length) <= Tolerance;
    }

    private static string BimApiType(string classification)
    {
        return classification.ToUpperInvariant() switch
        {
            "BIMWALL" or "WALL" => "BIM_WALL",
            "BIMWINDOW" or "WINDOW" => "BIM_WINDOW",
            "BIMDOOR" or "DOOR" => "BIM_DOOR",
            "BIMCOLUMN" or "COLUMN" => "BIM_COLUMN",
            "BIMSLAB" or "SLAB" => "BIM_SLAB",
            "BIMBEAM" or "BEAM" => "BIM_BEAM",
            "BIMROOM" or "ROOM" => "BIM_ROOM",
            "BIMGENERICBUILDINGELT" or "GENERICBUILDINGELT" => "BIM_GENERIC_BUILDING_ELEMENT",
            _ => throw new ArgumentException($"Nicht unterstützte BricsCAD-BIM-Klassifikation: {classification}")
        };
    }

    private static string NativeBimClassification(ObjectId id)
    {
        try
        {
            if (id.IsNull || !id.IsValid || BIMClassification.IsUnclassified(id)) return string.Empty;
            string apiType = BIMClassification.GetClassificationName(id, false);
            return apiType.ToUpperInvariant() switch
            {
                "BIM_WALL" => "BIMWall",
                "BIM_WINDOW" => "BIMWindow",
                "BIM_DOOR" => "BIMDoor",
                "BIM_COLUMN" => "BIMColumn",
                "BIM_SLAB" => "BIMSlab",
                "BIM_BEAM" => "BIMBeam",
                "BIM_ROOM" => "BIMRoom",
                "BIM_GENERIC_BUILDING_ELEMENT" => "BIMGenericBuildingElt",
                _ => apiType
            };
        }
        catch { return string.Empty; }
    }

    private static List<string> HostedBimOpeningHandles(IEnumerable<ObjectId> ids)
    {
        var result = new List<string>();
        foreach (ObjectId id in ids.Distinct())
        {
            string classification = NativeBimClassification(id);
            bool opening = classification.Equals("BIMWindow", StringComparison.OrdinalIgnoreCase)
                || classification.Equals("BIMDoor", StringComparison.OrdinalIgnoreCase);
            if (opening && AnchoredBlocks.IsAnchoredBlockReference(id))
                result.Add(Handle(id));
        }
        return result;
    }

    private static string EntityName(ObjectId id)
    {
        try
        {
            using var tr = Database.TransactionManager.StartOpenCloseTransaction();
            string name = tr.GetObject(id, OpenMode.ForRead, false) is Entity entity ? EntityName(entity) : string.Empty;
            tr.Commit();
            return name;
        }
        catch { return string.Empty; }
    }

    private static string EntityName(Entity entity)
    {
        try
        {
            string native = BIMClassification.GetName(entity.ObjectId);
            if (!string.IsNullOrWhiteSpace(native)) return native;
        }
        catch { }
        try
        {
            using ResultBuffer? data = entity.GetXDataForApplication("BAREBONE_ENTITY");
            if (data is not null)
                foreach (TypedValue value in data)
                    if (value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                        return value.Value?.ToString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static JsonObject BimProperties(ObjectId id)
    {
        var properties = new JsonObject();
        try
        {
            foreach ((string key, string value) in BIMClassification.GetPropertiesMap(id))
                properties[key] = value;
        }
        catch { }
        try
        {
            Bricscad.Global.PropertyAccessor accessor = Bricscad.Global.PropertyService.CreateAccessor(id);
            foreach (string name in accessor.QualifiedPropertyNames)
            {
                try { properties[name] = JsonSafeValue(accessor.GetValue(name)); }
                catch { }
            }
        }
        catch { }
        return properties;
    }

    private static JsonNode? JsonSafeValue(object? value)
    {
        if (value is null) return null;
        return value switch
        {
            string text => JsonValue.Create(text),
            bool boolean => JsonValue.Create(boolean),
            byte number => JsonValue.Create(number),
            short number => JsonValue.Create(number),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            float number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
        };
    }

    private static void EnsureRegApp(Transaction tr, string name)
    {
        var table = (RegAppTable)tr.GetObject(Database.RegAppTableId, OpenMode.ForRead);
        if (table.Has(name)) return;
        table.UpgradeOpen();
        var record = new RegAppTableRecord { Name = name };
        table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static HashSet<string> CurrentSpaceBlockReferences()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var tr = Database.TransactionManager.StartOpenCloseTransaction();
        var space = (BlockTableRecord)tr.GetObject(Database.CurrentSpaceId, OpenMode.ForRead);
        foreach (ObjectId id in space)
            if (tr.GetObject(id, OpenMode.ForRead, false) is BlockReference)
                result.Add(Handle(id));
        tr.Commit();
        return result;
    }

    private static string ObjectClassName(ObjectId id)
    {
        using var tr = Database.TransactionManager.StartOpenCloseTransaction();
        string name = tr.GetObject(id, OpenMode.ForRead, false)?.GetType().Name ?? string.Empty;
        tr.Commit();
        return name;
    }

    private static bool IsInsideBounds(Point3d point, JsonObject bounds)
    {
        Point3d min = PointMm(bounds, "min", Point3d.Origin);
        Point3d max = PointMm(bounds, "max", Point3d.Origin);
        return point.X >= Math.Min(min.X, max.X) - Tolerance && point.X <= Math.Max(min.X, max.X) + Tolerance
            && point.Y >= Math.Min(min.Y, max.Y) - Tolerance && point.Y <= Math.Max(min.Y, max.Y) + Tolerance
            && point.Z >= Math.Min(min.Z, max.Z) - Tolerance && point.Z <= Math.Max(min.Z, max.Z) + Tolerance;
    }

    private void Remember(IEnumerable<ObjectId> ids, bool extruded)
    {
        lock (_lastResultLock)
        {
            _lastResult.Clear(); _lastResult.AddRange(ids);
            if (extruded) { _lastExtruded.Clear(); _lastExtruded.AddRange(ids); }
        }
    }

    private static JsonObject Result(string schema, JsonObject result)
    {
        result["schema"] ??= schema;
        result["provider"] ??= CapabilityRegistry.Provider;
        result["runtimeInstanceId"] ??= RuntimeIdentity.Id;
        if (result["revision"] is not null)
            result["revisionScope"] ??= "runtimeInstance";
        return result;
    }
    private static JsonArray Handles(IEnumerable<ObjectId> ids)
    {
        var result = new JsonArray();
        foreach (ObjectId id in ids) result.Add(Handle(id));
        return result;
    }
    private static string Handle(ObjectId id) => id.Handle.ToString();
    private static Point3d Point(JsonObject o, string key, Point3d fallback) => key.Length == 0 ? Point(o, fallback) : o[key] is JsonObject p ? Point(p, fallback) : fallback;
    private static Point3d Point(JsonObject o, Point3d fallback) => new(Num(o, "x", fallback.X), Num(o, "y", fallback.Y), Num(o, "z", fallback.Z));
    private static double Angle(JsonObject o, string radians, string degrees, double fallback) => o[radians] is not null ? Num(o, radians, fallback) : Num(o, degrees, fallback * 180 / Math.PI) * Math.PI / 180;
    private static string Str(JsonObject o, string key, string fallback = "") => o[key]?.GetValue<string>()?.Trim() ?? fallback;
    private static int Int(JsonObject o, string key, int fallback) => o[key]?.GetValue<int>() ?? fallback;
    private static double Num(JsonObject o, string key, double fallback) => o[key]?.GetValue<double>() ?? fallback;
    private static bool Bool(JsonObject o, string key, bool fallback) => o[key]?.GetValue<bool>() ?? fallback;
    private static List<string> Strings(JsonObject o, string key) => o[key]?.AsArray().Select(n => n?.GetValue<string>() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
    private static bool NearlyEqual(double left, double right)
        => Math.Abs(left - right) <= Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right))) * 1e-6;
}

