using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;

/// <summary>
/// Independent expectations for the read-only hull context (contract C05). Every expected
/// value is checked against the generated hull or the public context queries, never against
/// the private voxel solid.
/// </summary>
internal static class HullContextTests
{
    public static void Run()
    {
        var generator = new HullGenerator();
        VerifyBoundsAndParity(generator);
        VerifyArmorMatchesGeneratedPlacements(generator);
        VerifyReservedAirIsNotUsableCavity(generator);
        VerifyCavityAndProtectedShell(generator);
        VerifyLocalDeckAndFloor(generator);
        VerifyDeckOpeningMask(generator);
        VerifyCancellationAndInvalidParameters(generator);
        VerifyArmorDiagnostics(generator);

        Console.WriteLine(
            "Hull context: bounds/parity, armor-intent parity, reserved air, usable cavity, " +
            "protected shell, local deck/floor, deck masks, cancellation and armor diagnostics passed.");
    }

    private static void VerifyBoundsAndParity(HullGenerator generator)
    {
        var parameters = HullParameters.Default;
        var hull = generator.Generate(parameters);
        var context = HullGenerator.CreateContext(parameters);

        Require(context.MinX == hull.MinX && context.MaxX == hull.MaxX &&
                context.MinY == hull.MinY && context.MaxY == hull.MaxY &&
                context.MinZ == hull.MinZ && context.MaxZ == hull.MaxZ,
            "The context bounds must equal the generated hull's occupied bounds.");
        Require(context.MirrorSum == context.MinX + context.MaxX,
            "The mirror sum must be MinX + MaxX.");
        Require(context.Lattice.MinCell == context.MinX && context.Lattice.MaxCell == context.MaxX,
            "The lattice must span the context's real columns.");
        Require(context.Lattice.MirrorSum == context.MirrorSum,
            "The lattice mirror sum must match the context's.");
        Require(context.CenterPlaneX == context.Lattice.CenterPlane,
            "The centre plane must be the lattice's true mirror plane.");
        Require(context.Lattice.Width == hull.OccupiedWidth,
            "The lattice width must match the generated hull's occupied width.");

        // Width 21 is odd: the plane falls on a real centre column.
        Require(context.Lattice.Width == 21 && context.Lattice.HasCenterColumn &&
                context.CenterPlaneX.IsWholeMetre && context.CenterPlaneX.Metres == 0,
            "An odd-width hull must have an integral centre column at x = 0.");
        Require(context.Lattice.IsThicknessRepresentable(1) == context.Lattice.HasCenterColumn,
            "A one-cell centred slab is representable exactly when the plane has a centre column.");

        var evenParameters = parameters with { Width = 18 };
        var evenHull = generator.Generate(evenParameters);
        var evenContext = HullGenerator.CreateContext(evenParameters);
        Require(evenContext.MinX == evenHull.MinX && evenContext.MaxX == evenHull.MaxX,
            "The even-width context bounds must match its hull.");
        Require(evenContext.Lattice.Width == 18 && !evenContext.Lattice.HasCenterColumn,
            "An even-width hull must not report a centre column.");
        Require(!evenContext.CenterPlaneX.IsWholeMetre &&
                evenContext.CenterPlaneX.Metres == (evenHull.MinX + evenHull.MaxX) / 2.0,
            "The even-width centre plane must be the half-integral mirror sum.");
        Require(!evenContext.Lattice.IsThicknessRepresentable(1) && evenContext.Lattice.IsThicknessRepresentable(2),
            "An even-width hull centres even slabs and not a single-cell wall.");
    }

    private static void VerifyArmorMatchesGeneratedPlacements(HullGenerator generator)
    {
        var parameters = HullParameters.Default;
        var hull = generator.Generate(parameters);
        var context = HullGenerator.CreateContext(parameters);

        var generatedMaterials = new Dictionary<HullCell, MaterialKind>();
        foreach (var block in hull.Blocks)
        foreach (var cell in block.OccupiedCells)
            Require(generatedMaterials.TryAdd(new HullCell(cell.X, cell.Y, cell.Z), block.Material),
                $"Generated placements overlap at ({cell.X}, {cell.Y}, {cell.Z}).");

        foreach (var intent in context.EnumerateArmor().Where(intent => intent.IsStructuralArmor))
        {
            Require(generatedMaterials.TryGetValue(intent.Cell, out var material),
                $"A structural armor intent at {intent.Cell} has no generated placement.");
            Require(material == intent.Material,
                $"Armor intent at {intent.Cell} carries {intent.Material} but the placement is {material}.");
        }

        foreach (var (cell, material) in generatedMaterials)
        {
            Require(context.TryGetArmor(cell.X, cell.Y, cell.Z, out var intent),
                $"Generated placement at {cell} has no armor intent.");
            Require(intent.Material == material && intent.IsStructuralArmor,
                $"Generated placement at {cell} does not match its armor intent.");
            Require(context.IsOccupied(cell.X, cell.Y, cell.Z),
                $"Generated placement at {cell} is not occupied by the analytic solid.");
        }

        Require(context.ArmorCellCount == context.EnumerateArmor().Count(),
            "ArmorCellCount must equal the enumerated armor intents.");
        Require(context.StructuralArmorCellCount + context.ReservedAirCellCount == context.ArmorCellCount,
            "Every armor intent is either structural armor or reserved air.");
        Require(context.StructuralArmorCellCount == generatedMaterials.Count,
            "Every generated placement is a structural armor intent and vice versa.");

        Require(!context.IsOccupied(context.MaxX + 5, context.MinY, context.MinZ) &&
                context.RoleAt(context.MaxX + 5, context.MinY, context.MinZ) == HullCellRole.Outside,
            "A cell beyond the hull must be outside and unoccupied.");
    }

    private static void VerifyReservedAirIsNotUsableCavity(HullGenerator generator)
    {
        var parameters = HullParameters.Default with
        {
            HullArmor = new ArmorLayout(
            [
                new ArmorLayer(MaterialKind.Metal),
                ArmorLayer.Air,
                new ArmorLayer(MaterialKind.Metal),
            ]),
            Beamify = false,
        };
        var hull = generator.Generate(parameters);
        var context = HullGenerator.CreateContext(parameters);

        var reserved = context.EnumerateArmor().Where(intent => intent.IsReservedAir).ToArray();
        Require(reserved.Length > 0, "A deliberate air layer must produce reserved-armor-air cells.");
        foreach (var intent in reserved)
        {
            Require(intent.Material is null && intent.Role == HullCellRole.ReservedArmorAir,
                "A reserved-air intent must have a null material and the reserved-air role.");
            Require(context.IsReservedArmorAir(intent.Cell.X, intent.Cell.Y, intent.Cell.Z),
                "IsReservedArmorAir must agree with the enumerated intent.");
            Require(!context.IsUsableCavity(intent.Cell.X, intent.Cell.Y, intent.Cell.Z),
                "Reserved armor air must never be usable cavity.");
            Require(context.RoleAt(intent.Cell.X, intent.Cell.Y, intent.Cell.Z) == HullCellRole.ReservedArmorAir,
                "RoleAt must report reserved armor air.");
        }

        // Reserved air is an armor reservation, not a placement: the real hull leaves it empty.
        var placed = hull.Blocks.SelectMany(block => block.OccupiedCells)
            .Select(cell => new HullCell(cell.X, cell.Y, cell.Z)).ToHashSet();
        Require(reserved.All(intent => !placed.Contains(intent.Cell)),
            "Reserved armor air must not appear as a generated placement.");

        // The same air layout's structural cells still correspond to real placements.
        foreach (var intent in context.EnumerateArmor().Where(intent => intent.IsStructuralArmor))
            Require(placed.Contains(intent.Cell), $"Structural armor at {intent.Cell} was not placed.");
    }

    private static void VerifyCavityAndProtectedShell(HullGenerator generator)
    {
        var context = HullGenerator.CreateContext(HullParameters.Default);

        HullCell? cavity = null;
        for (var z = context.MinZ; z <= context.MaxZ && cavity is null; z++)
        for (var y = context.MinY; y <= context.MaxY && cavity is null; y++)
        for (var x = context.MinX; x <= context.MaxX; x++)
        {
            if (context.IsUsableCavity(x, y, z))
            {
                cavity = new HullCell(x, y, z);
                break;
            }
        }

        Require(cavity is not null, "A default hull must expose at least one usable cavity cell.");
        Require(context.RoleAt(cavity!.Value.X, cavity.Value.Y, cavity.Value.Z) == HullCellRole.Cavity,
            "A usable cavity cell must report the cavity role.");
        Require(!context.IsProtectedShellCell(cavity.Value.X, cavity.Value.Y, cavity.Value.Z),
            "A cavity cell must not be a protected shell cell.");

        var side = context.EnumerateArmor().FirstOrDefault(intent => intent.Role == HullCellRole.SideArmor);
        Require(side is not null, "A default hull must expose side armor.");
        Require(!context.IsUsableCavity(side!.Cell.X, side.Cell.Y, side.Cell.Z),
            "An exposed side armor cell must not be usable cavity.");
        Require(context.IsProtectedShellCell(side.Cell.X, side.Cell.Y, side.Cell.Z),
            "An exposed side armor cell must be protected from feature cuts.");
        Require(side.Depth == 0 && side.Region == ArmorRegion.Side,
            "An exposed side armor intent must be depth zero and side-owned.");

        var bottom = context.EnumerateArmor().FirstOrDefault(intent => intent.Role == HullCellRole.BottomArmor);
        Require(bottom is not null && context.IsProtectedShellCell(bottom.Cell.X, bottom.Cell.Y, bottom.Cell.Z),
            "An exposed bottom armor cell must be protected from feature cuts.");
    }

    private static void VerifyLocalDeckAndFloor(HullGenerator generator)
    {
        // The axe bow lowers its deck toward the stem, so the local deck is not a global maximum.
        var parameters = HullParameters.Default with
        {
            Length = 60,
            Width = 17,
            Height = 12,
            BowStyle = BowStyle.Axe,
            SternStyle = SternStyle.Counter,
        };
        var context = HullGenerator.CreateContext(parameters);

        for (var z = context.MinZ; z <= context.MaxZ; z++)
        {
            var occupiedRows = Enumerable.Range(context.MinY, context.MaxY - context.MinY + 1)
                .Where(y => Enumerable.Range(context.MinX, context.MaxX - context.MinX + 1)
                    .Any(x => context.IsOccupied(x, y, z)))
                .ToArray();
            Require(occupiedRows.Length > 0, $"Station z={z} has no occupied rows.");
            Require(context.DeckYAt(z) == occupiedRows.Max(),
                $"DeckYAt({z}) must be the local top occupied row.");
            Require(context.FloorYAt(z) == occupiedRows.Min(),
                $"FloorYAt({z}) must be the local bottom occupied row.");
        }

        var localDecks = Enumerable.Range(context.MinZ, context.MaxZ - context.MinZ + 1)
            .Select(context.DeckYAt).Distinct().ToArray();
        Require(localDecks.Length > 1,
            "An axe bow must vary the local deck elevation rather than reporting a global maximum.");
        Require(context.DeckYAt(context.MinZ - 1) == int.MinValue &&
                context.FloorYAt(context.MaxZ + 1) == int.MinValue,
            "Out-of-range stations must report the no-surface sentinel.");
    }

    private static void VerifyDeckOpeningMask(HullGenerator generator)
    {
        var parameters = HullParameters.Default;
        var context = HullGenerator.CreateContext(parameters);
        var deck = context.EnumerateArmor().FirstOrDefault(intent => intent.Role == HullCellRole.DeckArmor);
        Require(deck is not null, "A decked hull must expose deck armor.");
        var side = context.EnumerateArmor().First(intent => intent.Role == HullCellRole.SideArmor);
        var otherArmor = context.EnumerateArmor().First(intent => intent.IsStructuralArmor);

        Require(!DeckOpeningMask.None.Allows(deck!.Cell),
            "The empty mask must allow nothing.");

        var noneContext = HullGenerator.CreateContext(parameters, DeckOpeningMask.None);
        Require(!noneContext.IsAuthorizedDeckOpening(deck.Cell),
            "No deck cell may be authorized without a mask entry.");

        var deckMask = DeckOpeningMask.FromCells([deck.Cell]);
        var deckContext = HullGenerator.CreateContext(parameters, deckMask);
        Require(deckContext.IsAuthorizedDeckOpening(deck.Cell),
            "A mask naming a deck armor cell must authorize that cell.");
        Require(deckContext.DeckOpenings.Allows(deck.Cell),
            "The supplied mask must be exposed unchanged.");

        // Naming a non-deck cell is not authorization, however explicit the mask is.
        var sideContext = HullGenerator.CreateContext(parameters, DeckOpeningMask.FromCells([side.Cell]));
        Require(!sideContext.IsAuthorizedDeckOpening(side.Cell),
            "A mask naming a side armor cell must not authorize it as a deck opening.");
        Require(!sideContext.IsAuthorizedDeckOpening(otherArmor.Cell),
            "An unnamed armor cell must never be an authorized deck opening.");

        // The mask is a snapshot: mutating the caller's sequence afterward must not change it.
        var mutable = new List<HullCell> { deck.Cell };
        var snapshot = DeckOpeningMask.FromCells(mutable);
        mutable.Clear();
        Require(snapshot.Allows(deck.Cell) && snapshot.Count == 1,
            "A deck-opening mask must be immutable after construction.");
        Require(DeckOpeningMask.FromCells([]).Count == 0,
            "An empty sequence must produce an empty mask.");
    }

    private static void VerifyCancellationAndInvalidParameters(HullGenerator generator)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        HullBuildContext? leaked = null;
        var cancellationObserved = false;
        try
        {
            leaked = HullGenerator.CreateContext(HullParameters.Default, null, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }

        Require(cancellationObserved, "A pre-cancelled token must cancel the context build.");
        Require(leaked is null, "A cancelled context build must not leak a partial context.");

        var invalidRejected = false;
        try
        {
            HullGenerator.CreateContext(HullParameters.Default with { Length = 1 });
        }
        catch (HullGenerationException)
        {
            invalidRejected = true;
        }

        Require(invalidRejected, "Invalid parameters must throw HullGenerationException.");
    }

    private static void VerifyArmorDiagnostics(HullGenerator generator)
    {
        // Thirteen deck layers cannot fit above the default keel: layer 13 is reported rather
        // than silently dropped, and the context still returns because it is read-only.
        var parameters = HullParameters.Default with
        {
            DeckArmor = new ArmorLayout(Enumerable.Repeat(MaterialKind.Metal, 13)),
        };
        var context = HullGenerator.CreateContext(parameters);
        Require(context.Diagnostics.Any(diagnostic =>
                diagnostic.IsError && diagnostic.Code == HullContextDiagnosticCodes.ArmorLayerDoesNotFit),
            "An armor layer that cannot fit must be carried as a context diagnostic.");

        // A valid hull reports no armor or topology errors.
        var validContext = HullGenerator.CreateContext(HullParameters.Default);
        Require(validContext.Diagnostics.Count == 0,
            $"A valid hull must report no context diagnostics, found {validContext.Diagnostics.Summary()}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
