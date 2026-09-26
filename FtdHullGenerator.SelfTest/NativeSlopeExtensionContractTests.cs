using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain;
using System.Buffers.Binary;
using System.Numerics;

internal static class NativeSlopeExtensionContractTests
{
    public static void Run()
    {
        foreach (var (length, mesh, scale, offset) in new[]
                 {
                     (5, 1, 5f, 2f), (10, 1, 10f, 4.5f), (11, 2, 5.5f, 2.25f),
                     (20, 2, 10f, 4.5f), (21, 3, 7f, 3f), (30, 3, 10f, 4.5f),
                     (31, 4, 7.75f, 3.375f), (40, 4, 10f, 4.5f),
                 })
        {
            var actual = NativeSlopeExtensionRule.Calculate(length);
            Require(actual == (mesh, scale, offset), $"Independent {length} m boundary example changed.");
        }

        for (var length = 5; length <= 40; length++)
        {
            var (mesh, scale, offset) = NativeSlopeExtensionRule.Calculate(length);
            Require(mesh is >= 1 and <= 4 && scale <= 10,
                $"{length} m violates the mesh vocabulary or scale bound.");
            Require(Math.Abs(mesh * (double)scale - length) < 0.000002,
                $"{length} m does not retain its requested longitudinal extent.");
            Require(mesh == 1 || (double)length / (mesh - 1) > 10,
                $"{length} m did not select the smallest donor within the scale bound.");
            Require(Math.Abs(-0.5 * scale + offset + 0.5) < 0.000001,
                $"{length} m moves the independently measured local starting edge.");
        }

        foreach (var length in new[] { int.MinValue, -1, 0, 1, 2, 3, 4, 41, int.MaxValue })
        {
            try { NativeSlopeExtensionRule.Calculate(length); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidOperationException($"Unsupported extension length {length} was accepted.");
        }
        Require(NativeSlopeExtensionPlan.Empty.IsValid &&
            NativeSlopeExtensionPlan.Empty.Extensions.Length == 0, "Empty native-only plan changed.");
        VerifySingleTransformSource();
        Console.WriteLine("Native extension contract: all 5–40 m integer lengths, independent donor boundaries, " +
            "10x bound, starting edge and native-length rejection passed.");
    }

    private static void VerifySingleTransformSource()
    {
        var anchor = new ResolvedSlopeAnchor(new BlockPlacement(BlockShape.Slope4, MaterialKind.Metal,
            1, 4, 0, 16) { Origin = BlockOrigin.Smoothing },
            Guid.Parse("db9ed060-d556-435b-945c-19c923e233d3"));
        var tether = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(tether, 1);
        BinaryPrimitives.WriteInt32LittleEndian(tether.AsSpan(4), 4);
        var record = new DecorationRecord(0, Guid.Empty,
        [
            new DecorationChunk(0, Guid.Parse("5548037e-8428-43f8-bcb6-d730dbcd0a79").ToByteArray()),
            new DecorationChunk(1, Floats(1.001f, 1.001f, 5)),
            new DecorationChunk(2, Floats(0, 0, 2)),
            new DecorationChunk(3, Floats(-0f, -0f, 270)),
            new DecorationChunk(5, tether),
        ]);
        var extension = new NativeSlopeExtension(anchor, SlopeExtensionKind.Horizontal, 5, record);
        Require(BitConverter.SingleToInt32Bits(extension.EulerAngles.X) == int.MinValue,
            "Typed preview values lost the record's signed zero.");
        Require(extension.Record == record && extension.Anchor == anchor,
            "Resolved extension changed the authoritative record or anchor.");
        var point = extension.TransformMeshPoint(new Vector3(0, 0, -.5f));
        Require(Vector3.Distance(point, new Vector3(1, 4, -.5f)) < 0.000001f,
            "Preview transform disagrees with the independent horizontal start-edge example.");
        Require(extension.VisualBounds.MinZ == -.5 && extension.VisualBounds.MaxZ == 4.5,
            "Visual bounds were replaced with native four-cell bounds.");
        Require(!new NativeSlopeExtensionPlan([extension], []).IsValid,
            "A nonempty plan without a source identity became authoritative.");
        ExpectInvalid(record with { Chunks = record.Chunks.SetItem(4, new DecorationChunk(5, new byte[12])) });
        ExpectInvalid(record with { Chunks = record.Chunks.SetItem(1, new DecorationChunk(1, Floats(1.001f, 1.001f, 6))) });
        ExpectInvalid(record with { Chunks = record.Chunks.SetItem(2, new DecorationChunk(2, Floats(float.NaN, 0, 2))) });
        ExpectInvalid(record with { Chunks = record.Chunks.Add(new DecorationChunk(6, new byte[] { 1 })) });

        void ExpectInvalid(DecorationRecord invalid)
        {
            try { _ = new NativeSlopeExtension(anchor, SlopeExtensionKind.Horizontal, 5, invalid); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Inconsistent or unsupported extension record was accepted.");
        }
        static byte[] Floats(float x, float y, float z)
        {
            var bytes = new byte[12];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, x);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), z);
            return bytes;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
