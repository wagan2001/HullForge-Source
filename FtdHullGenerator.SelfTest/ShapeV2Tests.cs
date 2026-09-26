using System.Text.Json;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using static SlopeFillTestSupport;
using System.Collections.Concurrent;

internal static class ShapeV2Tests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog, bool full, int workerCount = 0)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);

        VerifyShapeDomain();
        var styleCases = full ? VerifyStyleMatrix(generator, workerCount) : VerifyStyleSmoke(generator);
        VerifySectionExpression(generator);
        VerifyFlatBottom(generator, catalog);
        VerifyRegionalIsolation(generator);
        VerifyConstructionVariants(generator);
        VerifySmoothing(generator);
        VerifyRaisedProfile(generator, catalog);

        Console.WriteLine(full
            ? $"Shape V2: {styleCases} body/bow/stern cases, regional isolation, flat bottom, construction, smoothing, raised profile, and export bounds passed."
            : $"Shape V2: {styleCases} representative body/bow/stern cases, regional isolation, flat bottom, construction, smoothing, raised profile, and export bounds passed.");
    }

    private static void VerifyShapeDomain()
    {
        var namedStyles = Enum.GetValues<BodyStyle>().Where(style => style != BodyStyle.Custom).ToArray();
        Require(namedStyles.Length == 7, $"Shape V2 exposes {namedStyles.Length} named body styles instead of 7.");
        foreach (var style in namedStyles)
            Require(BodyShapeSettings.ForStyle(style).Style == style, $"The {style} bundle reports the wrong body style.");

        var defaults = HullShapeSettings.Default;
        Require(defaults == new HullShapeSettings(
                    new BowShapeSettings(0, 0.15, 45),
                    new BodyShapeSettings(BodyStyle.Rounded, 0.35, 0.10, -0.65),
                    new SternShapeSettings(0, 0, 25),
                    HullProfileSettings.Flat),
            "The documented Dolphin/default Shape V2 bundle changed.");

        var legacy = HullShapeSettings.FromLegacy(-0.4, 0.3, -0.2);
        Require(legacy.Bow.Fullness == -0.4 && legacy.Stern.Fullness == 0.3 &&
                legacy.Body.Fullness == -0.2 && legacy.Body.Style == BodyStyle.Custom,
            "Legacy scalar adaptation did not preserve the three original controls.");

        Require((HullParameters.Default with
                {
                    Shape = defaults with { Bow = defaults.Bow with { Flare = double.NaN } },
                }).Validate().Any(error => error.Contains("finite", StringComparison.OrdinalIgnoreCase)),
            "A non-finite regional control was accepted.");
        Require((HullParameters.Default with
                {
                    Shape = defaults with { Bow = defaults.Bow with { EntranceLengthPercent = 4 } },
                }).Validate().Any(error => error.Contains("entrance", StringComparison.OrdinalIgnoreCase)),
            "A bow entrance below 5% was accepted.");
        Require((HullParameters.Default with
                {
                    Shape = defaults with { Stern = defaults.Stern with { RunLengthPercent = 81 } },
                }).Validate().Any(error => error.Contains("run length", StringComparison.OrdinalIgnoreCase)),
            "A stern run above 80% was accepted.");
        Require((HullParameters.Default with
                {
                    Shape = defaults with { Profile = defaults.Profile with { BowDeckRise = HullParameters.Default.Height - 1 } },
                }).Validate().Any(error => error.Contains("deck rise", StringComparison.OrdinalIgnoreCase)),
            "A profile rise above Height - 2 was accepted.");
        Require((HullParameters.Default with
                {
                    Shape = defaults with { Body = defaults.Body with { FlatBottom = 1.5 } },
                }).Validate().Any(error => error.Contains("flat bottom", StringComparison.OrdinalIgnoreCase)),
            "A body flat bottom above 1 was accepted.");
    }

    private static int VerifyStyleMatrix(HullGenerator generator, int workerCount = 0)
    {
        var count = 0;
        var cases = (
            from bodyStyle in Enum.GetValues<BodyStyle>().Where(style => style != BodyStyle.Custom)
            from bowStyle in Enum.GetValues<BowStyle>()
            from sternStyle in Enum.GetValues<SternStyle>()
            select (bodyStyle, bowStyle, sternStyle,
                parameters: HullParameters.Default with
                {
                    Length = 48,
                    Width = 15,
                    Height = 10,
                    BowStyle = bowStyle,
                    SternStyle = sternStyle,
                    Shape = HullShapeSettings.Default with
                    {
                        Body = BodyShapeSettings.ForStyle(bodyStyle),
                        Profile = HullProfileSettings.Flat,
                    },
                }))
            .ToArray();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = SelfTestProfiles.ResolveWorkerCount(workerCount),
        };
        // Run every case before reporting so one failure does not hide the rest.
        var exceptions = new ConcurrentQueue<Exception>();

        Parallel.ForEach(
            cases,
            options,
            () => new HullGenerator(),
            (testCase, _, localGenerator) =>
            {
                try
                {
                    var hull = localGenerator.Generate(testCase.parameters);
                    var errors = HullGeometryValidator.Validate(hull);
                    Require(errors.Count == 0,
                        $"{testCase.bodyStyle}/{testCase.bowStyle}/{testCase.sternStyle} failed geometry validation: {string.Join("; ", errors)}");
                    Require(hull.OccupiedLength == testCase.parameters.Length &&
                            hull.OccupiedWidth == testCase.parameters.Width &&
                            hull.OccupiedHeight == testCase.parameters.OverallHeight,
                        $"{testCase.bodyStyle}/{testCase.bowStyle}/{testCase.sternStyle} did not occupy its requested extents.");
                    Interlocked.Increment(ref count);
                }
                catch (Exception exception)
                {
                    exceptions.Enqueue(exception);
                }

                return localGenerator;
            },
            _ => { });

        if (!exceptions.IsEmpty)
            throw new AggregateException(exceptions);

        Require(count == 252, $"The Shape V2 style matrix covered {count} cases instead of 252.");
        return count;
    }

    private static int VerifyStyleSmoke(HullGenerator generator)
    {
        var count = 0;
        var bodyStyle = BodyStyle.Rounded;
        var shape = HullShapeSettings.Default with
        {
            Body = BodyShapeSettings.ForStyle(bodyStyle),
            Profile = HullProfileSettings.Flat,
        };
        foreach (var bowStyle in Enum.GetValues<BowStyle>())
        foreach (var sternStyle in Enum.GetValues<SternStyle>())
        {
            var parameters = HullParameters.Default with
            {
                Length = 48,
                Width = 15,
                Height = 10,
                BowStyle = bowStyle,
                SternStyle = sternStyle,
                Shape = shape,
            };
            var hull = generator.Generate(parameters);
            var errors = HullGeometryValidator.Validate(hull);
            Require(errors.Count == 0,
                $"{bodyStyle}/{bowStyle}/{sternStyle} failed representative geometry validation: {string.Join("; ", errors)}");
            Require(hull.OccupiedLength == parameters.Length &&
                    hull.OccupiedWidth == parameters.Width &&
                    hull.OccupiedHeight == parameters.OverallHeight,
                $"{bodyStyle}/{bowStyle}/{sternStyle} did not occupy its requested extents.");
            count++;
        }

        Require(count == 36, $"The representative Shape V2 style smoke covered {count} cases instead of 36.");
        return count;
    }

    private static void VerifySectionExpression(HullGenerator generator)
    {
        var basis = HullParameters.Default with
        {
            Length = 80,
            Width = 41,
            Height = 17,
            Beamify = false,
            Shape = HullShapeSettings.Default with
            {
                Bow = HullShapeSettings.Default.Bow with { EntranceLengthPercent = 25 },
                Stern = HullShapeSettings.Default.Stern with { RunLengthPercent = 25 },
                Profile = HullProfileSettings.Flat,
            },
        };
        var rounded = generator.Generate(basis with
        {
            Shape = basis.EffectiveShape with { Body = BodyShapeSettings.ForStyle(BodyStyle.Rounded) },
        });
        var deepV = generator.Generate(basis with
        {
            Shape = basis.EffectiveShape with { Body = BodyShapeSettings.ForStyle(BodyStyle.DeepV) },
        });
        var flatWide = generator.Generate(basis with
        {
            Shape = basis.EffectiveShape with { Body = BodyShapeSettings.ForStyle(BodyStyle.FlatWide) },
        });
        const int bodyStation = 0;
        const int lowRow = 2;
        var roundedLow = RowBreadth(rounded, bodyStation, lowRow);
        var deepVLow = RowBreadth(deepV, bodyStation, lowRow);
        var flatLow = RowBreadth(flatWide, bodyStation, lowRow);
        Require(deepVLow < roundedLow && flatLow > roundedLow,
            $"Body styles lost their low-section ordering: Deep V {deepVLow}, Rounded {roundedLow}, Flat/Wide {flatLow}.");

        var tumblehome = generator.Generate(basis with
        {
            Shape = basis.EffectiveShape with { Body = BodyShapeSettings.ForStyle(BodyStyle.Tumblehome) },
        });
        var bodyCells = CellsAtStation(tumblehome, bodyStation);
        var deckY = bodyCells.Max(cell => cell.Y);
        var deckBreadth = RowBreadth(tumblehome, bodyStation, deckY);
        var maximumBreadth = bodyCells.GroupBy(cell => cell.Y)
            .Max(row => row.Max(cell => cell.X) - row.Min(cell => cell.X) + 1);
        Require(deckBreadth < maximumBreadth,
            $"Tumblehome did not draw the deck inward: deck {deckBreadth}, maximum {maximumBreadth}.");
    }

    private static void VerifyRegionalIsolation(HullGenerator generator)
    {
        var baseShape = HullShapeSettings.Default with
        {
            Bow = new BowShapeSettings(0, -0.9, 30),
            Body = BodyShapeSettings.ForStyle(BodyStyle.Rounded),
            Stern = new SternShapeSettings(0, -0.9, 25),
            Profile = HullProfileSettings.Flat,
        };
        var basis = HullParameters.Default with
        {
            Length = 80,
            Width = 31,
            Height = 15,
            Beamify = false,
            Shape = baseShape,
        };

        var original = generator.Generate(basis);
        var bowFlared = generator.Generate(basis with
        {
            Shape = baseShape with { Bow = baseShape.Bow with { Flare = 0.9 } },
        });
        var bowStart = basis.Length - 1 - RegionIntervals(basis.Length, baseShape.Bow.EntranceLengthPercent) + original.MinZ;
        RequireChangedOnly(original, bowFlared, cell => cell.Z >= bowStart,
            "Bow flare changed cells outside the bow region.");

        var longEntranceShape = baseShape with
        {
            Bow = baseShape.Bow with { EntranceLengthPercent = 55 },
        };
        var longEntrance = generator.Generate(basis with { Shape = longEntranceShape });
        var earliestBowStart = basis.Length - 1 - RegionIntervals(basis.Length, longEntranceShape.Bow.EntranceLengthPercent) + original.MinZ;
        RequireChangedOnly(original, longEntrance, cell => cell.Z >= earliestBowStart - 1,
            "Bow entrance length changed cells in the stern region.");

        var sternFlared = generator.Generate(basis with
        {
            Shape = baseShape with { Stern = baseShape.Stern with { SideShape = 0.9 } },
        });
        var sternEnd = original.MinZ + RegionIntervals(basis.Length, baseShape.Stern.RunLengthPercent);
        RequireChangedOnly(original, sternFlared, cell => cell.Z <= sternEnd,
            "Stern side shape changed cells outside the stern region.");

        var longRunShape = baseShape with
        {
            Stern = baseShape.Stern with { RunLengthPercent = 55 },
        };
        var longRun = generator.Generate(basis with { Shape = longRunShape });
        var latestSternEnd = original.MinZ + RegionIntervals(basis.Length, longRunShape.Stern.RunLengthPercent);
        RequireChangedOnly(original, longRun, cell => cell.Z <= latestSternEnd + 1,
            "Stern run length changed cells in the bow region.");

        var overlapShape = baseShape with
        {
            Bow = baseShape.Bow with { EntranceLengthPercent = 80 },
            Stern = baseShape.Stern with { RunLengthPercent = 80 },
        };
        var overlap = generator.Generate(basis with
        {
            Length = 12,
            Width = 9,
            Height = 7,
            Shape = overlapShape,
        });
        Require(HullGeometryValidator.Validate(overlap).Count == 0 && overlap.OccupiedLength == 12,
            "A short hull with overlapping 80% entrance/run requests was not redistributed safely.");

        foreach (var sternStyle in new[] { SternStyle.Counter, SternStyle.Canoe })
        {
            var shortRunShape = baseShape with
            {
                Bow = baseShape.Bow with { EntranceLengthPercent = 70 },
                Stern = baseShape.Stern with { RunLengthPercent = 10 },
            };
            var styled = generator.Generate(basis with
            {
                SternStyle = sternStyle,
                Shape = shortRunShape,
            });
            var sternLimit = styled.MinZ + RegionIntervals(basis.Length, 10);
            var raisedStations = Cells(styled.Blocks)
                .GroupBy(cell => cell.Z)
                .Where(station => station.Min(cell => cell.Y) > styled.MinY)
                .Select(station => station.Key)
                .ToArray();
            Require(raisedStations.Length > 0 && raisedStations.All(z => z <= sternLimit),
                $"{sternStyle} profile cuts escaped the requested short stern run when the bow entrance was long.");
        }
    }

    private static void VerifyConstructionVariants(HullGenerator generator)
    {
        var shape = HullShapeSettings.Default with
        {
            Bow = new BowShapeSettings(-0.35, 0.65, 50),
            Body = BodyShapeSettings.ForStyle(BodyStyle.HardChine),
            Stern = new SternShapeSettings(0.25, -0.45, 35),
            Profile = HullProfileSettings.Flat,
        };
        var basis = HullParameters.Default with
        {
            Length = 64,
            Width = 21,
            Height = 14,
            Shape = shape,
        };

        foreach (var width in new[] { 21, 20 })
        foreach (var hasDeck in new[] { true, false })
        {
            var hull = generator.Generate(basis with
            {
                Width = width,
                DeckArmor = hasDeck ? ArmorLayout.Single(MaterialKind.Metal) : null,
            });
            Require(HullGeometryValidator.Validate(hull).Count == 0,
                $"Shape V2 failed for width {width}, deck={hasDeck}.");
        }

        var decklessBody = generator.Generate(basis with
        {
            HullArmor = ArmorLayout.Single(MaterialKind.Metal),
            DeckArmor = null,
            Beamify = false,
        });
        var bodyTop = Cells(decklessBody.Blocks)
            .Where(cell => cell.Z == 0 && cell.Y == basis.Height - 1)
            .Select(cell => cell.X)
            .Order()
            .ToArray();
        Require(bodyTop.SequenceEqual(new[] { decklessBody.MinX, decklessBody.MaxX }),
            $"Deckless regional repair restored interior top cells at the body station: {string.Join(", ", bodyTop)}.");

        var cubeHull = generator.Generate(basis with { Beamify = false });
        var beamHull = generator.Generate(basis with { Beamify = true });
        Require(Cells(cubeHull.Blocks).SetEquals(Cells(beamHull.Blocks)) && beamHull.BlockCount < cubeHull.BlockCount,
            "Beam construction changed the Shape V2 occupied lattice or failed to merge blocks.");

        var airPole = generator.Generate(basis with
        {
            HullArmor = new ArmorLayout([
                new ArmorLayer(MaterialKind.Metal),
                ArmorLayer.Air,
                new ArmorLayer(MaterialKind.HeavyArmor, usePoles: true),
            ]),
            DeckArmor = new ArmorLayout([
                new ArmorLayer(MaterialKind.Wood),
                ArmorLayer.Air,
                new ArmorLayer(MaterialKind.LightweightAlloy, usePoles: true),
            ]),
            Beamify = true,
        });
        Require(HullGeometryValidator.Validate(airPole).Count == 0 &&
                airPole.Blocks.Any(block => block.Shape is BlockShape.Pole1 or BlockShape.Pole2 or BlockShape.Pole3 or BlockShape.Pole4),
            "Layered Shape V2 armor with air gaps and poles failed.");

        var bulb = generator.Generate(basis with
        {
            BowStyle = BowStyle.Raked,
            SternStyle = SternStyle.Canoe,
            HasBulb = true,
            Bulb = new BulbSettings(10, 40, 0, 0),
        });
        Require(HullGeometryValidator.Validate(bulb).Count == 0,
            "A regional hard-chine hull with a bulb failed geometry validation.");
    }

    private static void VerifySmoothing(HullGenerator generator)
    {
        var parameters = HullParameters.Default with
        {
            Length = 64,
            Width = 19,
            Height = 13,
            Beamify = true,
            Shape = new HullShapeSettings(
                new BowShapeSettings(-0.45, 0.75, 52),
                BodyShapeSettings.ForStyle(BodyStyle.DeepV),
                new SternShapeSettings(0.35, -0.50, 38),
                HullProfileSettings.Flat),
        };
        var baseline = generator.Generate(parameters);
        foreach (var method in SelfTestProfiles.ProductSmoothingMethods)
        {
            var filled = generator.Generate(parameters with { Smoothing = method });
            SlopeFillTestSupport.VerifySmoothing(baseline, filled, method);
        }
    }

    private static void VerifyRaisedProfile(HullGenerator generator, FtdBlockCatalog catalog)
    {
        var shape = HullShapeSettings.Default with
        {
            Bow = HullShapeSettings.Default.Bow with { EntranceLengthPercent = 35 },
            Stern = HullShapeSettings.Default.Stern with { RunLengthPercent = 25 },
            Profile = new HullProfileSettings(3, 2, 2, 1),
        };
        var parameters = HullParameters.Default with
        {
            Length = 72,
            Width = 21,
            Height = 12,
            Beamify = false,
            Shape = shape,
            HullArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor]),
            DeckArmor = new ArmorLayout([MaterialKind.Wood, MaterialKind.Lead]),
        };
        var hull = generator.Generate(parameters);
        Require(HullGeometryValidator.Validate(hull).Count == 0,
            "The raised-profile layered hull failed geometry validation.");
        Require(parameters.OverallHeight == 15 && hull.OccupiedHeight == parameters.OverallHeight,
            $"Height 12 with a 3m deck rise occupied {hull.OccupiedHeight}m instead of 15m.");
        Require(MainWindow.CreateBlueprintName(parameters) == "HF_Custom_72x21x12",
            "A raised profile used overall height instead of midship height in its blueprint name.");

        var cellsByZ = Cells(hull.Blocks).GroupBy(cell => cell.Z)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var floorByZ = cellsByZ.ToDictionary(pair => pair.Key, pair => pair.Value.Min(cell => cell.Y));
        var deckByZ = cellsByZ.ToDictionary(pair => pair.Key, pair => pair.Value.Max(cell => cell.Y));
        Require(deckByZ[hull.MinZ] == parameters.Height - 1 + shape.Profile.SternDeckRise &&
                deckByZ[hull.MaxZ] == parameters.Height - 1 + shape.Profile.BowDeckRise &&
                floorByZ[hull.MinZ] == shape.Profile.SternKeelRise &&
                floorByZ[hull.MaxZ] == shape.Profile.BowKeelRise,
            "The requested endpoint deck or keel rise was not realized.");
        for (var z = hull.MinZ + 1; z <= 0; z++)
        {
            Require(deckByZ[z] <= deckByZ[z - 1] && floorByZ[z] <= floorByZ[z - 1],
                $"The stern profile rose toward midships at z={z}.");
        }
        for (var z = 0; z < hull.MaxZ; z++)
        {
            Require(deckByZ[z] <= deckByZ[z + 1] && floorByZ[z] <= floorByZ[z + 1],
                $"The bow profile fell toward its endpoint at z={z}.");
        }

        var materials = CellMaterials(hull);
        var checkedInteriorDecks = 0;
        foreach (var (z, stationCells) in cellsByZ)
        {
            var deckY = deckByZ[z];
            var deckRow = stationCells.Where(cell => cell.Y == deckY).OrderBy(cell => cell.X).ToArray();
            var minX = deckRow[0].X;
            var maxX = deckRow[^1].X;
            Require(materials[(minX, deckY, z)] == MaterialKind.Metal &&
                    materials[(maxX, deckY, z)] == MaterialKind.Metal,
                $"Deck armor replaced hull-owned rim armor at z={z}.");
            if (maxX - minX < 2)
                continue;
            checkedInteriorDecks++;
            Require(Enumerable.Range(minX + 1, maxX - minX - 1)
                    .All(x => materials[(x, deckY, z)] == MaterialKind.Wood),
                $"The top deck layer did not follow local elevation at z={z}.");
        }
        Require(checkedInteriorDecks > 0 &&
                materials.Any(pair => pair.Value == MaterialKind.Wood && pair.Key.Y > parameters.Height - 1) &&
                materials.Any(pair => pair.Value == MaterialKind.Lead && pair.Key.Y > parameters.Height - 2),
            "Raised local deck elevations did not contain both requested deck layers.");

        var bulbParameters = parameters with
        {
            Length = 80,
            Width = 19,
            HullArmor = ArmorLayout.Single(MaterialKind.Metal),
            DeckArmor = ArmorLayout.Single(MaterialKind.Metal),
            BowStyle = BowStyle.Raked,
            HasBulb = true,
            Bulb = new BulbSettings(10, 40, 0, 0),
            Shape = shape with { Profile = new HullProfileSettings(2, 0, 2, 0) },
        };
        var bulbHull = generator.Generate(bulbParameters);
        var bulbCells = Cells(bulbHull.Blocks);
        var stemZ = bulbCells.Where(cell => cell.Y == bulbHull.MaxY).Max(cell => cell.Z);
        Require(HullGeometryValidator.Validate(bulbHull).Count == 0 && bulbHull.MaxZ > stemZ &&
                bulbCells.Any(cell => cell.Z == bulbHull.MaxZ && cell.Y < bulbHull.MaxY),
            "A raised forefoot lost its face-connected bulb extension.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"HullForgeShapeV2-{Guid.NewGuid():N}");
        try
        {
            var export = new BlueprintExporter().Export(hull, catalog, tempDirectory, "Shape V2 raised profile");
            using var document = JsonDocument.Parse(File.ReadAllText(export.FilePath));
            var craft = document.RootElement.GetProperty("Blueprint");
            Require(craft.GetProperty("MinCords").GetString() == $"{hull.MinX},{hull.MinY},{hull.MinZ}" &&
                    craft.GetProperty("MaxCords").GetString() == $"{hull.MaxX},{hull.MaxY},{hull.MaxZ}",
                "A raised profile export did not use its actual occupied bounds.");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static void VerifyFlatBottom(HullGenerator generator, FtdBlockCatalog catalog)
    {
        // The reported case: Flat/Wide, neutral sides, maximum hard chine, a short
        // entrance and run that leave a clear body, and no bulb or profile rise.
        var flatBody = BodyShapeSettings.ForStyle(BodyStyle.FlatWide) with
        {
            Style = BodyStyle.Custom,
            SideShape = 0,
            Chine = 0.9,
        };
        Require(flatBody.FlatBottom == 1,
            "The Flat/Wide bundle must enable the full flat bottom before the reproduction edits.");
        var shape = HullShapeSettings.Default with
        {
            Bow = HullShapeSettings.Default.Bow with { EntranceLengthPercent = 10 },
            Stern = HullShapeSettings.Default.Stern with { RunLengthPercent = 10 },
            Body = flatBody,
            Profile = HullProfileSettings.Flat,
        };
        var basis = HullParameters.Default with
        {
            Length = 80,
            Width = 21,
            Height = 12,
            Beamify = false,
            Smoothing = SmoothingMethod.None,
            HasBulb = false,
            BowStyle = BowStyle.Pointed,
            SternStyle = SternStyle.Transom,
            HullArmor = ArmorLayout.Single(MaterialKind.Metal),
            BottomArmor = ArmorLayout.Single(MaterialKind.Metal),
            DeckArmor = ArmorLayout.Single(MaterialKind.Metal),
            Shape = shape,
        };

        // Odd and even widths, and a second hull size. The independent oracle is
        // purely geometric: at the midship station the lowest occupied row must be the
        // full requested beam, as one contiguous run, and the row directly above must
        // still own both outer columns. That is a horizontal floor meeting a vertical
        // side; a tapered keel, a one-cell floor, or an accidental voxel corner all
        // fail it, and none of it is computed from the section formula under test.
        foreach (var (width, length, height) in new[] { (21, 80, 12), (20, 80, 12), (17, 60, 10) })
        {
            var parameters = basis with { Width = width, Length = length, Height = height };
            var hull = generator.Generate(parameters);
            Require(HullGeometryValidator.Validate(hull).Count == 0,
                $"The flat-bottom {length}x{width}x{height} hull failed geometry validation.");
            Require(hull.OccupiedWidth == width, $"The flat-bottom {width} m hull did not occupy its requested beam.");

            var station = CellsAtStation(hull, 0);
            var floorY = station.Min(cell => cell.Y);
            Require(floorY == hull.MinY, "The midship floor was not the lowest row of a level-profile hull.");
            var floorRow = station.Where(cell => cell.Y == floorY).Select(cell => cell.X).Order().ToArray();
            Require(floorRow.SequenceEqual(Enumerable.Range(hull.MinX, width)),
                $"The midship floor at {width} m beam was not one full-width horizontal run: " +
                $"{floorRow.First()}..{floorRow.Last()} with {floorRow.Length} cells.");
            var above = station.Where(cell => cell.Y == floorY + 1).Select(cell => cell.X).ToHashSet();
            Require(above.Contains(hull.MinX) && above.Contains(hull.MaxX),
                "The vertical side did not rise directly from the outer edge of the flat floor.");
            var twoAbove = station.Where(cell => cell.Y == floorY + 2).Select(cell => cell.X).ToHashSet();
            Require(twoAbove.Contains(hull.MinX) && twoAbove.Contains(hull.MaxX),
                "The side wall above the flat floor narrowed within a row of the floor.");
        }

        // Bow/body/stern transitions stay watertight: every station's floor is one
        // contiguous run and neighbouring stations share at least one floor cell, so
        // the broad floor cannot open a hole or seam along the hull.
        var transitionHull = generator.Generate(basis);
        var floorByStation = Cells(transitionHull.Blocks)
            .GroupBy(cell => cell.Z)
            .ToDictionary(group => group.Key, group => group
                .Where(cell => cell.Y == group.Min(candidate => candidate.Y))
                .Select(cell => cell.X).Order().ToArray());
        for (var z = transitionHull.MinZ; z <= transitionHull.MaxZ; z++)
        {
            var floor = floorByStation[z];
            Require(floor.SequenceEqual(Enumerable.Range(floor.First(), floor.Length)),
                $"The flat-bottom floor had a hole at station z={z}.");
            if (z == transitionHull.MinZ)
                continue;
            Require(floor.Intersect(floorByStation[z - 1]).Any(),
                $"The flat-bottom floor broke between stations z={z - 1} and z={z}.");
        }

        // Side/bottom armor and a usable cavity: layered bottom armor fills the floor,
        // side armor fills the wall, and the interior stays open.
        var armored = generator.Generate(basis with
        {
            HullArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor]),
            BottomArmor = new ArmorLayout([MaterialKind.Wood, MaterialKind.LightweightAlloy]),
        });
        Require(HullGeometryValidator.Validate(armored).Count == 0, "The flat-bottom layered hull failed validation.");
        var armoredMaterials = CellMaterials(armored);
        Require(armoredMaterials.Values.Contains(MaterialKind.Wood) && armoredMaterials.Values.Contains(MaterialKind.LightweightAlloy),
            "The flat-bottom hull did not place its bottom armor layers.");
        var armoredFloorY = Cells(armored.Blocks).Where(cell => cell.Z == 0).Min(cell => cell.Y);
        Require(Enumerable.Range(armored.MinX, armored.OccupiedWidth)
                .All(x => armoredMaterials.TryGetValue((x, armoredFloorY, 0), out var material) && material == MaterialKind.Wood),
            "The flat-bottom floor was not fully covered by its outer bottom armor.");
        Require(!Cells(armored.Blocks).Contains((0, armoredFloorY + 2, 0)),
            "The flat-bottom hull lost its usable interior cavity.");

        // Beam merging is a pure repartitioning of the same flat-bottom cells.
        var cubeHull = generator.Generate(basis with { Beamify = false });
        var beamHull = generator.Generate(basis with { Beamify = true });
        Require(Cells(cubeHull.Blocks).SetEquals(Cells(beamHull.Blocks)) && beamHull.BlockCount < cubeHull.BlockCount,
            "Beam construction changed the flat-bottom lattice or failed to merge blocks.");

        // Representative rounded, U, and V sections keep their tapered keel: the
        // flat-bottom extension is off for their bundles and the floor stays narrow.
        foreach (var style in new[] { BodyStyle.Rounded, BodyStyle.U, BodyStyle.V })
        {
            Require(BodyShapeSettings.ForStyle(style).FlatBottom == 0,
                $"{style} unexpectedly enabled the flat-bottom extension.");
            var tapered = generator.Generate(basis with
            {
                Shape = shape with { Body = BodyShapeSettings.ForStyle(style) },
            });
            var taperedStation = CellsAtStation(tapered, 0);
            var taperedFloorY = taperedStation.Min(cell => cell.Y);
            var taperedFloor = taperedStation.Where(cell => cell.Y == taperedFloorY).Select(cell => cell.X).ToArray();
            Require(taperedFloor.Length < tapered.OccupiedWidth,
                $"{style} lost its tapered keel and became flat-bottomed.");
        }

        // Vertical and horizontal fill stay additive over the flat-bottom hull.
        foreach (var method in new[] { SmoothingMethod.VerticalSlopeFill, SmoothingMethod.HorizontalSlopeFill })
        {
            var filled = generator.Generate(basis with { Smoothing = method });
            VerifyAdditive(cubeHull, filled);
        }

        // Preview and export consume one list: the exported file expands back to the
        // same cells and keeps the full-width midship floor.
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"HullForgeFlatBottom-{Guid.NewGuid():N}");
        try
        {
            var export = new BlueprintExporter().Export(cubeHull, catalog, tempDirectory, "Shape V2 flat bottom");
            using var document = JsonDocument.Parse(File.ReadAllText(export.FilePath));
            var root = document.RootElement;
            var craft = root.GetProperty("Blueprint");
            Require(craft.GetProperty("MinCords").GetString() == $"{cubeHull.MinX},{cubeHull.MinY},{cubeHull.MinZ}" &&
                    craft.GetProperty("MaxCords").GetString() == $"{cubeHull.MaxX},{cubeHull.MaxY},{cubeHull.MaxZ}",
                "The flat-bottom export did not use its actual occupied bounds.");
            var exportedCells = ExpandExport(craft, root, catalog, cubeHull);
            Require(exportedCells.SetEquals(Cells(cubeHull.Blocks)),
                "The flat-bottom export described a different occupied lattice than the preview.");
            var exportedFloor = exportedCells.Where(cell => cell.Z == 0 && cell.Y == cubeHull.MinY)
                .Select(cell => cell.X).Order().ToArray();
            Require(exportedFloor.SequenceEqual(Enumerable.Range(cubeHull.MinX, cubeHull.OccupiedWidth)),
                "The exported midship floor was not the same full-width horizontal run as the preview.");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static HashSet<(int X, int Y, int Z)> ExpandExport(
        JsonElement craft,
        JsonElement root,
        FtdBlockCatalog catalog,
        GeneratedHull hull)
    {
        var shapes = catalog.Blocks.ToDictionary(block => block.Guid, block => block.Shape);
        var items = root.GetProperty("ItemDictionary").EnumerateObject()
            .ToDictionary(entry => int.Parse(entry.Name), entry => Guid.Parse(entry.Value.GetString()!));
        var positions = craft.GetProperty("BLP").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var rotations = craft.GetProperty("BLR").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var ids = craft.GetProperty("BlockIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var cells = new HashSet<(int X, int Y, int Z)>();
        for (var index = 0; index < positions.Length; index++)
        {
            var shape = shapes[items[ids[index]]];
            var parts = positions[index].Split(',').Select(int.Parse).ToArray();
            var placement = new BlockPlacement(
                shape, hull.Parameters.SurfaceMaterial, parts[0], parts[1], parts[2], rotations[index]);
            foreach (var cell in placement.OccupiedCells)
                cells.Add(cell);
        }
        return cells;
    }

    private static int RowBreadth(GeneratedHull hull, int z, int y)
    {
        var row = hull.Blocks.SelectMany(block => block.OccupiedCells)
            .Where(cell => cell.Z == z && cell.Y == y)
            .Select(cell => cell.X)
            .Distinct()
            .ToArray();
        Require(row.Length > 0, $"Hull has no cells at row y={y}, z={z}.");
        return row.Max() - row.Min() + 1;
    }

    private static (int X, int Y, int Z)[] CellsAtStation(GeneratedHull hull, int z) =>
        hull.Blocks.SelectMany(block => block.OccupiedCells)
            .Where(cell => cell.Z == z)
            .Distinct()
            .ToArray();

    private static void RequireChangedOnly(
        GeneratedHull before,
        GeneratedHull after,
        Func<(int X, int Y, int Z), bool> belongsToRegion,
        string message)
    {
        var differences = Cells(before.Blocks);
        differences.SymmetricExceptWith(Cells(after.Blocks));
        Require(differences.Count > 0 && differences.All(belongsToRegion),
            differences.Count == 0
                ? $"{message} The two hulls occupied identical cells."
                : $"{message} Changed-cell bounds: " +
                  $"X {differences.Min(cell => cell.X)}..{differences.Max(cell => cell.X)}, " +
                  $"Y {differences.Min(cell => cell.Y)}..{differences.Max(cell => cell.Y)}, " +
                  $"Z {differences.Min(cell => cell.Z)}..{differences.Max(cell => cell.Z)}.");
    }

    private static int RegionIntervals(int length, int percent) =>
        Math.Clamp(
            (int)Math.Round((length - 1) * percent / 100d, MidpointRounding.AwayFromZero),
            1,
            length - 2);

    private static Dictionary<(int X, int Y, int Z), MaterialKind> CellMaterials(GeneratedHull hull)
    {
        var result = new Dictionary<(int X, int Y, int Z), MaterialKind>();
        foreach (var block in hull.Blocks)
        foreach (var cell in block.OccupiedCells)
            Require(result.TryAdd(cell, block.Material), $"Overlapping materials at {cell}.");
        return result;
    }
}
