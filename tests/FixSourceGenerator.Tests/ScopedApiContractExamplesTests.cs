using System;
using FixSourceGenerator.Tests.ScopedApiContractExamples;
using Xunit;

namespace FixSourceGenerator.Tests
{
    /// <summary>
    /// Exercises the issue #29 scoped reader/writer design spike (docs/CONTRACT.md §12,
    /// ScopedApiContractExamples.cs). These are NOT tests of the shipped generator output —
    /// WriterEmitter.cs/ReaderEmitter.cs are unchanged by this issue — they prove the *proposed*
    /// API shape actually compiles, round-trips, and behaves as specified.
    /// </summary>
    public class ScopedApiContractExamplesTests
    {

        [Fact]
        public void RoundTrips_RequiredComponentAndBackpatchGroup()
        {
            Span<byte> destination = new byte[512];

            var writer = new ProtoOrderWriter(destination, "ORD-1"u8);
            var instrument = writer.BeginInstrument("EUR/USD"u8);
            instrument.WriteSecurityID("SEC-1"u8);
            var tail = instrument.EndInstrument('1', 100_000m, '1');
            tail.WritePrice(1.2345m);

            var group = tail.BeginNoPartyIDs();
            group.AddEntry("PARTY-A"u8, 'D', 3);
            group.AddEntry("PARTY-B"u8, null, null);
            int length = group.EndGroup().Finish();

            var reader = new ProtoOrderReader(destination.Slice(0, length));
            Assert.Equal("ORD-1", System.Text.Encoding.ASCII.GetString(reader.ClOrdID));
            Assert.Equal('1', reader.Side);
            Assert.Equal(100_000m, reader.OrderQty);
            Assert.Equal('1', reader.OrdType);
            Assert.True(reader.TryGetPrice(out decimal price));
            Assert.Equal(1.2345m, price);

            Assert.Equal("EUR/USD", System.Text.Encoding.ASCII.GetString(reader.Instrument.Symbol));
            Assert.True(reader.Instrument.TryGetSecurityID(out var secId));
            Assert.Equal("SEC-1", System.Text.Encoding.ASCII.GetString(secId));

            Assert.Equal(2, reader.NoPartyIDs.Count);
            int seen = 0;
            foreach (var entry in reader.NoPartyIDs)
            {
                seen++;
                if (seen == 1)
                {
                    Assert.Equal("PARTY-A", System.Text.Encoding.ASCII.GetString(entry.PartyID));
                    Assert.True(entry.TryGetPartyIDSource(out char src));
                    Assert.Equal('D', src);
                    Assert.True(entry.TryGetPartyRole(out int role));
                    Assert.Equal(3, role);
                }
                else
                {
                    Assert.Equal("PARTY-B", System.Text.Encoding.ASCII.GetString(entry.PartyID));
                    Assert.False(entry.TryGetPartyIDSource(out _));
                    Assert.False(entry.TryGetPartyRole(out _));
                }
            }

            Assert.Equal(2, seen);
        }

        [Fact]
        public void BackpatchGroup_ShiftsBytesWhenDigitWidthGrows()
        {
            // 11 entries needs 2 digits for tag 453, but the writer only reserves 1 — proves the
            // "backpatching is not free" claim (docs/CONTRACT.md §12.5): EndGroup() must shift
            // every byte written since the placeholder before writing the wider count.
            Span<byte> destination = new byte[2048];
            var writer = new ProtoOrderWriter(destination, "ORD-2"u8);
            var tail = writer.BeginInstrument("EUR/USD"u8).EndInstrument('2', 50m, '2');
            var group = tail.BeginNoPartyIDs();
            for (int i = 0; i < 11; i++)
            {
                group.AddEntry("P"u8, null, null);
            }

            int length = group.EndGroup().Finish();

            var reader = new ProtoOrderReader(destination.Slice(0, length));
            Assert.Equal(11, reader.NoPartyIDs.Count);
            int count = 0;
            foreach (var entry in reader.NoPartyIDs)
            {
                Assert.Equal("P", System.Text.Encoding.ASCII.GetString(entry.PartyID));
                count++;
            }

            Assert.Equal(11, count);
        }

        [Fact]
        public void OmittedGroup_IsAbsent_NotZero_OnTheWire()
        {
            Span<byte> destination = new byte[256];
            var writer = new ProtoOrderWriter(destination, "ORD-3"u8);
            var tail = writer.BeginInstrument("EUR/USD"u8).EndInstrument('1', 1m, '1');
            int length = tail.Finish(); // NoPartyIDs never begun at all

            string wire = System.Text.Encoding.ASCII.GetString(destination.Slice(0, length));
            Assert.DoesNotContain("453=", wire);

            var reader = new ProtoOrderReader(destination.Slice(0, length));
            Assert.False(reader.NoPartyIDs.TryGetCount(out _));
            Assert.Equal(0, reader.NoPartyIDs.Count); // convenience accessor collapses absent -> 0
        }

        [Fact]
        public void ExplicitEmptyGroup_WritesZero_DistinctFromOmission()
        {
            Span<byte> destination = new byte[256];
            var writer = new ProtoOrderWriter(destination, "ORD-4"u8);
            var tail = writer.BeginInstrument("EUR/USD"u8).EndInstrument('1', 1m, '1');
            int length = tail.BeginNoPartyIDs().EndGroup().Finish(); // begun, but zero entries

            string wire = System.Text.Encoding.ASCII.GetString(destination.Slice(0, length));
            Assert.Contains("453=0", wire);

            var reader = new ProtoOrderReader(destination.Slice(0, length));
            Assert.True(reader.NoPartyIDs.TryGetCount(out int count));
            Assert.Equal(0, count);
        }

        [Fact]
        public void UpfrontCountVariant_RoundTrips_AndValidatesCount()
        {
            Span<byte> destination = new byte[512];
            var writer = new ProtoOrderWriter(destination, "ORD-5"u8);
            var tail = writer.BeginInstrument("EUR/USD"u8).EndInstrument('1', 1m, '1');
            var group = tail.BeginNoPartyIDs(expectedCount: 2);
            group.AddEntry("A"u8, null, null);
            group.AddEntry("B"u8, null, null);
            int length = group.EndGroup().Finish();

            var reader = new ProtoOrderReader(destination.Slice(0, length));
            Assert.Equal(2, reader.NoPartyIDs.Count);
        }

        [Fact]
        public void UpfrontCountVariant_ThrowsOnOverCount()
        {
            Span<byte> destination = new byte[512];
            var writer = new ProtoOrderWriter(destination, "ORD-6"u8);
            var tail = writer.BeginInstrument("EUR/USD"u8).EndInstrument('1', 1m, '1');
            var group = tail.BeginNoPartyIDs(expectedCount: 1);
            group.AddEntry("A"u8, null, null);

            var threw = false;
            try { group.AddEntry("B"u8, null, null); }
            catch (InvalidOperationException) { threw = true; }
            Assert.True(threw);
        }

        [Fact]
        public void UpfrontCountVariant_ThrowsOnUnderCount()
        {
            Span<byte> destination = new byte[512];
            var writer = new ProtoOrderWriter(destination, "ORD-7"u8);
            var tail = writer.BeginInstrument("EUR/USD"u8).EndInstrument('1', 1m, '1');
            var group = tail.BeginNoPartyIDs(expectedCount: 2);
            group.AddEntry("A"u8, null, null);

            var threw = false;
            try { group.EndGroup(); }
            catch (InvalidOperationException) { threw = true; }
            Assert.True(threw);
        }

        [Fact]
        public void ParentAccess_WhileGroupScopeActive_IsRejected()
        {
            // "Parent access while a child scope is active" (docs/CONTRACT.md §12.3): the tail
            // writer's own _state flag is mutated through the by-ref struct receiver, so a second
            // call on the *same* variable after BeginNoPartyIDs() correctly throws.
            Span<byte> destination = new byte[256];
            var writer = new ProtoOrderWriter(destination, "ORD-8"u8);
            var tail = writer.BeginInstrument("EUR/USD"u8).EndInstrument('1', 1m, '1');
            _ = tail.BeginNoPartyIDs();

            var threwOnPrice = false;
            try { tail.WritePrice(1m); }
            catch (InvalidOperationException) { threwOnPrice = true; }
            Assert.True(threwOnPrice);

            var threwOnFinish = false;
            try { tail.Finish(); }
            catch (InvalidOperationException) { threwOnFinish = true; }
            Assert.True(threwOnFinish);
        }

        [Fact]
        public void RepeatedClosure_OnSameHandle_IsRejected()
        {
            Span<byte> destination = new byte[256];
            var writer = new ProtoOrderWriter(destination, "ORD-9"u8);
            var instrument = writer.BeginInstrument("EUR/USD"u8);
            _ = instrument.EndInstrument('1', 1m, '1');

            var threwOnRepeatedEnd = false;
            try { instrument.EndInstrument('1', 1m, '1'); }
            catch (InvalidOperationException) { threwOnRepeatedEnd = true; }
            Assert.True(threwOnRepeatedEnd);

            var threwOnRepeatedBegin = false;
            try { writer.BeginInstrument("EUR/USD"u8); }
            catch (InvalidOperationException) { threwOnRepeatedBegin = true; }
            Assert.True(threwOnRepeatedBegin);
        }

        [Fact]
        public void SharedState_RejectsStaleCopiedAndDefaultHandles()
        {
            Span<byte> destination = new byte[256];
            Span<int> state = stackalloc int[ProtoSafeWriterOwner.RequiredStateLength];
            var owner = new ProtoSafeWriterOwner(destination, state);
            var writer = owner.BeginOrder("ORD-10"u8);
            var staleCopy = writer;
            _ = writer.BeginInstrument("EUR/USD"u8);

            AssertInvalidOperation(ref staleCopy);

            ProtoSafeOrderWriter defaultHandle = default;
            AssertInvalidOperation(ref defaultHandle);

            static void AssertInvalidOperation(ref ProtoSafeOrderWriter handle)
            {
                bool threw = false;
                try { handle.BeginInstrument("USD/JPY"u8); }
                catch (InvalidOperationException) { threw = true; }
                Assert.True(threw);
            }
        }

        [Fact]
        public void SharedState_PoisonsAllCopiesAfterCapacityFailure()
        {
            Span<byte> destination = stackalloc byte[10];
            Span<int> state = stackalloc int[ProtoSafeWriterOwner.RequiredStateLength];
            var owner = new ProtoSafeWriterOwner(destination, state);
            var writer = owner.BeginOrder("A"u8);
            var copy = writer;

            bool capacityFailed = false;
            try { writer.BeginInstrument("TOO-LONG"u8); }
            catch (ArgumentException) { capacityFailed = true; }
            Assert.True(capacityFailed);

            bool continuationFailed = false;
            try { copy.BeginInstrument("B"u8); }
            catch (InvalidOperationException) { continuationFailed = true; }
            Assert.True(continuationFailed);
        }

        [Fact]
        public void OptionalComponent_RequiresItsChildWhenPresent()
        {
            Span<byte> destination = new byte[256];
            Span<int> state = stackalloc int[ProtoSafeWriterOwner.RequiredStateLength];
            var owner = new ProtoSafeWriterOwner(destination, state);
            var tail = owner.BeginOrder("ORD"u8)
                .BeginInstrument("EUR/USD"u8)
                .EndInstrument('1', 1m, '1');
            int length = tail.BeginOptionalBroker("BROKER"u8).EndOptionalBroker().Finish();
            Assert.Contains("76=BROKER", System.Text.Encoding.ASCII.GetString(destination.Slice(0, length)));

            Span<int> invalidState = stackalloc int[ProtoSafeWriterOwner.RequiredStateLength];
            var invalidOwner = new ProtoSafeWriterOwner(new byte[256], invalidState);
            var invalidTail = invalidOwner.BeginOrder("ORD"u8)
                .BeginInstrument("EUR/USD"u8)
                .EndInstrument('1', 1m, '1');
            bool threw = false;
            try { invalidTail.BeginOptionalBroker(ReadOnlySpan<byte>.Empty); }
            catch (ArgumentException) { threw = true; }
            Assert.True(threw);
        }

        [Fact]
        public void EmptyTextIsRejectedWithoutConfusingPresenceAndOmission()
        {
            bool requiredThrew = false;
            try { _ = new ProtoOrderWriter(new byte[128], ReadOnlySpan<byte>.Empty); }
            catch (ArgumentException) { requiredThrew = true; }
            Assert.True(requiredThrew);

            Span<byte> destination = new byte[256];
            var writer = new ProtoOrderWriter(destination, "ORD"u8);
            bool symbolThrew = false;
            try { writer.BeginInstrument(ReadOnlySpan<byte>.Empty); }
            catch (ArgumentException) { symbolThrew = true; }
            Assert.True(symbolThrew);

            writer = new ProtoOrderWriter(destination, "ORD"u8);
            var instrument = writer.BeginInstrument("EUR/USD"u8);
            bool optionalThrew = false;
            try { instrument.WriteSecurityID(ReadOnlySpan<byte>.Empty); }
            catch (ArgumentException) { optionalThrew = true; }
            Assert.True(optionalThrew);

            var permissiveReader = new ProtoInstrumentReader("55=EUR/USD\u000148=\u0001"u8);
            Assert.True(permissiveReader.TryGetSecurityID(out var empty));
            Assert.True(empty.IsEmpty);

            Span<int> state = stackalloc int[ProtoSafeWriterOwner.RequiredStateLength];
            var safeOwner = new ProtoSafeWriterOwner(new byte[256], state);
            var safeInstrument = safeOwner.BeginOrder("ORD"u8).BeginInstrument("EUR/USD"u8);
            bool enumThrew = false;
            try { safeInstrument.EndInstrument('?', 1m, '1'); }
            catch (ArgumentOutOfRangeException) { enumThrew = true; }
            Assert.True(enumThrew);
        }

        [Fact]
        public void SharedState_ConsumesEachSourceHandleOnTransition()
        {
            Span<byte> destination = stackalloc byte[256];
            Span<int> state = stackalloc int[ProtoSafeWriterOwner.RequiredStateLength];
            var owner = new ProtoSafeWriterOwner(destination, state);
            var ownerCopy = owner;
            var writer = owner.BeginOrder("ORD"u8);
            int rejected = 0;
            try { owner.BeginOrder("DUPLICATE"u8); }
            catch (InvalidOperationException) { rejected++; }
            try { ownerCopy.BeginOrder("DUPLICATE"u8); }
            catch (InvalidOperationException) { rejected++; }

            var instrument = writer.BeginInstrument("EUR/USD"u8);
            try { writer.BeginInstrument("DUPLICATE"u8); }
            catch (InvalidOperationException) { rejected++; }
            var tail = instrument.EndInstrument('1', 1m, '1');
            try { instrument.EndInstrument('1', 1m, '1'); }
            catch (InvalidOperationException) { rejected++; }
            var broker = tail.BeginOptionalBroker("BROKER"u8);
            try { tail.BeginOptionalBroker("DUPLICATE"u8); }
            catch (InvalidOperationException) { rejected++; }
            try { tail.Finish(); }
            catch (InvalidOperationException) { rejected++; }
            var completedTail = broker.EndOptionalBroker();
            try { broker.EndOptionalBroker(); }
            catch (InvalidOperationException) { rejected++; }
            int length = completedTail.Finish();
            try { completedTail.Finish(); }
            catch (InvalidOperationException) { rejected++; }

            Assert.Equal(8, rejected);
            Assert.Equal("11=ORD\u000155=EUR/USD\u000154=1\u000138=1\u000140=1\u000176=BROKER\u0001",
                System.Text.Encoding.ASCII.GetString(destination.Slice(0, length)));
        }

        [Fact]
        public void SharedState_RejectsMetadataOverlappingTheDestination()
        {
            Span<byte> storage = stackalloc byte[64];
            Span<int> state = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(storage.Slice(0, 12));
            bool threw = false;
            try { _ = new ProtoSafeWriterOwner(storage, state); }
            catch (ArgumentException) { threw = true; }
            Assert.True(threw);
        }

        [Theory]
        [InlineData('?', 1, '1', "side")]
        [InlineData('1', 0, '1', "orderQty")]
        [InlineData('1', 1, '?', "ordType")]
        public void InvalidRequiredInputs_PoisonSharedState(char side, int quantity, char ordType, string parameter)
        {
            Span<int> state = stackalloc int[ProtoSafeWriterOwner.RequiredStateLength];
            var owner = new ProtoSafeWriterOwner(new byte[256], state);
            var instrument = owner.BeginOrder("ORD"u8).BeginInstrument("EUR/USD"u8);
            bool invalid = false;
            try { instrument.EndInstrument(side, quantity, ordType); }
            catch (ArgumentOutOfRangeException exception)
            {
                invalid = true;
                Assert.Equal(parameter, exception.ParamName);
            }
            Assert.True(invalid);
            bool poisoned = false;
            try { instrument.EndInstrument('1', 1m, '1'); }
            catch (InvalidOperationException) { poisoned = true; }
            Assert.True(poisoned);
        }

        [Fact]
        public void StackallocInput_CanBeReusedImmediatelyAfterWrite()
        {
            // Span escape/lifetime proof (docs/CONTRACT.md §12.7, issue #26 precedent): the
            // constructor copies the stackalloc'd bytes synchronously, so the caller's scratch
            // buffer can be overwritten for a second, unrelated purpose right after the call
            // returns, without corrupting the first write.
            Span<byte> destination = new byte[256];
            Span<byte> scratch = stackalloc byte[8];
            "ORD-11"u8.CopyTo(scratch);
            var writer = new ProtoOrderWriter(destination, scratch.Slice(0, 6));

            scratch.Fill((byte)'X'); // reuse the same scratch span for something else entirely
            "USD/JPY"u8.CopyTo(scratch);
            var instrumentWriter = writer.BeginInstrument(scratch.Slice(0, 7));
            int length = instrumentWriter.EndInstrument('1', 1m, '1').Finish();

            var reader = new ProtoOrderReader(destination.Slice(0, length));
            Assert.Equal("ORD-11", System.Text.Encoding.ASCII.GetString(reader.ClOrdID));
            Assert.Equal("USD/JPY", System.Text.Encoding.ASCII.GetString(reader.Instrument.Symbol));
        }

        [Fact]
        public void UnknownTag_IsSkipped_WithoutDisturbingKnownFields()
        {
            // Hand-crafted wire bytes (not produced by the writer) with an unrecognized tag
            // (999) interleaved between known fields — proves the reader's switch-with-default
            // simply skips over it (docs/CONTRACT.md §12.6).
            string raw = "11=ORD-12\u000199=vendorextension\u000155=EUR/USD\u0001";
            var bytes = System.Text.Encoding.ASCII.GetBytes(raw);

            var reader = new ProtoOrderReader(bytes);
            Assert.Equal("ORD-12", System.Text.Encoding.ASCII.GetString(reader.ClOrdID));
            Assert.Equal("EUR/USD", System.Text.Encoding.ASCII.GetString(reader.Instrument.Symbol));
        }

        [Fact]
        public void SelectiveProjection_View_ReadsOnlyRequestedFields()
        {
            Span<byte> destination = new byte[512];
            var writer = new ProtoOrderWriter(destination, "ORD-13"u8);
            var tail = writer.BeginInstrument("EUR/USD"u8).EndInstrument('1', 1m, '1');
            tail.WritePrice(9.5m);
            int length = tail.Finish();

            var view = new ProtoOrderView(destination.Slice(0, length));
            Assert.Equal("ORD-13", System.Text.Encoding.ASCII.GetString(view.ClOrdID));
            Assert.True(view.TryGetPrice(out decimal price));
            Assert.Equal(9.5m, price);
        }

        [Fact]
        public void SelectiveProjection_View_HandlesAbsentOptionalField()
        {
            Span<byte> destination = new byte[512];
            var writer = new ProtoOrderWriter(destination, "ORD-14"u8);
            var tail = writer.BeginInstrument("EUR/USD"u8).EndInstrument('1', 1m, '1');
            int length = tail.Finish(); // Price never written

            var view = new ProtoOrderView(destination.Slice(0, length));
            Assert.Equal("ORD-14", System.Text.Encoding.ASCII.GetString(view.ClOrdID));
            Assert.False(view.TryGetPrice(out _));
        }

        [Fact]
        public void ProjectionUsesFirstOccurrenceAcrossEarlyExitBoundary()
        {
            var beforeBoundary = System.Text.Encoding.ASCII.GetBytes(
                "11=FIRST\u000111=SECOND\u000144=1\u0001");
            var beforeView = new ProtoOrderView(beforeBoundary);
            Assert.Equal("FIRST", System.Text.Encoding.ASCII.GetString(beforeView.ClOrdID));

            var afterBoundary = System.Text.Encoding.ASCII.GetBytes(
                "11=FIRST\u000144=1\u000111=LATE\u000144=2\u0001");
            var view = new ProtoOrderView(afterBoundary);
            Assert.Equal("FIRST", System.Text.Encoding.ASCII.GetString(view.ClOrdID));
            Assert.True(view.TryGetPrice(out decimal projectedPrice));
            Assert.Equal(1m, projectedPrice);

            var currentFullReader = new ProtoOrderReader(afterBoundary);
            Assert.Equal("LATE", System.Text.Encoding.ASCII.GetString(currentFullReader.ClOrdID));
            Assert.True(currentFullReader.TryGetPrice(out decimal fullPrice));
            Assert.Equal(2m, fullPrice);
        }

        [Theory]
        [InlineData("453=0\u000154=1\u0001", 0)]
        [InlineData("453=1\u0001448=A\u0001448=B\u000154=1\u0001", 1)]
        [InlineData("453=2\u0001448=A\u000154=1\u0001", 1)]
        [InlineData("453=2\u0001448=A\u0001999=PARENT\u0001448=B\u0001", 1)]
        public void GroupEnumeration_UsesDeclaredCountAndMembershipBoundary(string raw, int expected)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(raw);
            var group = new ProtoPartyGroupReader(bytes);
            int actual = 0;
            foreach (var _ in group)
            {
                actual++;
            }

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void EntryCopyRemainsValidAfterEnumeratorAdvances()
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(
                "453=2\u0001448=A\u0001447=D\u0001448=B\u0001447=D\u0001");
            var enumerator = new ProtoPartyGroupReader(bytes).GetEnumerator();
            Assert.True(enumerator.MoveNext());
            var retained = enumerator.Current;
            Assert.True(enumerator.MoveNext());
            Assert.Equal("B", System.Text.Encoding.ASCII.GetString(enumerator.Current.PartyID));
            Assert.Equal("A", System.Text.Encoding.ASCII.GetString(retained.PartyID));
        }

        [Fact]
        public void NestedGroup_StaysInsideOuterEntryAndHonorsItsCount()
        {
            Span<byte> destination = new byte[512];
            var writer = new ProtoOrderWriter(destination, "ORD"u8);
            var group = writer.BeginInstrument("EUR/USD"u8)
                .EndInstrument('1', 1m, '1')
                .BeginNoPartyIDs();
            group.AddEntryWithTwoNestedParties("A"u8, "N1"u8, "N2"u8);
            int length = group.EndGroup().Finish();

            var outer = new ProtoPartyGroupReader(destination.Slice(0, length)).GetEnumerator();
            Assert.True(outer.MoveNext());
            var entry = outer.Current;

            var nested = entry.NestedParties.GetEnumerator();
            Assert.True(nested.MoveNext());
            Assert.Equal("N1", System.Text.Encoding.ASCII.GetString(nested.Current.NestedPartyID));
            Assert.True(nested.MoveNext());
            Assert.Equal("N2", System.Text.Encoding.ASCII.GetString(nested.Current.NestedPartyID));
            Assert.False(nested.MoveNext());
            Assert.False(outer.MoveNext());
        }
    }
}
